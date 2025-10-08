using System.Collections.Generic;

namespace Pokedex.Models
{
    /// <summary>
    /// Minimal data needed to render a Pokémon card in the index grid.
    /// </summary>
    public class PokemonListItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public List<string> Types { get; set; } = new();
    }
}
