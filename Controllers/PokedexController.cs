using Microsoft.AspNetCore.Mvc;
using Pokedex.Models;
using Pokedex.Services;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;

namespace Pokedex.Controllers
{
    public class PokedexController : Controller
    {
        private readonly IPokeApiClient _client;

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
        public async Task<IActionResult> Details(string id, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(id)) return NotFound();

            var details = await _client.GetDetailsAsync(id);
            if (details == null) return NotFound();

            return View(details);
        }
    }
}
