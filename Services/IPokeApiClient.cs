using System.Collections.Generic;
using System.Threading.Tasks;
using Pokedex.Models;

namespace Pokedex.Services
{
    /// <summary>
    /// Abstraction over PokéAPI calls used by the controller and views.
    /// </summary>
    public interface IPokeApiClient
    {
        Task<List<string>> GetTypesAsync();

        // Existing (kept for direct lookups)
        Task<PokemonListItem?> GetPokemonByNameAsync(string nameOrId);
        Task<List<PokemonListItem>> GetPokemonByTypeAsync(string typeName, int max = 50);

        // ✅ New: detailed info (stats + abilities)
        Task<PokemonDetails?> GetDetailsAsync(string nameOrId);

        // ✅ New: paging helpers for the index grid
        Task<int> GetTotalPokemonCountAsync();
        Task<List<PokemonDetails>> GetPageAsync(int page, int pageSize);
        Task<List<PokemonDetails>> GetByTypePageAsync(string typeName, int page, int pageSize);

        // ✅ New: name cache + suggestions for typeahead search
        Task<List<string>> GetAllNamesAsync();
        Task<List<string>> SuggestAsync(string term, int max = 10);

        // Count how many Pokémon belong to a type (for paging)
        Task<int> GetTypeCountAsync(string typeName);

    }
}
