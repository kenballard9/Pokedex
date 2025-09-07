using System.Collections.Generic;

namespace Pokedex.Models
{
    /// <summary>
    /// Represents detailed info about a Pokémon (used in card display).
    /// </summary>
    public class PokemonDetails
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public List<string> Types { get; set; } = new();

        // ✅ New properties for stats
        public int HP { get; set; }
        public int Attack { get; set; }
        public int Defense { get; set; }
        public int SpecialAttack { get; set; }
        public int SpecialDefense { get; set; }
        public int Speed { get; set; }

        // ✅ New property for abilities
        public List<string> Abilities { get; set; } = new();
    }
}
