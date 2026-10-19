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

`FakeFacilitator` has no ledger. Its settlement transaction hash is derived from the nonce, and nothing touches a chain. No test against it can catch:

- an insufficient on-chain balance (the `InsufficientFunds` scenario is a scripted answer, not a balance check);
- a `transferWithAuthorization` that reverts;
- mempool races between two settlements of competing authorizations;
- a facilitator that reports success on a transfer that later fails.

The first two are covered by the [on-chain suite](#on-chain-tests-against-a-fork) below; the last two are covered by nothing in this repository.

It also says nothing about whether a real facilitator settles a given asset: it settles anything well-formed. That question is answered only by [probing the real facilitator](facilitators.md#probe-before-you-rely-on-one).

## On-chain tests against a fork

`tests/X402.OnChain.Tests` swaps `FakeFacilitator` for a test-only facilitator that really submits `transferWithAuthorization`, against Circle’s real `FiatToken` contracts for EURC and USDC on an [Anvil](https://getfoundry.sh/anvil/overview) fork of Base Sepolia at a pinned block. The paid API and the paying agent are the same `PaidApiFixture` the end-to-end suite uses. Each test asserts the HTTP outcome and reads the chain back independently (`balanceOf`, `authorizationState`, the settlement receipt).

| Case | Agent sees | Chain shows |
|---|---|---|
| Settles in EURC, and in USDC | 200 | payee balance up by exactly the price, receipt status 1 |
| Unfunded payer | 402 | `/settle` never called, payee unchanged |
| Funds moved away between verify and settle | 402 | receipt status 0, payee unchanged, nonce still unused |
| Same authorization landed by someone else first | 402 | payee credited exactly once, nonce used |
| `validBefore` passed on the chain’s clock | 402 | receipt status 0 (the contract refuses it), payee unchanged |

The suite is opt-in. Without `X402_ONCHAIN=1` every test reports Skipped and nothing touches Docker or the network:

```bash
X402_ONCHAIN=1 dotnet test --project tests/X402.OnChain.Tests
```

It needs Docker (an Anvil container through Testcontainers) and outbound HTTPS to a Base Sepolia RPC, `https://sepolia.base.org` by default; `X402_BASE_SEPOLIA_RPC_URL` overrides it. With Foundry installed and no Docker, start a fork yourself (`anvil --fork-url https://sepolia.base.org --fork-block-number 48300032 --chain-id 84532`) and point `X402_ANVIL_RPC_URL` at it. Opted in without either, the suite fails loudly rather than skipping. CI runs it in its own `onchain` job.

It still does not prove how a real facilitator behaves (the one in these tests is ours), mempool races, reorgs, or anything about the live network.

`TestServer` itself is more forgiving than a real transport. It tolerates wire-level inconsistencies, such as a stale `Content-Length`, that a real Kestrel connection rejects. Assert on headers explicitly when the wire format matters. See [Limits of what’s proven](limits.md).
