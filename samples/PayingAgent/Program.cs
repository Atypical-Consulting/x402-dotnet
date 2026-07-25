using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using X402.Assets;
using X402.Client;
using X402.Client.DependencyInjection;
using X402.Client.Signing;
using X402.Networks;

// A throwaway key plays the agent below — address 0x2e3c7D875Ba3561895739Ebdf4e2B6Ceb8a20c55,
// generated for this sample and never funded. It is also committed in this repository, so it is
// public: fine for a demo, unusable for anything you want to keep private. Export
// X402_PRIVATE_KEY with a key of your own, then send it testnet EURC/USDC (see samples/README.md),
// to see the paid calls actually settle instead of failing for lack of funds.
const string DemoPrivateKey = "0x887619a98601e74fc00d885b594fb9da1272bad87f8d511c532b2dd359bc123e";

var privateKey = Environment.GetEnvironmentVariable("X402_PRIVATE_KEY") ?? DemoPrivateKey;

if (args.Contains("--probe"))
{
    await Probe.RunAsync(privateKey, useFake: args.Contains("--fake"));
    return;
}

var apiBaseUrl = Environment.GetEnvironmentVariable("X402_API_URL") ?? "http://localhost:8402";

var services = new ServiceCollection();

// Registers X402ClientOptions, an ISpendTracker that enforces it, and X402PaymentHandler — the
// three things AddX402Payment below needs. It does not register a signer: that is this
// application's key material, never the library's.
services.AddX402Client(options =>
{
    options.AllowedNetworks.Add(KnownNetworks.BaseSepolia);
    // Generous enough for the three calls below; a real agent sizes these to its own budget,
    // never to "whatever a server happens to ask".
    options.SetLimits(KnownAssets.EurcBaseSepolia, perRequest: 1m, perSession: 10m);
    options.SetLimits(KnownAssets.UsdcBaseSepolia, perRequest: 1m, perSession: 10m);
});
services.AddSingleton<IPaymentSigner>(new PrivateKeyPaymentSigner(privateKey));

// This is the whole integration: one named HttpClient, with the paying handler attached.
// Everything below calls it exactly as it would call any other HttpClient.
services.AddHttpClient("PaidApi", client => client.BaseAddress = new Uri(apiBaseUrl))
    .AddX402Payment();

await using var provider = services.BuildServiceProvider();
var paidApi = provider.GetRequiredService<IHttpClientFactory>().CreateClient("PaidApi");

Console.WriteLine($"Paying agent — three calls to {apiBaseUrl}, no payment logic below.");
Console.WriteLine();

await CallAsync(paidApi, HttpMethod.Get, "/weather", content: null);
await CallAsync(paidApi, HttpMethod.Get, "/weather/detailed", content: null);
await CallAsync(paidApi, HttpMethod.Post, "/analyze", JsonContent.Create(new
{
    Document = "The quick brown fox jumps over the lazy dog. Priced per byte, settled per request.",
}));

// The only payment-aware line in this whole file is reading the receipt back off the response —
// everything that produced it (seeing the 402, picking an asset, signing, replaying) happened
// inside PaidApi's HttpClient, never here.
static async Task CallAsync(HttpClient http, HttpMethod method, string path, HttpContent? content)
{
    using var request = new HttpRequestMessage(method, path) { Content = content };

    try
    {
        using var response = await http.SendAsync(request);
        var receipt = response.GetPaymentReceipt();

        Console.WriteLine(receipt is null
            ? $"{method,-4} {path,-18} -> {(int)response.StatusCode} (no payment required)"
            : $"{method,-4} {path,-18} -> {(int)response.StatusCode}, paid {receipt.Amount} " +
              $"atomic units, settled {receipt.Transaction}");
    }
    catch (X402Exception exception)
    {
        // Expected without a funded testnet wallet: the facilitator refused settlement, and this
        // client does not retry a payment that was already refused once. See samples/README.md.
        Console.WriteLine($"{method,-4} {path,-18} -> payment failed: {exception.Message}");
    }
}
