using Microsoft.AspNetCore.Mvc;
using Pokedex.Models;
using Pokedex.Services;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Pokedex.Controllers
{
    public class HomeController : Controller
    {
        private readonly IPokeApiClient _client;

        public HomeController(IPokeApiClient client)
        {
            _client = client;
        }

        // Optional: root -> Home/Index
        [HttpGet("/")]
        public IActionResult Root() => RedirectToAction(nameof(Index));

        // Home/Index with search + type filter + paging
        [HttpGet("/Home/Index")]
        public async Task<IActionResult> Index(string? searchName, string? selectedType, int page = 1, int pageSize = 20)
        {
            var vm = new PokedexIndexViewModel
            {
                SearchName = searchName,
                SelectedType = selectedType,
                AllTypes = await _client.GetTypesAsync(), // your client already excludes non-standard types
                Page = page < 1 ? 1 : page,
                PageSize = pageSize < 1 ? 20 : pageSize
            };

            var term = (vm.SearchName ?? "").Trim();
            var hasTerm = term.Length > 0;
            var hasType = !string.IsNullOrWhiteSpace(vm.SelectedType);

            // ------------------------------
            // A) TYPE SELECTED (and maybe name)
            // -> Filter inside the type pool, then ORDER BY ID, then page
            // ------------------------------
            if (hasType)
            {
                // Pull the full pool for the selected type (cached in client)
                var allInType = await _client.GetPokemonByTypeAsync(vm.SelectedType!, max: 20000);

                // Filter by name text (if any), then sort by Id ASC
                var filteredById = allInType
                    .Where(p => !hasTerm || p.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.Id)
                    .ToList();

                vm.TotalCount = filteredById.Count;

                var totalPages = Math.Max(1, (int)Math.Ceiling(vm.TotalCount / (double)vm.PageSize));
                vm.Page = Math.Min(Math.Max(1, vm.Page), totalPages);

                // Page by ID order
                var pageItems = filteredById
                    .Skip((vm.Page - 1) * vm.PageSize)
                    .Take(vm.PageSize)
                    .ToList();

                // Hydrate to details for the page subset, and ensure final stable order by Id
                var detailsTasks = pageItems.Select(i => _client.GetDetailsAsync(i.Id.ToString()));
                var details = await Task.WhenAll(detailsTasks);
                vm.Results = details.Where(d => d != null)!.OrderBy(d => d!.Id).ToList()!;

                return View("~/Views/Home/Index.cshtml", vm);
            }

            // ------------------------------
            // B) NAME ONLY (no type)
            // -> find candidates by contains, resolve their Ids, ORDER BY ID, then page
            // ------------------------------
            if (hasTerm)
            {
                // Try exact (or ID) first — quick path
                var exact = await _client.GetDetailsAsync(term);
                if (exact != null)
                {
                    vm.TotalCount = 1;
                    vm.Page = 1; vm.PageSize = 1;
                    vm.Results = new List<PokemonDetails> { exact };
                    return View("~/Views/Home/Index.cshtml", vm);
                }

                // Get all names and filter by contains
                var allNames = await _client.GetAllNamesAsync();
                var nameMatches = allNames
                    .Where(n => n.Contains(term, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // Resolve each candidate's Id via lightweight lookup (cached),
                // then sort by Id ASC BEFORE paging
                var liteTasks = nameMatches.Select(n => _client.GetPokemonByNameAsync(n));
                var liteResults = await Task.WhenAll(liteTasks);
                var orderedById = liteResults
                    .Where(x => x != null)
                    .OrderBy(x => x!.Id)
                    .ToList()!;

                vm.TotalCount = orderedById.Count;

                var totalPages = Math.Max(1, (int)Math.Ceiling(vm.TotalCount / (double)vm.PageSize));
                vm.Page = Math.Min(Math.Max(1, vm.Page), totalPages);

                // Page by Id order
                var pageItems = orderedById
                    .Skip((vm.Page - 1) * vm.PageSize)
                    .Take(vm.PageSize)
                    .ToList();

                // Hydrate details for the page subset; keep Id order
                var detailsTasks = pageItems.Select(i => _client.GetDetailsAsync(i!.Id.ToString()));
                var details = await Task.WhenAll(detailsTasks);
                vm.Results = details.Where(d => d != null)!.OrderBy(d => d!.Id).ToList()!;

                return View("~/Views/Home/Index.cshtml", vm);
            }

            // ------------------------------
            // C) NO FILTERS -> default paging (already by Id)
            // ------------------------------
            vm.TotalCount = await _client.GetTotalPokemonCountAsync();
            var totalPagesAll = Math.Max(1, (int)Math.Ceiling(vm.TotalCount / (double)vm.PageSize));
            vm.Page = Math.Min(Math.Max(1, vm.Page), totalPagesAll);

            vm.Results = await _client.GetPageAsync(vm.Page, vm.PageSize);
            return View("~/Views/Home/Index.cshtml", vm);
        }
    }
}
