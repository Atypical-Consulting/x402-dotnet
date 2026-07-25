using X402.AspNetCore.DependencyInjection;
using X402.AspNetCore.Gate;
using X402.Assets;
using X402.Pricing;
using X402.TestKit;

var builder = WebApplication.CreateBuilder(args);

// Fixed so PayingAgent's default X402_API_URL can point at it with no extra configuration.
builder.WebHost.UseUrls("http://localhost:8402");

// Register. Everything the pipeline needs — payee, network, accepted assets, facilitator — comes
// from the "X402" section of appsettings.json. appsettings.Development.json overrides only the
// facilitator, for pointing at one you run yourself; see samples/README.md.
builder.Services.AddX402(builder.Configuration.GetSection("X402"));

// Opt-in, independent of ASPNETCORE_ENVIRONMENT: `UseFakeFacilitator=true dotnet run` swaps the
// two named facilitator HttpClients for an in-process fake, so every call below settles for real
// with no testnet wallet at all. Nothing else in this file changes — the fake is wired at the
// transport, not the pipeline, and X402.AspNetCore never knows the difference.
if (builder.Configuration.GetValue("UseFakeFacilitator", defaultValue: false))
{
    var fake = new FakeFacilitator();

    foreach (var clientName in new[] { "x402-verify", "x402-settle" })
    {
        builder.Services.AddHttpClient(clientName, client =>
        {
            // FakeFacilitator mounts /verify, /settle and /supported at the root, unlike the real
            // facilitator's "https://x402.org/facilitator/" from appsettings.json — replace the
            // base address too, or every call resolves to the wrong path against it.
            client.BaseAddress = new Uri("https://fake-facilitator.invalid/");
        }).ConfigurePrimaryHttpMessageHandler(fake.CreateHandler);
    }
}

var app = builder.Build();

// Protect. One route priced through the table — euros first, then dollars, the order this
// library asks operators to prefer (see ADR 0002) — one priced from inside its own handler
// because its price depends on the request. UseX402 must be registered ahead of the endpoints
// below: it is what settles payment on the way out.
var detailedPrices = new PriceSet(
[
    Price.For(KnownAssets.EurcBaseSepolia, 0.010m),
    Price.For(KnownAssets.UsdcBaseSepolia, 0.011m),
]);

app.UseX402(routes => routes.Map(
    "/weather/detailed", detailedPrices,
    describe: overrides => overrides.Description = "Hourly detail: wind, humidity and precipitation chance."));

// Done. Three endpoints: free, priced by route, priced dynamically.
app.MapGet("/weather", () => Results.Ok(new WeatherReport("Brussels", 19, "Overcast")));

app.MapGet("/weather/detailed", () => Results.Ok(new DetailedWeatherReport(
    "Brussels", 19, "Overcast", WindKph: 14, HumidityPercent: 82, PrecipitationChancePercent: 40)));

app.MapPost("/analyze", AnalyzeAsync);

app.Run();

// Priced at one atomic unit of EURC/USDC per byte submitted: there is no fixed table entry
// because the price is not known until the request body is. IX402PaymentGate opens the same
// payment the route table opens above — this handler just computes its own price first.
static async Task AnalyzeAsync(HttpContext context, IX402PaymentGate gate)
{
    using var reader = new StreamReader(context.Request.Body);
    var document = await reader.ReadToEndAsync(context.RequestAborted);
    var bytes = System.Text.Encoding.UTF8.GetByteCount(document).ToString();

    var prices = new PriceSet(
    [
        Price.Atomic(KnownAssets.EurcBaseSepolia, bytes),
        Price.Atomic(KnownAssets.UsdcBaseSepolia, bytes),
    ]);

    var result = await gate.RequireAsync(prices, cancellationToken: context.RequestAborted);
    if (!result.CanContinue)
    {
        await result.Result!.ExecuteAsync(context);
        return;
    }

    var words = document.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    await context.Response.WriteAsJsonAsync(
        new AnalysisResult(int.Parse(bytes), words, result.SettledAsset!.Symbol),
        context.RequestAborted);
}

internal sealed record WeatherReport(string City, int TemperatureCelsius, string Condition);

internal sealed record DetailedWeatherReport(
    string City, int TemperatureCelsius, string Condition,
    int WindKph, int HumidityPercent, int PrecipitationChancePercent);

internal sealed record AnalysisResult(int Bytes, int Words, string SettledIn);
