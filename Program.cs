using Pokedex.Services;            // ✅ add this
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// MVC
builder.Services.AddControllersWithViews();

// ✅ Register an HttpClient-backed PokéAPI service
builder.Services.AddHttpClient<IPokeApiClient, PokeApiClient>(client =>
{
    client.BaseAddress = new Uri("https://pokeapi.co/api/v2/");
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// ✅ Make Pokédex the default route
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Pokedex}/{action=Index}/{id?}");

app.Run();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Pokedex}/{action=Index}/{id?}");
