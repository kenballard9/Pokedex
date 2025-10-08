namespace Pokedex.ViewModels
{
    public class TypeDetailsViewModel
    {
        public string TypeName { get; set; } = "";

        // Types this one deals 2× damage to
        public List<string>? Strengths { get; set; }

        // Types that deal 2× damage to this one
        public List<string>? Weaknesses { get; set; }

        // Types that deal ½× damage to this one
        public List<string>? Resistances { get; set; }

        // Types that deal 0× damage to this one
        public List<string>? Immunities { get; set; }

        // Optional list of all types for quick navigation
        public List<string>? AllTypes { get; set; }

        // Optional: return URL to get back to previous page
        public string? ReturnUrl { get; set; }
    }
}
