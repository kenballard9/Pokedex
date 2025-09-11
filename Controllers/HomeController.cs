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
                AllTypes = await _client.GetTypesAsync(),
                Page = page < 1 ? 1 : page,
                PageSize = pageSize < 1 ? 20 : pageSize
            };

            // TYPE FILTER
            if (!string.IsNullOrWhiteSpace(selectedType))
            {
                vm.TotalCount = await _client.GetTypeCountAsync(selectedType);
                vm.TotalCount = Math.Max(0, vm.TotalCount);
                vm.Page = Math.Min(Math.Max(1, vm.Page), Math.Max(1, (int)Math.Ceiling(vm.TotalCount / (double)vm.PageSize)));

                vm.Results = await _client.GetByTypePageAsync(selectedType, vm.Page, vm.PageSize);
                return View("~/Views/Home/Index.cshtml", vm);
            }

            // NAME SEARCH
            if (!string.IsNullOrWhiteSpace(searchName))
            {
                // exact (or ID)
                var exact = await _client.GetDetailsAsync(searchName);
                if (exact != null)
                {
                    vm.TotalCount = 1;
                    vm.Page = 1; vm.PageSize = 1;
                    vm.Results = new List<PokemonDetails> { exact };
                    return View("~/Views/Home/Index.cshtml", vm);
                }

                // prefix suggestions (paged)
                var suggestions = await _client.SuggestAsync(searchName, max: 200);
                vm.TotalCount = suggestions.Count;

                if (vm.TotalCount == 0)
                {
                    vm.Results = new();
                    return View("~/Views/Home/Index.cshtml", vm);
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

                return View("~/Views/Home/Index.cshtml", vm);
            }

            // DEFAULT: page across all Pokémon
            vm.TotalCount = await _client.GetTotalPokemonCountAsync();
            var total = Math.Max(0, vm.TotalCount);
            var totalPagesAll = Math.Max(1, (int)Math.Ceiling(total / (double)vm.PageSize));
            vm.Page = Math.Min(Math.Max(1, vm.Page), totalPagesAll);

            vm.Results = await _client.GetPageAsync(vm.Page, vm.PageSize);
            return View("~/Views/Home/Index.cshtml", vm);



        }
    }
}
