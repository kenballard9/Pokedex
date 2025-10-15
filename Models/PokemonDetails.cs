using System.Collections.Generic;

namespace Pokedex.Models
{
    /// <summary>
    /// DTO used by Index and the dedicated Details page.
    /// </summary>
    public class PokemonDetails
    {
        // Identity & visuals
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string ImageUrl { get; set; } = "";

        // Optional extra info from /pokemon
        public int? Height { get; set; }
        public int? Weight { get; set; }

        // Typing
        public List<string> Types { get; set; } = new();

        // Base stats
        public int HP { get; set; }
        public int Attack { get; set; }
        public int Defense { get; set; }
        public int SpecialAttack { get; set; }
        public int SpecialDefense { get; set; }
        public int Speed { get; set; }

        public List<MoveLearnRow> Moves { get; set; } = new();

        // Abilities (names only)
        public List<string> Abilities { get; set; } = new();

        // English Pokédex entries (from /pokemon-species)
        public List<PokedexEntry> PokedexEntries { get; set; } = new();

        // Ability definitions (English) from /ability/{name}
        public List<AbilityDefinition> AbilityDetails { get; set; } = new();

        // ✅ Evolution line used by Details.cshtml
        public List<PokemonListItem> EvolutionLine { get; set; } = new();
    }

    public class PokedexEntry
    {
        public string Version { get; set; } = "";
        public string Text { get; set; } = "";
    }

    public class AbilityDefinition
    {
        public string Name { get; set; } = "";
        public string? ShortEffect { get; set; }
        public string? Effect { get; set; }
    }


}   

    public class MoveLearnRow
    {
        public string MoveName { get; set; } = "";
        public int Level { get; set; }                // 0 if not learned by level-up
        public string Method { get; set; } = "";      // "level-up", "machine", "tutor", "egg", etc.
        public string VersionGroup { get; set; } = ""; // e.g., "scarlet-violet"
        public string? MoveType { get; set; }
}

