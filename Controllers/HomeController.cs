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
                AllTypes = await _client.GetTypesAsync(), // make sure your GetTypesAsync excludes "stellar"
                Page = page < 1 ? 1 : page,
                PageSize = pageSize < 1 ? 20 : pageSize
            };

            var term = (vm.SearchName ?? "").Trim();
            var hasTerm = term.Length > 0;
            var hasType = !string.IsNullOrWhiteSpace(vm.SelectedType);

            // ------------------------------
            // A) TYPE SELECTED (and maybe name)
            // -> pull that type's pool, then apply name filter (Contains, case-insensitive), then page
            // ------------------------------
            if (hasType)
            {
                // large cap; this call is cached in your client
                var allInType = await _client.GetPokemonByTypeAsync(vm.SelectedType!, max: 20000);

                var filtered = allInType
                    .Where(p => !hasTerm || p.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n)
                    .ToList();

                vm.TotalCount = filtered.Count;

                var totalPages = Math.Max(1, (int)Math.Ceiling(vm.TotalCount / (double)vm.PageSize));
                vm.Page = Math.Min(Math.Max(1, vm.Page), totalPages);

                var pageNames = filtered
                    .Skip((vm.Page - 1) * vm.PageSize)
                    .Take(vm.PageSize)
                    .ToList();

                var details = await Task.WhenAll(pageNames.Select(n => _client.GetDetailsAsync(n)));
                vm.Results = details.Where(d => d != null)!.OrderBy(d => d!.Id).ToList()!;

                return View("~/Views/Home/Index.cshtml", vm);
            }

            // ------------------------------
            // B) NAME ONLY (no type)
            // -> try exact/id first; else contains across all names
            // ------------------------------
            if (hasTerm)
            {
                var exact = await _client.GetDetailsAsync(term);
                if (exact != null)
                {
                    vm.TotalCount = 1;
                    vm.Page = 1; vm.PageSize = 1;
                    vm.Results = new List<PokemonDetails> { exact };
                    return View("~/Views/Home/Index.cshtml", vm);
                }

                var allNames = await _client.GetAllNamesAsync();
                var filtered = allNames
                    .Where(n => n.Contains(term, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n)
                    .ToList();

                vm.TotalCount = filtered.Count;

                var totalPages = Math.Max(1, (int)Math.Ceiling(vm.TotalCount / (double)vm.PageSize));
                vm.Page = Math.Min(Math.Max(1, vm.Page), totalPages);

                var pageNames = filtered
                    .Skip((vm.Page - 1) * vm.PageSize)
                    .Take(vm.PageSize)
                    .ToList();

                var details = await Task.WhenAll(pageNames.Select(n => _client.GetDetailsAsync(n)));
                vm.Results = details.Where(d => d != null)!.OrderBy(d => d!.Id).ToList()!;

                return View("~/Views/Home/Index.cshtml", vm);
            }

            // ------------------------------
            // C) NO FILTERS -> default paging
            // ------------------------------
            vm.TotalCount = await _client.GetTotalPokemonCountAsync();
            var totalPagesAll = Math.Max(1, (int)Math.Ceiling(vm.TotalCount / (double)vm.PageSize));
            vm.Page = Math.Min(Math.Max(1, vm.Page), totalPagesAll);

            vm.Results = await _client.GetPageAsync(vm.Page, vm.PageSize);
            return View("~/Views/Home/Index.cshtml", vm);
        }
    }
}