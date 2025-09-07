using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pokedex.Models;

namespace Pokedex.Services
{
    /// <summary>
    /// HttpClient-based implementation for calling PokéAPI (https://pokeapi.co/).
    /// Provides stats, abilities, paging, and autocomplete suggestions.
    /// </summary>
    public class PokeApiClient : IPokeApiClient
    {
        private readonly HttpClient _http;
        private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

        // simple in-memory cache of all names for suggestions
        private static List<string>? _allNamesCache;
        private static int? _totalCountCache;
        private static readonly SemaphoreSlim _nameLock = new(1, 1);

        public PokeApiClient(HttpClient http)
        {
            _http = http;
            if (_http.BaseAddress == null)
                _http.BaseAddress = new Uri("https://pokeapi.co/api/v2/");
        }

        // ---------------- Types ----------------

        public async Task<List<string>> GetTypesAsync()
        {
            var list = new List<string>();
            using var res = await _http.GetAsync("type");
            res.EnsureSuccessStatusCode();

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (doc.RootElement.TryGetProperty("results", out var results))
            {
                foreach (var t in results.EnumerateArray())
                {
                    var name = t.GetProperty("name").GetString();
                    if (!string.IsNullOrWhiteSpace(name) && name != "unknown" && name != "shadow")
                        list.Add(Capitalize(name));
                }
            }

            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        // ---------------- Basic (existing) ----------------

        public async Task<PokemonListItem?> GetPokemonByNameAsync(string nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId)) return null;

            try
            {
                using var res = await _http.GetAsync($"pokemon/{nameOrId.ToLower()}");
                if (!res.IsSuccessStatusCode) return null;

                using var stream = await res.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var root = doc.RootElement;

                int id = root.GetProperty("id").GetInt32();
                string name = root.GetProperty("name").GetString() ?? "";
                string imageUrl = ExtractImage(root);

                var types = new List<string>();
                if (root.TryGetProperty("types", out var typesEl))
                {
                    foreach (var t in typesEl.EnumerateArray())
                        types.Add(Capitalize(t.GetProperty("type").GetProperty("name").GetString() ?? ""));
                }

                return new PokemonListItem
                {
                    Id = id,
                    Name = Capitalize(name),
                    ImageUrl = imageUrl,
                    Types = types
                };
            }
            catch
            {
                return null;
            }
        }

        public async Task<List<PokemonListItem>> GetPokemonByTypeAsync(string typeName, int max = 50)
        {
            var results = new List<PokemonListItem>();
            if (string.IsNullOrWhiteSpace(typeName)) return results;

            using var res = await _http.GetAsync($"type/{typeName.ToLower()}");
            if (!res.IsSuccessStatusCode) return results;

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("pokemon", out var pokemonList)) return results;

            int count = 0;
            foreach (var p in pokemonList.EnumerateArray())
            {
                var name = p.GetProperty("pokemon").GetProperty("name").GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var item = await GetPokemonByNameAsync(name);
                    if (item != null) results.Add(item);

                    count++;
                    if (count >= max) break;
                }
            }

            results.Sort((a, b) => a.Id.CompareTo(b.Id));
            return results;
        }

        // ---------------- Detailed (stats + abilities) ----------------

        public async Task<PokemonDetails?> GetDetailsAsync(string nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId)) return null;

            using var res = await _http.GetAsync($"pokemon/{nameOrId.ToLower()}");
            if (!res.IsSuccessStatusCode) return null;

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            var details = new PokemonDetails
            {
                Id = root.GetProperty("id").GetInt32(),
                Name = Capitalize(root.GetProperty("name").GetString() ?? ""),
                ImageUrl = ExtractImage(root),
                Types = ExtractTypes(root)
            };

            // stats
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

            // abilities
            var abilities = new List<string>();
            if (root.TryGetProperty("abilities", out var abilitiesEl))
            {
                foreach (var a in abilitiesEl.EnumerateArray())
                {
                    var ability = a.GetProperty("ability").GetProperty("name").GetString();
                    if (!string.IsNullOrWhiteSpace(ability))
                        abilities.Add(Capitalize(ability));
                }
            }
            details.Abilities = abilities;

            return details;
        }

        // ---------------- Paging ----------------

        public async Task<int> GetTotalPokemonCountAsync()
        {
            if (_totalCountCache.HasValue) return _totalCountCache.Value;

            using var res = await _http.GetAsync("pokemon?limit=1&offset=0");
            res.EnsureSuccessStatusCode();
            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            _totalCountCache = doc.RootElement.GetProperty("count").GetInt32();
            return _totalCountCache.Value;
        }

        public async Task<List<PokemonDetails>> GetPageAsync(int page, int pageSize)
        {
            var list = new List<PokemonDetails>();
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 20;

            int offset = (page - 1) * pageSize;
            using var res = await _http.GetAsync($"pokemon?limit={pageSize}&offset={offset}");
            if (!res.IsSuccessStatusCode) return list;

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("results", out var results)) return list;

            // Get details for each name
            foreach (var item in results.EnumerateArray())
            {
                var name = item.GetProperty("name").GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var d = await GetDetailsAsync(name);
                    if (d != null) list.Add(d);
                }
            }

            return list.OrderBy(d => d.Id).ToList();
        }

        public async Task<List<PokemonDetails>> GetByTypePageAsync(string typeName, int page, int pageSize)
        {
            var details = new List<PokemonDetails>();
            if (string.IsNullOrWhiteSpace(typeName)) return details;

            // First, get all names for the type (uncapped)
            using var res = await _http.GetAsync($"type/{typeName.ToLower()}");
            if (!res.IsSuccessStatusCode) return details;

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            if (!doc.RootElement.TryGetProperty("pokemon", out var pokemonList)) return details;

            var names = pokemonList.EnumerateArray()
                                   .Select(p => p.GetProperty("pokemon").GetProperty("name").GetString())
                                   .Where(n => !string.IsNullOrWhiteSpace(n))
                                   .Select(n => n!)
                                   .OrderBy(n => n)
                                   .ToList();

            // page over those names
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 20;

            var pageNames = names.Skip((page - 1) * pageSize).Take(pageSize);

            foreach (var n in pageNames)
            {
                var d = await GetDetailsAsync(n);
                if (d != null) details.Add(d);
            }

            return details.OrderBy(d => d.Id).ToList();
        }

        // ---------------- Suggestions ----------------

        public async Task<List<string>> GetAllNamesAsync()
        {
            if (_allNamesCache != null) return _allNamesCache;

            await _nameLock.WaitAsync();
            try
            {
                if (_allNamesCache != null) return _allNamesCache;

                // Grab all names once (PokéAPI supports high limit)
                using var res = await _http.GetAsync("pokemon?limit=20000&offset=0");
                res.EnsureSuccessStatusCode();
                using var stream = await res.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);

                var names = new List<string>();
                foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
                {
                    var name = r.GetProperty("name").GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                        names.Add(name);
                }
                _allNamesCache = names.OrderBy(n => n).ToList();
                return _allNamesCache;
            }
            finally
            {
                _nameLock.Release();
            }
        }

        public async Task<List<string>> SuggestAsync(string term, int max = 10)
        {
            var all = await GetAllNamesAsync();
            term ??= "";
            term = term.Trim().ToLowerInvariant();
            if (term.Length == 0) return new List<string>();

            return all.Where(n => n.StartsWith(term, StringComparison.OrdinalIgnoreCase))
                      .Take(max)
                      .Select(Capitalize)
                      .ToList();
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

        private static string Capitalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            return char.ToUpper(s[0]) + s.Substring(1);
        }

        public async Task<int> GetTypeCountAsync(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return 0;

            using var res = await _http.GetAsync($"type/{typeName.ToLower()}");
            if (!res.IsSuccessStatusCode) return 0;

            using var stream = await res.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (!doc.RootElement.TryGetProperty("pokemon", out var pokemonList))
                return 0;

            // the "pokemon" array contains entries for each species in that type
            return pokemonList.GetArrayLength();
        }

    }
}
