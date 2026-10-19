---
title: Testing with FakeFacilitator
description: Run the whole payment chain in-process, with real signature checks, and know where that proof stops.
---

# Testing with FakeFacilitator

`FakeFacilitator` is an in-process x402 facilitator. It performs **real** EIP-712 signature recovery and real validity-window, amount and recipient checks, then settles without any ledger. A test that passes against it proves the client built and signed the authorization correctly, not merely that the plumbing connects.

> [!NOTE]
> `FakeFacilitator` lives in `tests/X402.TestKit`, which is **not published** to NuGet. Use it from a clone of the repository, with a project reference to `tests/X402.TestKit/X402.TestKit.csproj`. Nothing in the three published packages depends on it.

## What it checks

For every `/verify` and `/settle` call, in the default `Valid` scenario:

1. **Signature.** It rebuilds the EIP-712 typed data from the requirement and the authorization, recovers the signer, and refuses with `invalid_exact_evm_payload_signature` unless it matches `from`.
2. **Validity window.** `validAfter` must be in the past and `validBefore` in the future.
3. **Exact amount.** The signed `value` must equal the requirement’s `amount`.
4. **Recipient.** The signed `to` must equal the requirement’s `payTo`.

It also records what it saw: `VerifyCallCount`, `SettleCallCount`, `SettledNonces`, `HasDoubleSettled` and `LastRequestBody`.

## Scenarios

`Scenario` switches its behaviour for every following call; `FailNextCalls` and `FailNextSettleCalls` switch it for a fixed number of calls, then revert.

| Scenario | Behaviour |
| --- | --- |
| `Valid` | Verify the signature for real and settle successfully. |
| `InsufficientFunds` | Reject verification with `insufficient_funds`. |
| `UnsupportedAsset` | Reject any asset outside `SupportedAssets`, as a single-asset facilitator would. |
| `NetworkFailure` | Abort the connection without answering. |
| `Timeout` | Answer after `TimeoutDelay`, longer than the caller’s timeout. |
| `SettleFailure` | Verify successfully, fail settlement. |
| `ServerError` | Answer a bare 500. |

## Wiring it in

The server reaches its facilitator through two named `HttpClient`s, `x402-verify` and `x402-settle`. Point their primary handler at the fake, and nothing else in the pipeline changes. On the client side, put a real <xref:X402.Client.X402PaymentHandler> in front of the test server’s own handler:

```csharp
using System.Net;
using Microsoft.AspNetCore.TestHost;
using Shouldly;
using X402.AspNetCore.Configuration;
using X402.AspNetCore.DependencyInjection;
using X402.Assets;
using X402.Client;
using X402.Client.Signing;
using X402.Client.Spending;
using X402.Networks;
using X402.Pricing;
using X402.TestKit;

public sealed class PaidEndpointTests
{
    [Fact]
    public async Task A_paid_request_settles_once_and_returns_the_content()
    {
        await using var facilitator = new FakeFacilitator();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddX402(options =>
        {
            options.PayTo = TestData.PayeeAddress;
            options.Network = KnownNetworks.BaseSepolia;
            // FakeFacilitator mounts /verify, /settle and /supported at the root.
            options.FacilitatorUrl = new Uri("https://fake-facilitator.invalid/");
            options.Assets.Add(new AssetConfiguration { Symbol = "EURC" });
        });
        foreach (var name in new[] { "x402-verify", "x402-settle" })
        {
            builder.Services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(facilitator.CreateHandler);
        }

        await using var app = builder.Build();
        app.UseX402(routes => routes.Map("/premium", Price.For(KnownAssets.EurcBaseSepolia, 0.01m)));
        app.MapGet("/premium", () => "paid content");
        await app.StartAsync();

        var options = new X402ClientOptions().SetLimits(KnownAssets.EurcBaseSepolia, 1m, 10m);
        var handler = new X402PaymentHandler(
            options, new PrivateKeyPaymentSigner(TestData.PayerPrivateKey), new InMemorySpendTracker(options))
        {
            InnerHandler = app.GetTestServer().CreateHandler(),
        };
        using var agent = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };

        var response = await agent.GetAsync("/premium");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        facilitator.SettleCallCount.ShouldBe(1);
    }
}
```

`TestData` (also in the test kit) holds a throwaway payer key and payee address. The repository’s own end-to-end suite, `tests/X402.Integration.Tests`, is built exactly this way. `samples/PaidApi` uses the same seam behind its `UseFakeFacilitator=true` switch.

## What it does not prove

`FakeFacilitator` has no ledger. Its settlement transaction hash is derived from the nonce, and nothing touches a chain. These are all outside what any test against it, or any test in this repository, can catch:

- an insufficient on-chain balance (the `InsufficientFunds` scenario is a scripted answer, not a balance check);
- a `transferWithAuthorization` that reverts;
- mempool races between two settlements of competing authorizations;
- a facilitator that reports success on a transfer that later fails.

It also says nothing about whether a real facilitator settles a given asset: it settles anything well-formed. That question is answered only by [probing the real facilitator](facilitators.md#probe-before-you-rely-on-one).

`TestServer` itself is more forgiving than a real transport. It tolerates wire-level inconsistencies, such as a stale `Content-Length`, that a real Kestrel connection rejects. Assert on headers explicitly when the wire format matters. See [Limits of what’s proven](limits.md).
