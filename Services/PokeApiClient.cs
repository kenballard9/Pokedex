// PokeApiClient.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;                    // for IOException in retry helper
using Microsoft.Extensions.Caching.Memory;
using Pokedex.Models;

namespace Pokedex.Services
{
    /// <summary>
    /// HttpClient-based implementation for calling PokéAPI (https://pokeapi.co/).
    /// Uses IMemoryCache, singleflight-style coalescing, and selective hydration for performance.
    /// </summary>
    public class PokeApiClient : IPokeApiClient
    {
        private readonly HttpClient _http;
        private readonly IMemoryCache _cache;

        private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

        // Coalesce concurrent requests for the same cache key
        private static readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _inflight = new();

        // Cache for move -> type lookups to avoid repeated network calls
        private static readonly ConcurrentDictionary<string, string?> _moveTypeCache =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly SemaphoreSlim _moveTypeGate = new(initialCount: 6, maxCount: 6);

        // TTLs (tune to taste)
        private static readonly TimeSpan TTL_Detail = TimeSpan.FromHours(12);
        private static readonly TimeSpan TTL_Lookup = TimeSpan.FromHours(24);
        private static readonly TimeSpan TTL_List = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan TTL_Count = TimeSpan.FromMinutes(30);

        public PokeApiClient(HttpClient http, IMemoryCache cache)
        {
            _http = http;
            _cache = cache;
            if (_http.BaseAddress == null)
                _http.BaseAddress = new Uri("https://pokeapi.co/api/v2/");
        }

        // ---------------- Types ----------------

        public async Task<List<string>> GetTypesAsync(CancellationToken ct = default)
        {
            // NOTE: cache key bumped to v2 to invalidate previous cached list that might include "Stellar"
            return await GetCachedAsync("types:all:v2", TTL_Lookup, async () =>
            {
                var list = new List<string>();
                using var res = await _http.GetAsync("type", ct);
                res.EnsureSuccessStatusCode();

                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (doc.RootElement.TryGetProperty("results", out var results))
                {
                    foreach (var t in results.EnumerateArray())
                    {
                        var name = t.GetProperty("name").GetString();
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        var lower = name.Trim().ToLowerInvariant();
                        // Exclude non-standard types: unknown, shadow, stellar
                        if (lower == "unknown" || lower == "shadow" || lower == "stellar") continue;

                        list.Add(Capitalize(name));
                    }
                }

                list.Sort(StringComparer.OrdinalIgnoreCase);
                return list;
            });
        }

        // ---------------- Basic lookups ----------------

        public async Task<PokemonListItem?> GetPokemonByNameAsync(string nameOrId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(nameOrId)) return null;
            var slug = nameOrId.Trim().ToLowerInvariant();

            return await GetCachedAsync<PokemonListItem?>($"pli:{slug}", TTL_Detail, async () =>
            {
                using var res = await _http.GetAsync($"pokemon/{slug}", ct);
                if (!res.IsSuccessStatusCode) return null;

                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = doc.RootElement;

                int id = root.GetProperty("id").GetInt32();
                string name = root.GetProperty("name").GetString() ?? "";
                string imageUrl = ExtractImage(root);
                var types = ExtractTypes(root);

                return new PokemonListItem
                {
                    Id = id,
                    Name = Capitalize(name),
                    ImageUrl = imageUrl,
                    Types = types
                };
            });
        }

        public async Task<List<PokemonListItem>> GetPokemonByTypeAsync(string typeName, int max = 50, CancellationToken ct = default)
        {
            var results = new List<PokemonListItem>();
            if (string.IsNullOrWhiteSpace(typeName)) return results;
            var lower = typeName.ToLowerInvariant();

            // Cache the list of names for this type
            var names = await GetCachedAsync<List<string>>($"type:names:{lower}", TTL_List, async () =>
            {
                using var res = await _http.GetAsync($"type/{lower}", ct);
                if (!res.IsSuccessStatusCode) return new List<string>();

                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!doc.RootElement.TryGetProperty("pokemon", out var pokemonList)) return new List<string>();

                return pokemonList.EnumerateArray()
                                  .Select(p => p.GetProperty("pokemon").GetProperty("name").GetString())
                                  .Where(n => !string.IsNullOrWhiteSpace(n))
                                  .Select(n => n!)
                                  .OrderBy(n => n)
                                  .ToList();
            });

            foreach (var name in names.Take(Math.Max(1, max)))
            {
                var item = await GetPokemonByNameAsync(name, ct); // cached
                if (item != null) results.Add(item);
            }

            results.Sort((a, b) => a.Id.CompareTo(b.Id));
            return results;
        }

        // ---------------- Detailed (stats + abilities + evolution [+ optional move types]) ----------------

        public Task<PokemonDetails?> GetDetailsAsync(string nameOrId, CancellationToken ct = default)
            => GetDetailsCoreAsync(nameOrId, includeMoveTypes: true, ct);   // FULL details for Details page

        private async Task<PokemonDetails?> GetDetailsCoreAsync(string nameOrId, bool includeMoveTypes, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(nameOrId)) return null;
            var slug = nameOrId.Trim().ToLowerInvariant();

            // separate cache buckets for lite/full to avoid cross-contamination
            var cacheKey = $"pdet:{slug}:{(includeMoveTypes ? "full" : "lite")}";

            return await GetCachedAsync<PokemonDetails?>(cacheKey, TTL_Detail, async () =>
            {
                // ---- /pokemon/{nameOrId} ----
                using var res = await GetWithRetriesAsync($"pokemon/{slug}", ct);
                if (!res.IsSuccessStatusCode) return null;

                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = doc.RootElement;

                var details = new PokemonDetails
                {
                    Id = root.GetProperty("id").GetInt32(),
                    Name = Capitalize(root.GetProperty("name").GetString() ?? ""),
                    ImageUrl = ExtractImage(root),
                    Types = ExtractTypes(root)
                };

                // Height / Weight
                if (root.TryGetProperty("height", out var heightEl) && heightEl.ValueKind == JsonValueKind.Number)
                    details.Height = heightEl.GetInt32();
                if (root.TryGetProperty("weight", out var weightEl) && weightEl.ValueKind == JsonValueKind.Number)
                    details.Weight = weightEl.GetInt32();

                // Base stats
                if (root.TryGetProperty("stats", out var statsEl))
                {
                    foreach (var s in statsEl.EnumerateArray())
                    {
                        var statName = s.GetProperty("stat").GetProperty("name").GetString() ?? "";
                        var value = s.GetProperty("base_stat").GetInt32();
                        switch (statName)
                        {
                            case "hp": details.HP = value; break;
                            case "attack": details.Attack = value; break;
                            case "defense": details.Defense = value; break;
                            case "special-attack": details.SpecialAttack = value; break;
                            case "special-defense": details.SpecialDefense = value; break;
                            case "speed": details.Speed = value; break;
                        }
                    }
                }

                // Ability names
                var abilityNames = new List<string>();
                if (root.TryGetProperty("abilities", out var abilitiesEl))
                {
                    foreach (var a in abilitiesEl.EnumerateArray())
                    {
                        var ability = a.GetProperty("ability").GetProperty("name").GetString();
                        if (!string.IsNullOrWhiteSpace(ability))
                            abilityNames.Add(Capitalize(ability));
                    }
                }
                details.Abilities = abilityNames;

                // MOVES (optionally include MoveType)
                var moveRows = new List<MoveLearnRow>();
                if (root.TryGetProperty("moves", out var movesEl) && movesEl.ValueKind == JsonValueKind.Array)
                {
                    if (!includeMoveTypes)
                    {
                        // Lite mode: don't fetch types; render names/levels/methods only
                        foreach (var m in movesEl.EnumerateArray())
                        {
                            var moveNameSlug = m.GetProperty("move").GetProperty("name").GetString() ?? "";
                            if (string.IsNullOrWhiteSpace(moveNameSlug)) continue;

                            var moveDisplayName = moveNameSlug.Replace("-", " ");
                            if (m.TryGetProperty("version_group_details", out var vgdArr))
                            {
                                foreach (var vgd in vgdArr.EnumerateArray())
                                {
                                    var vg = vgd.GetProperty("version_group").GetProperty("name").GetString() ?? "";
                                    var method = vgd.GetProperty("move_learn_method").GetProperty("name").GetString() ?? "";
                                    var level = vgd.TryGetProperty("level_learned_at", out var lvlEl) && lvlEl.ValueKind == JsonValueKind.Number
                                        ? lvlEl.GetInt32()
                                        : 0;

                                    moveRows.Add(new MoveLearnRow
                                    {
                                        MoveName = moveDisplayName,
                                        VersionGroup = vg,
                                        Method = method,
                                        Level = level,
                                        MoveType = null
                                    });
                                }
                            }
                        }
                    }
                    else
                    {
                        // Full mode: fetch move types (cached + concurrent)
                        var uniqueMoveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var mv in movesEl.EnumerateArray())
                        {
                            var moveName = mv.GetProperty("move").GetProperty("name").GetString();
                            if (!string.IsNullOrWhiteSpace(moveName))
                                uniqueMoveNames.Add(moveName);
                        }

                        var typeTasks = uniqueMoveNames.ToDictionary(n => n, n => GetMoveTypeAsync(n, ct));
                        await Task.WhenAll(typeTasks.Values);

                        foreach (var mv in movesEl.EnumerateArray())
                        {
                            var moveNameSlug = mv.GetProperty("move").GetProperty("name").GetString() ?? "";
                            if (string.IsNullOrWhiteSpace(moveNameSlug)) continue;

                            var moveDisplayName = moveNameSlug.Replace("-", " ");
                            var moveType = typeTasks.TryGetValue(moveNameSlug, out var task) ? task.Result : null;

                            if (mv.TryGetProperty("version_group_details", out var vgdArr))
                            {
                                foreach (var vgd in vgdArr.EnumerateArray())
                                {
                                    var vg = vgd.GetProperty("version_group").GetProperty("name").GetString() ?? "";
                                    var method = vgd.GetProperty("move_learn_method").GetProperty("name").GetString() ?? "";
                                    var level = vgd.TryGetProperty("level_learned_at", out var lvlEl) && lvlEl.ValueKind == JsonValueKind.Number
                                        ? lvlEl.GetInt32()
                                        : 0;

                                    moveRows.Add(new MoveLearnRow
                                    {
                                        MoveName = moveDisplayName,
                                        VersionGroup = vg,
                                        Method = method,
                                        Level = level,
                                        MoveType = moveType
                                    });
                                }
                            }
                        }
                    }
                }

                details.Moves = moveRows;

                // ---- Fetch extras concurrently (all cached) ----
                var abilityDefsTask = ExtractAbilityDefinitionsCachedAsync(abilityNames, ct);
                var speciesTask = GetSpeciesEntriesCachedAsync(details.Id, ct);
                var evoTask = GetEvolutionLineCachedAsync(details.Id, ct);

                details.AbilityDetails = await abilityDefsTask;
                details.PokedexEntries = await speciesTask;
                details.EvolutionLine = await evoTask;

                return details;
            });
        }

        private async Task<List<AbilityDefinition>> ExtractAbilityDefinitionsCachedAsync(List<string> displayNames, CancellationToken ct)
        {
            var result = new List<AbilityDefinition>();
            if (displayNames == null || displayNames.Count == 0) return result;

            var tasks = displayNames.Select(async displayName =>
            {
                var slug = displayName.ToLowerInvariant().Replace(' ', '-');
                return await GetCachedAsync<AbilityDefinition?>($"ability:{slug}", TTL_Lookup, async () =>
                {
                    using var res = await GetWithRetriesAsync($"ability/{slug}", ct);
                    if (!res.IsSuccessStatusCode) return null;

                    await using var stream = await res.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    var root = doc.RootElement;

                    string name = Capitalize(root.GetProperty("name").GetString() ?? displayName);
                    string? effect = null, shortEffect = null;

                    if (root.TryGetProperty("effect_entries", out var effs))
                    {
                        foreach (var e in effs.EnumerateArray())
                        {
                            var lang = e.GetProperty("language").GetProperty("name").GetString();
                            if (lang == "en")
                            {
                                effect = CleanFlavor(e.GetProperty("effect").GetString());
                                shortEffect = CleanFlavor(e.GetProperty("short_effect").GetString());
                                break;
                            }
                        }
                    }

                    return new AbilityDefinition { Name = name, Effect = effect, ShortEffect = shortEffect };
                });
            });

            var defs = await Task.WhenAll(tasks);
            foreach (var d in defs)
                if (d != null) result.Add(d);

            return result;
        }

        private Task<List<PokedexEntry>> GetSpeciesEntriesCachedAsync(int id, CancellationToken ct)
        {
            return GetCachedAsync<List<PokedexEntry>>($"species:{id}", TTL_Lookup, async () =>
            {
                var entries = new List<PokedexEntry>();

                using var res = await GetWithRetriesAsync($"pokemon-species/{id}", ct);
                if (!res.IsSuccessStatusCode) return entries;

                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var root = doc.RootElement;

                if (root.TryGetProperty("flavor_text_entries", out var flavs))
                {
                    foreach (var f in flavs.EnumerateArray())
                    {
                        var lang = f.GetProperty("language").GetProperty("name").GetString();
                        if (lang != "en") continue;

                        var text = CleanFlavor(f.GetProperty("flavor_text").GetString());
                        var version = f.GetProperty("version").GetProperty("name").GetString();

                        if (string.IsNullOrWhiteSpace(text)) continue;

                        entries.Add(new PokedexEntry
                        {
                            Text = text,
                            Version = Capitalize(version ?? "")
                        });
                    }
                }

                // Deduplicate and limit a bit
                return entries
                    .GroupBy(e => (e.Version, e.Text))
                    .Select(g => g.First())
                    .Take(40)
                    .ToList();
            });
        }

        // ---------------- Paging ----------------

        public async Task<int> GetTotalPokemonCountAsync(CancellationToken ct = default)
        {
            return await GetCachedAsync<int>("pokemon:count", TTL_Count, async () =>
            {
                using var res = await _http.GetAsync("pokemon?limit=1&offset=0", ct);
                res.EnsureSuccessStatusCode();

                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                return doc.RootElement.GetProperty("count").GetInt32();
            });
        }

        /// <summary>Returns just the Pokémon IDs for the given page.</summary>
        public async Task<List<int>> GetPokemonIdsForPageAsync(int page, int pageSize, CancellationToken ct = default)
        {
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 20;

            return await GetCachedAsync<List<int>>($"page:ids:{page}:{pageSize}", TTL_List, async () =>
            {
                int offset = (page - 1) * pageSize;
                using var res = await _http.GetAsync($"pokemon?limit={pageSize}&offset={offset}", ct);
                if (!res.IsSuccessStatusCode) return new List<int>();

                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (!doc.RootElement.TryGetProperty("results", out var results)) return new List<int>();

                var ids = new List<int>();
                foreach (var item in results.EnumerateArray())
                {
                    var url = item.GetProperty("url").GetString();
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    var parts = url.TrimEnd('/').Split('/');
                    if (int.TryParse(parts.LastOrDefault(), out var id)) ids.Add(id);
                }
                return ids;
            });
        }

        public async Task<List<PokemonDetails>> GetPageAsync(int page, int pageSize, CancellationToken ct = default)
        {
            var list = new List<PokemonDetails>();
            var ids = await GetPokemonIdsForPageAsync(page, pageSize, ct);

            // Hydrate "LITE" details (no move types) for grid speed
            var tasks = ids.Select(id => GetDetailsCoreAsync(id.ToString(), includeMoveTypes: false, ct));
            var dtos = await Task.WhenAll(tasks);
            foreach (var d in dtos)
                if (d != null) list.Add(d);

            return list.OrderBy(d => d.Id).ToList();
        }

        public async Task<List<PokemonDetails>> GetByTypePageAsync(string typeName, int page, int pageSize, CancellationToken ct = default)
        {
            var details = new List<PokemonDetails>();
            if (string.IsNullOrWhiteSpace(typeName)) return details;

            var lower = typeName.ToLowerInvariant();

            var names = await GetCachedAsync<List<string>>($"type:names:{lower}", TTL_List, async () =>
            {
                using var res = await _http.GetAsync($"type/{lower}", ct);
                if (!res.IsSuccessStatusCode) return new List<string>();

                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!doc.RootElement.TryGetProperty("pokemon", out var pokemonList)) return new List<string>();

                return pokemonList.EnumerateArray()
                                  .Select(p => p.GetProperty("pokemon").GetProperty("name").GetString())
                                  .Where(n => !string.IsNullOrWhiteSpace(n))
                                  .Select(n => n!)
                                  .OrderBy(n => n)
                                  .ToList();
            });

            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 20;

            var pageNames = names.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            // Hydrate "LITE" details (no move types) for grid speed
            var tasks = pageNames.Select(n => GetDetailsCoreAsync(n, includeMoveTypes: false, ct));
            var dtos = await Task.WhenAll(tasks);
            foreach (var d in dtos)
                if (d != null) details.Add(d);

            return details.OrderBy(d => d.Id).ToList();
        }

        // ---------------- Suggestions ----------------

        public async Task<List<string>> GetAllNamesAsync(CancellationToken ct = default)
        {
            return await GetCachedAsync("names:all", TTL_Lookup, async () =>
            {
                using var res = await _http.GetAsync("pokemon?limit=20000&offset=0", ct);
                res.EnsureSuccessStatusCode();

                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var names = new List<string>();
                foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
                {
                    var name = r.GetProperty("name").GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                }
                names.Sort(StringComparer.Ordinal);
                return names;
            });
        }

        public async Task<List<string>> SuggestAsync(string term, int max = 10, CancellationToken ct = default)
        {
            var all = await GetAllNamesAsync(ct);
            term ??= "";
            term = term.Trim().ToLowerInvariant();
            if (term.Length == 0) return new List<string>();

            return all.Where(n => n.StartsWith(term, StringComparison.OrdinalIgnoreCase))
                      .Take(Math.Max(1, max))
                      .Select(Capitalize)
                      .ToList();
        }

        // ---------------- Type counts ----------------

        public async Task<int> GetTypeCountAsync(string typeName, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return 0;
            var lower = typeName.ToLowerInvariant();

            // Count derived from cached names list for consistency/speed
            var names = await GetCachedAsync<List<string>>($"type:names:{lower}", TTL_List, async () =>
            {
                using var res = await _http.GetAsync($"type/{lower}", ct);
                if (!res.IsSuccessStatusCode) return new List<string>();

                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (!doc.RootElement.TryGetProperty("pokemon", out var pokemonList)) return new List<string>();

                return pokemonList.EnumerateArray()
                                  .Select(p => p.GetProperty("pokemon").GetProperty("name").GetString())
                                  .Where(n => !string.IsNullOrWhiteSpace(n))
                                  .Select(n => n!)
                                  .ToList();
            });

            return names.Count;
        }

        // ---------------- helpers ----------------

        private static string ExtractImage(JsonElement root)
        {
            string imageUrl = "";
            if (root.TryGetProperty("sprites", out var sprites))
            {
                if (sprites.TryGetProperty("other", out var other) &&
                    other.TryGetProperty("official-artwork", out var art) &&
                    art.TryGetProperty("front_default", out var official) &&
                    official.ValueKind == JsonValueKind.String)
                {
                    imageUrl = official.GetString() ?? "";
                }
                if (string.IsNullOrEmpty(imageUrl) &&
                    sprites.TryGetProperty("front_default", out var front) &&
                    front.ValueKind == JsonValueKind.String)
                {
                    imageUrl = front.GetString() ?? "";
                }
            }
            return imageUrl;
        }

        private static List<string> ExtractTypes(JsonElement root)
        {
            var types = new List<string>();
            if (root.TryGetProperty("types", out var typesEl))
            {
                foreach (var t in typesEl.EnumerateArray())
                {
                    var typeName = t.GetProperty("type").GetProperty("name").GetString();
                    if (!string.IsNullOrWhiteSpace(typeName))
                        types.Add(Capitalize(typeName));
                }
            }
            return types;
        }

        private static string? CleanFlavor(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            return s.Replace('\n', ' ')
                    .Replace('\f', ' ')
                    .Replace("\r", " ")
                    .Trim();
        }

        private static string Capitalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        private static int ExtractIdFromUrl(string url)
        {
            var parts = url.TrimEnd('/').Split('/');
            return int.TryParse(parts.LastOrDefault(), out var id) ? id : 0;
        }

        private static string ArtworkUrlForId(int id) =>
            $"https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/other/official-artwork/{id}.png";

        private Task<List<PokemonListItem>> GetEvolutionLineCachedAsync(int speciesOrPokemonId, CancellationToken ct)
        {
            return GetCachedAsync<List<PokemonListItem>>($"evo:{speciesOrPokemonId}", TTL_Lookup, async () =>
            {
                // 1) species -> evolution_chain URL
                using var res1 = await GetWithRetriesAsync($"pokemon-species/{speciesOrPokemonId}", ct);
                if (!res1.IsSuccessStatusCode) return new List<PokemonListItem>();
                await using var s1 = await res1.Content.ReadAsStreamAsync(ct);
                using var d1 = await JsonDocument.ParseAsync(s1, cancellationToken: ct);
                if (!d1.RootElement.TryGetProperty("evolution_chain", out var evoObj)) return new List<PokemonListItem>();
                var evoUrl = evoObj.GetProperty("url").GetString() ?? "";
                var chainId = ExtractIdFromUrl(evoUrl);
                if (chainId <= 0) return new List<PokemonListItem>();

                // 2) evolution-chain/{id}
                using var res2 = await GetWithRetriesAsync($"evolution-chain/{chainId}", ct);
                if (!res2.IsSuccessStatusCode) return new List<PokemonListItem>();
                await using var s2 = await res2.Content.ReadAsStreamAsync(ct);
                using var d2 = await JsonDocument.ParseAsync(s2, cancellationToken: ct);

                var list = new List<PokemonListItem>();
                void Walk(JsonElement node)
                {
                    var sp = node.GetProperty("species");
                    var spName = sp.GetProperty("name").GetString() ?? "";
                    var spUrl = sp.GetProperty("url").GetString() ?? "";
                    var id = ExtractIdFromUrl(spUrl);
                    if (id > 0 && !list.Any(x => x.Id == id))
                    {
                        list.Add(new PokemonListItem
                        {
                            Id = id,
                            Name = char.ToUpperInvariant(spName[0]) + spName.Substring(1),
                            ImageUrl = ArtworkUrlForId(id)
                        });
                    }

                    if (node.TryGetProperty("evolves_to", out var arr))
                    {
                        foreach (var child in arr.EnumerateArray())
                            Walk(child);
                    }
                }

                Walk(d2.RootElement.GetProperty("chain"));
                return list;
            });
        }

        /// <summary>
        /// Generic cache helper with singleflight.
        /// </summary>
        private async Task<T> GetCachedAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory)
        {
            if (_cache.TryGetValue(key, out T cached))
                return cached;

            var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<object?>>(async () =>
            {
                var value = await factory();
                _cache.Set(key, value!, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = ttl,
                    Size = 1
                });
                return (object?)value!;
            }));

            try
            {
                var obj = await lazy.Value;
                return (T)obj!;
            }
            finally
            {
                _inflight.TryRemove(key, out _);
            }
        }

        // ============= Move type lookup (cached) ===================
        // at top of the class (with other statics

        // ...

        private async Task<string?> GetMoveTypeAsync(string moveName, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(moveName)) return null;

            if (_moveTypeCache.TryGetValue(moveName, out var cached))
                return cached;

            var slug = moveName.Trim().ToLowerInvariant();

            await _moveTypeGate.WaitAsync(ct); // throttle to avoid 429s
            try
            {
                using var res = await GetWithRetriesAsync($"move/{slug}", ct);
                if (!res.IsSuccessStatusCode)
                {
                    _moveTypeCache[moveName] = null; // cache negative to avoid refetch storms
                    return null;
                }

                await using var s = await res.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);

                string? typeName = null;
                if (doc.RootElement.TryGetProperty("type", out var typeObj) &&
                    typeObj.TryGetProperty("name", out var typeNameEl) &&
                    typeNameEl.ValueKind == JsonValueKind.String)
                {
                    typeName = typeNameEl.GetString(); // e.g., "fire"
                }

                _moveTypeCache[moveName] = typeName; // cache even null
                return typeName;
            }
            finally
            {
                _moveTypeGate.Release();
            }
        }



        // ===========================================================

        // === Interface wrappers (no CancellationToken) ================================
        public Task<List<string>> GetTypesAsync()
            => GetTypesAsync(default);

        public Task<PokemonListItem?> GetPokemonByNameAsync(string nameOrId)
            => GetPokemonByNameAsync(nameOrId, default);

        public Task<List<PokemonListItem>> GetPokemonByTypeAsync(string typeName, int max = 50)
            => GetPokemonByTypeAsync(typeName, max, default);

        public Task<PokemonDetails?> GetDetailsAsync(string nameOrId)
            => GetDetailsAsync(nameOrId, default);

        public Task<int> GetTotalPokemonCountAsync()
            => GetTotalPokemonCountAsync(default);

        public Task<List<PokemonDetails>> GetPageAsync(int page, int pageSize)
            => GetPageAsync(page, pageSize, default);

        public Task<List<PokemonDetails>> GetByTypePageAsync(string typeName, int page, int pageSize)
            => GetByTypePageAsync(typeName, page, pageSize, default);

        public Task<List<string>> GetAllNamesAsync()
            => GetAllNamesAsync(default);

        public Task<List<string>> SuggestAsync(string term, int max = 10)
            => SuggestAsync(term, max, default);

        public Task<int> GetTypeCountAsync(string typeName)
            => GetTypeCountAsync(typeName, default);
        // ============================================================================

        // =================== RETRY HELPER ===========================================
        // Retries GETs on transient failures (429/5xx, connection resets, timeouts) with backoff + jitter.
        private async Task<HttpResponseMessage> GetWithRetriesAsync(string relativeUrl, CancellationToken ct)
        {
            const int maxAttempts = 4;

            TimeSpan ComputeDelay(int attempt, HttpResponseMessage? res = null)
            {
                if (res?.Headers?.RetryAfter != null)
                {
                    var ra = res.Headers.RetryAfter;
                    if (ra.Delta.HasValue) return ra.Delta.Value;
                    if (ra.Date.HasValue)
                    {
                        var delta = ra.Date.Value - DateTimeOffset.UtcNow;
                        if (delta > TimeSpan.Zero) return delta;
                    }
                }

                var baseMs = 250 * Math.Pow(2, attempt - 1); // 250, 500, 1000, 2000
                var jitterMs = Random.Shared.Next(50, 200);
                return TimeSpan.FromMilliseconds(baseMs + jitterMs);
            }

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var res = await _http.GetAsync(relativeUrl, ct);

                    if ((int)res.StatusCode == 429 || (int)res.StatusCode >= 500)
                    {
                        if (attempt == maxAttempts) return res;
                        var delay = ComputeDelay(attempt, res);
                        res.Dispose();
                        await Task.Delay(delay, ct);
                        continue;
                    }

                    return res;
                }
                catch (HttpRequestException) when (attempt < maxAttempts)
                {
                    await Task.Delay(ComputeDelay(attempt), ct);
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    await Task.Delay(ComputeDelay(attempt), ct);
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < maxAttempts)
                {
                    await Task.Delay(ComputeDelay(attempt), ct);
                }
            }

            return await _http.GetAsync(relativeUrl, ct);
        }
        // ============================================================================

        // Satisfy interface member
        public async Task<PokeApiPokemon?> GetPokemonAsync(string idOrName, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(idOrName)) return null;

            var endpoint = $"pokemon/{idOrName.ToLowerInvariant()}";
            using var resp = await GetWithRetriesAsync(endpoint, ct);
            if (!resp.IsSuccessStatusCode) return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var data = await JsonSerializer.DeserializeAsync<PokeApiPokemon>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                ct
            );
            return data;
        }
    }
}
