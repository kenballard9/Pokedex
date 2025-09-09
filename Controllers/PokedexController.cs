using Microsoft.AspNetCore.Mvc;
using Pokedex.Models;
using Pokedex.Services;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Pokedex.Controllers
{
    public class PokedexController : Controller
    {
        private readonly IPokeApiClient _client;

        public PokedexController(IPokeApiClient client)
        {
            _client = client;
        }

        /// <summary>
        /// Main Pokédex screen with search (prefix ok), filter by type, and paging.
        /// /Pokedex/Index?searchName=&selectedType=&page=1&pageSize=20
        /// </summary>
        public async Task<IActionResult> Index(string? searchName, string? selectedType, int page = 1, int pageSize = 20)
        {
            var vm = new PokedexIndexViewModel
            {
                SearchName = searchName,
                SelectedType = selectedType,
                AllTypes = await _client.GetTypesAsync(),
                Page = page < 1 ? 1 : page,
                PageSize = pageSize < 1 ? 20 : pageSize
            };

            // If type filter is present, page across that type
            if (!string.IsNullOrWhiteSpace(selectedType))
            {
                vm.TotalCount = await _client.GetTypeCountAsync(selectedType);
                vm.TotalCount = Math.Max(0, vm.TotalCount);
                vm.Page = Math.Min(Math.Max(1, vm.Page), Math.Max(1, (int)Math.Ceiling(vm.TotalCount / (double)vm.PageSize)));

                vm.Results = await _client.GetByTypePageAsync(selectedType, vm.Page, vm.PageSize);
                return View(vm);
            }
v
            // If a search term is present, try exact first, then prefix matches
            if (!string.IsNullOrWhiteSpace(searchName))
            {
                // 1) exact (or ID) lookup
                var exact = await _client.GetDetailsAsync(searchName);
                if (exact != null)
                {
                    vm.TotalCount = 1;
                    vm.Page = 1; vm.PageSize = 1;
                    vm.Results = new List<PokemonDetails> { exact };
                    return View(vm);
                }

                // 2) prefix suggestions (search-as-you-type fallback when user presses Enter)
                var suggestions = await _client.SuggestAsync(searchName, max: 200); // large cap; we'll page it next
                vm.TotalCount = suggestions.Count;

                if (vm.TotalCount == 0)
                {
                    vm.Results = new();
                    return View(vm);
                }

                var totalPages = Math.Max(1, (int)Math.Ceiling(vm.TotalCount / (double)vm.PageSize));
                vm.Page = Math.Min(Math.Max(1, vm.Page), totalPages);

                var pageNames = suggestions
                    .Skip((vm.Page - 1) * vm.PageSize)
                    .Take(vm.PageSize)
                    .ToList();

                var details = new List<PokemonDetails>();
                foreach (var n in pageNames)
                {
                    var d = await _client.GetDetailsAsync(n);
                    if (d != null) details.Add(d);
                }
                vm.Results = details.OrderBy(d => d.Id).ToList();

                return View(vm);
            }

            // Default: page across all Pokémon
            vm.TotalCount = await _client.GetTotalPokemonCountAsync();
            var total = Math.Max(0, vm.TotalCount);
            var totalPagesAll = Math.Max(1, (int)Math.Ceiling(total / (double)vm.PageSize));
            vm.Page = Math.Min(Math.Max(1, vm.Page), totalPagesAll);

            vm.Results = await _client.GetPageAsync(vm.Page, vm.PageSize);
            return View(vm);
        }

        /// <summary>
        /// JSON endpoint for search-as-you-type (autocomplete).
        /// /Pokedex/Suggest?term=pi
        /// </summary>
        [HttpGet]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> Suggest(string term, int max = 10)
        {
            var items = await _client.SuggestAsync(term, max);
            return Json(items);
        }
    }
}
