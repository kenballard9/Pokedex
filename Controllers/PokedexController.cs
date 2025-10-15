using Microsoft.AspNetCore.Mvc;
using Pokedex.Models;
using Pokedex.Services;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System;
using System.Net.Http;
using System.Text.Json;

namespace Pokedex.Controllers
{
    public class PokedexController : Controller
    {
        private readonly IPokeApiClient _client;

        // Shared HttpClient for fallback fetch
        private static readonly HttpClient s_http = new HttpClient
        {
            BaseAddress = new Uri("https://pokeapi.co/api/v2/")
        };

        public PokedexController(IPokeApiClient client)
        {
            _client = client;
        }

        // Back-compat: if anything hits /Pokedex/Index, send it to Home/Index.
        [HttpGet]
        public IActionResult Index(string? searchName, string? selectedType, int page = 1, int pageSize = 20)
        {
            return RedirectToAction("Index", "Home", new { searchName, selectedType, page, pageSize });
        }

        /// JSON endpoint for autocomplete (keep if your JS points here)
        [HttpGet]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> Suggest(string term, int max = 10)
        {
            var items = await _client.SuggestAsync(term, max);
            return Json(items);
        }

        [HttpGet]
        public async Task<IActionResult> Details(string id, string? returnUrl, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(id)) return NotFound();

            // Allow the view to render a "Back to Pokédex" link
            ViewBag.ReturnUrl = returnUrl;

            // ✅ FULL details path: includes move types (chips)
            var details = await _client.GetDetailsAsync(id);
            if (details == null) return NotFound();

            // If client already provided moves (with MoveType), DO NOT overwrite them.
            // Only fall back to our local loaders if moves are missing entirely.
            if (details.Moves == null || details.Moves.Count == 0)
            {
                var moveRows = await TryMapMovesFromClientAsync(id, ct);

                if (moveRows.Count == 0)
                {
                    moveRows = await TryGetMovesViaFallbackAsync(id, ct);
                }

                // Assign only when we had nothing, so we don't lose MoveType.
                details.Moves = moveRows;
            }

            // Sort for display (preserves MoveType on each row)
            if (details.Moves != null && details.Moves.Count > 0)
            {
                details.Moves = details.Moves
                    .OrderBy(x => string.Equals(x.Method, "level-up", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(x => x.Level == 0 ? int.MaxValue : x.Level)
                    .ThenBy(x => x.MoveName)
                    .ToList();
            }

            return View(details);
        }

        /// <summary>
        /// Attempts to read moves using your IPokeApiClient.GetPokemonAsync(id).
        /// Uses strong typing: PokeApiPokemon.Moves (PascalCase).
        /// Returns an empty list if data isn't present.
        /// </summary>
        private async Task<List<MoveLearnRow>> TryMapMovesFromClientAsync(string id, CancellationToken ct)
        {
            var rows = new List<MoveLearnRow>();

            try
            {
                var pokemon = await _client.GetPokemonAsync(id, ct);
                if (pokemon == null || pokemon.Moves == null) return rows;

                foreach (var m in pokemon.Moves)
                {
                    var moveName = m?.Move?.Name ?? "";
                    if (string.IsNullOrWhiteSpace(moveName)) continue;

                    foreach (var v in m.VersionGroupDetails ?? Enumerable.Empty<PokeApiVersionGroupDetail>())
                    {
                        rows.Add(new MoveLearnRow
                        {
                            MoveName = moveName,
                            Level = v.LevelLearnedAt,                    // 0 for TM/tutor/egg
                            Method = v.MoveLearnMethod?.Name ?? "",
                            VersionGroup = v.VersionGroup?.Name ?? ""
                            // NOTE: This fallback does NOT include MoveType.
                            // We only use it when the client returned no moves at all.
                        });
                    }
                }
            }
            catch
            {
                // swallow; we'll try fallback
            }

            return rows;
        }

        /// <summary>
        /// Directly fetches https://pokeapi.co/api/v2/pokemon/{id-or-name} and parses moves.
        /// Uses System.Text.Json and no extra DTOs (just for fallback).
        /// </summary>
        private static async Task<List<MoveLearnRow>> TryGetMovesViaFallbackAsync(string idOrName, CancellationToken ct)
        {
            var rows = new List<MoveLearnRow>();

            try
            {
                var path = $"pokemon/{idOrName.ToLowerInvariant()}";
                using var resp = await s_http.GetAsync(path, ct);
                if (!resp.IsSuccessStatusCode) return rows;

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                if (!doc.RootElement.TryGetProperty("moves", out var movesElem) || movesElem.ValueKind != JsonValueKind.Array)
                    return rows;

                foreach (var move in movesElem.EnumerateArray())
                {
                    string moveName = "";
                    if (move.TryGetProperty("move", out var moveObj) &&
                        moveObj.TryGetProperty("name", out var moveNameElem) &&
                        moveNameElem.ValueKind == JsonValueKind.String)
                    {
                        moveName = moveNameElem.GetString() ?? "";
                    }
                    if (string.IsNullOrWhiteSpace(moveName)) continue;

                    if (!move.TryGetProperty("version_group_details", out var vgdElem) || vgdElem.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var v in vgdElem.EnumerateArray())
                    {
                        int level = 0;
                        string method = "";
                        string version = "";

                        if (v.TryGetProperty("level_learned_at", out var lvlElem) && lvlElem.TryGetInt32(out var lvl))
                            level = lvl;

                        if (v.TryGetProperty("move_learn_method", out var mlm) &&
                            mlm.TryGetProperty("name", out var mlmName) &&
                            mlmName.ValueKind == JsonValueKind.String)
                        {
                            method = mlmName.GetString() ?? "";
                        }

                        if (v.TryGetProperty("version_group", out var vg) &&
                            vg.TryGetProperty("name", out var vgName) &&
                            vgName.ValueKind == JsonValueKind.String)
                        {
                            version = vgName.GetString() ?? "";
                        }

                        rows.Add(new MoveLearnRow
                        {
                            MoveName = moveName,
                            Level = level,
                            Method = method,
                            VersionGroup = version
                            // NOTE: This raw fallback does NOT include MoveType.
                        });
                    }
                }
            }
            catch
            {
                // ignore; return whatever we have (likely empty)
            }

            return rows;
        }
    }
}
