using System;
using System.Collections.Generic;

namespace Pokedex.Models
{
    /// <summary>
    /// View model for the Pokédex index page with paging + detailed results.
    /// </summary>
    public class PokedexIndexViewModel
    {
        // --- Search / filter ---
        public string? SearchName { get; set; }
        public string? SelectedType { get; set; }
        public List<string> AllTypes { get; set; } = new();

        // --- Results (now detailed, with stats & abilities) ---
        public List<PokemonDetails> Results { get; set; } = new();

        // --- Paging ---
        public int Page { get; set; } = 1;          // 1-based page
        public int PageSize { get; set; } = 20;     // default page size
        public int TotalCount { get; set; } = 0;    // total pokemon count (for all or filtered)

        public int TotalPages => Math.Max(1, (int)Math.Ceiling((double)Math.Max(0, TotalCount) / Math.Max(1, PageSize)));
        public bool HasPrev => Page > 1;
        public bool HasNext => Page < TotalPages;

        // Helper to build a page number clamped to valid range
        public int ClampPage(int p) => Math.Min(Math.Max(1, p), TotalPages);
    }
}