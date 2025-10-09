using System.Collections.Generic;
using System.Threading.Tasks;
using Pokedex.Models;

namespace Pokedex.Services
{
    /// <summary>
    /// Abstraction over PokéAPI calls used by controllers/views.
    /// </summary>
    public interface IPokeApiClient
    {
        Task<List<string>> GetTypesAsync();

        // Basic lookups
        Task<PokemonListItem?> GetPokemonByNameAsync(string nameOrId);
        Task<List<PokemonListItem>> GetPokemonByTypeAsync(string typeName, int max = 50);

        // Detailed info (stats, abilities, entries, ability defs)
        Task<PokemonDetails?> GetDetailsAsync(string nameOrId);

        // Paging helpers
        Task<int> GetTotalPokemonCountAsync();
        Task<List<PokemonDetails>> GetPageAsync(int page, int pageSize);
        Task<List<PokemonDetails>> GetByTypePageAsync(string typeName, int page, int pageSize);

        // Suggestions / autocomplete
        Task<List<string>> GetAllNamesAsync();
        Task<List<string>> SuggestAsync(string term, int max = 10);

        // Type counts (for paging)
        Task<int> GetTypeCountAsync(string typeName);

        Task<PokeApiPokemon?> GetPokemonAsync(string idOrName, CancellationToken ct = default);

    }
}