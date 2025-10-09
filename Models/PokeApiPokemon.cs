using System.Collections.Generic;

namespace Pokedex.Models
{
    public class PokeApiPokemon
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public List<PokeApiPokemonMove> Moves { get; set; } = new();
    }

    public class PokeApiPokemonMove
    {
        public NamedApiResource Move { get; set; } = new();
        public List<PokeApiVersionGroupDetail> VersionGroupDetails { get; set; } = new();
    }

    public class PokeApiVersionGroupDetail
    {
        public int LevelLearnedAt { get; set; }
        public NamedApiResource MoveLearnMethod { get; set; } = new();
        public NamedApiResource VersionGroup { get; set; } = new();
    }

    public class NamedApiResource
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
    }
}
