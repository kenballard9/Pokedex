using Microsoft.AspNetCore.Mvc;
using Pokedex.ViewModels; // or Pokedex.Models if you put the VM there
using System;
using System.Collections.Generic;
using System.Linq;

namespace Pokedex.Controllers
{
    public class TypesController : Controller
    {
        // Handle both:
        //   /Types/Details              --> empty state (no selection)
        //   /Types/Details?name=fire    --> Fire selected
        //   /Types/Details/{name?}      --> also supports path segment (e.g., /Types/Details/fire)
        [HttpGet("/Types/Details")]
        [HttpGet("/Types/Details/{name?}")]
        public IActionResult Details(string? name, string? returnUrl)
        {
            // All types (Gen 1–9; remove any you don’t support)
            var allTypes = new[]
            {
                "normal","fire","water","electric","grass","ice","fighting","poison","ground",
                "flying","psychic","bug","rock","ghost","dragon","dark","steel","fairy"
            };

            // Offensive chart: attacker -> (defender -> multiplier)
            var eff = new Dictionary<string, Dictionary<string, double>>
            {
                { "normal",  new() { { "rock",0.5 }, { "ghost",0 }, { "steel",0.5 } } },
                { "fire",    new() { { "fire",0.5 }, { "water",0.5 }, { "grass",2 }, { "ice",2 }, { "bug",2 }, { "rock",0.5 }, { "dragon",0.5 }, { "steel",2 } } },
                { "water",   new() { { "fire",2 }, { "water",0.5 }, { "grass",0.5 }, { "ground",2 }, { "rock",2 }, { "dragon",0.5 } } },
                { "electric",new() { { "water",2 }, { "electric",0.5 }, { "grass",0.5 }, { "ground",0 }, { "flying",2 }, { "dragon",0.5 } } },
                { "grass",   new() { { "fire",0.5 }, { "water",2 }, { "grass",0.5 }, { "poison",0.5 }, { "ground",2 }, { "flying",0.5 }, { "bug",0.5 }, { "rock",2 }, { "dragon",0.5 }, { "steel",0.5 } } },
                { "ice",     new() { { "fire",0.5 }, { "water",0.5 }, { "grass",2 }, { "ground",2 }, { "flying",2 }, { "dragon",2 }, { "steel",0.5 } } },
                { "fighting",new() { { "normal",2 }, { "ice",2 }, { "rock",2 }, { "dark",2 }, { "steel",2 }, { "poison",0.5 }, { "flying",0.5 }, { "psychic",0.5 }, { "bug",0.5 }, { "fairy",0.5 }, { "ghost",0 } } },
                { "poison",  new() { { "grass",2 }, { "poison",0.5 }, { "ground",0.5 }, { "rock",0.5 }, { "ghost",0.5 }, { "steel",0 }, { "fairy",2 } } },
                { "ground",  new() { { "fire",2 }, { "electric",2 }, { "grass",0.5 }, { "poison",2 }, { "flying",0 }, { "bug",0.5 }, { "rock",2 }, { "steel",2 } } },
                { "flying",  new() { { "electric",0.5 }, { "grass",2 }, { "fighting",2 }, { "bug",2 }, { "rock",0.5 }, { "steel",0.5 } } },
                { "psychic", new() { { "fighting",2 }, { "poison",2 }, { "psychic",0.5 }, { "steel",0.5 }, { "dark",0 } } },
                { "bug",     new() { { "grass",2 }, { "psychic",2 }, { "dark",2 }, { "fire",0.5 }, { "fighting",0.5 }, { "poison",0.5 }, { "flying",0.5 }, { "ghost",0.5 }, { "steel",0.5 }, { "fairy",0.5 } } },
                { "rock",    new() { { "fire",2 }, { "ice",2 }, { "flying",2 }, { "bug",2 }, { "fighting",0.5 }, { "ground",0.5 }, { "steel",0.5 } } },
                { "ghost",   new() { { "normal",0 }, { "psychic",2 }, { "ghost",2 }, { "dark",0.5 } } },
                { "dragon",  new() { { "dragon",2 }, { "steel",0.5 }, { "fairy",0 } } },
                { "dark",    new() { { "psychic",2 }, { "ghost",2 }, { "fighting",0.5 }, { "dark",0.5 }, { "fairy",0.5 } } },
                { "steel",   new() { { "rock",2 }, { "ice",2 }, { "fairy",2 }, { "fire",0.5 }, { "water",0.5 }, { "electric",0.5 }, { "steel",0.5 } } },
                { "fairy",   new() { { "fighting",2 }, { "dragon",2 }, { "dark",2 }, { "fire",0.5 }, { "poison",0.5 }, { "steel",0.5 } } }
            };

            // Helper to pretty-case a list
            static List<string> CapList(IEnumerable<string> src) =>
                src.Select(s => string.IsNullOrWhiteSpace(s) ? s : char.ToUpperInvariant(s[0]) + s[1..]).ToList();

            // Empty state: if no name provided, DO NOT 404. Show "Please select a type."
            if (string.IsNullOrWhiteSpace(name))
            {
                var vmEmpty = new TypeDetailsViewModel
                {
                    TypeName = null,              // triggers empty-state in the view
                    Strengths = new List<string>(),
                    Weaknesses = new List<string>(),
                    Resistances = new List<string>(),
                    Immunities = new List<string>(),
                    AllTypes = CapList(allTypes), // still show "Jump to Type"
                    ReturnUrl = returnUrl
                };
                return View("Details", vmEmpty);   // Views/Types/Details.cshtml
            }

            // Normalize
            var type = name.Trim().ToLowerInvariant();

            if (!allTypes.Contains(type))
                return NotFound($"Unknown type: {name}");

            // Offensive strengths: what THIS type hits for >1×
            var strengths = eff.ContainsKey(type)
                ? eff[type].Where(kv => kv.Value > 1.0).Select(kv => kv.Key).OrderBy(x => x).ToList()
                : new List<string>();

            // Defensive multipliers: what happens when each attacker hits THIS type
            var defensive = new Dictionary<string, double>();
            foreach (var atk in allTypes)
            {
                double m = 1.0;
                if (eff.ContainsKey(atk) && eff[atk].TryGetValue(type, out var mv))
                    m = mv; // mv is how 'atk' does vs 'type'
                defensive[atk] = m;
            }

            var weaknesses = defensive.Where(kv => kv.Value > 1.0).Select(kv => kv.Key).OrderBy(x => x).ToList();
            var resistances = defensive.Where(kv => kv.Value > 0 && kv.Value < 1.0).Select(kv => kv.Key).OrderBy(x => x).ToList();
            var immunities = defensive.Where(kv => kv.Value == 0).Select(kv => kv.Key).OrderBy(x => x).ToList();

            var vm = new TypeDetailsViewModel
            {
                TypeName = char.ToUpperInvariant(type[0]) + type[1..],
                Strengths = CapList(strengths),
                Weaknesses = CapList(weaknesses),
                Resistances = CapList(resistances),
                Immunities = CapList(immunities),
                AllTypes = CapList(allTypes),
                ReturnUrl = returnUrl
            };

            return View("Details", vm); // Views/Types/Details.cshtml
        }
    }
}
