using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pokedex.Services;
using System.Net;        // ? added
using System.Net.Http;  // ? added

var builder = WebApplication.CreateBuilder(args);

// MVC
builder.Services.AddControllersWithViews();

// (optional but recommended if your client uses IMemoryCache)
// builder.Services.AddMemoryCache();

// DI for the API client (hardened HttpClient)
builder.Services.AddHttpClient<IPokeApiClient, PokeApiClient>(client =>
{
    client.BaseAddress = new Uri("https://pokeapi.co/api/v2/");
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestVersion = HttpVersion.Version11;
    client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // recycle/refresh pooled sockets so we avoid “forcibly closed” stale connections
    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),

    // keep bursts under control
    MaxConnectionsPerServer = 20,

    // fewer bytes over the wire
    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
})
// rotate handlers periodically to refresh pools
.SetHandlerLifetime(TimeSpan.FromMinutes(5));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Pokedex}/{action=Index}/{id?}"); // <-- Pokedex is default

builder.Services.AddMemoryCache();

app.Run();