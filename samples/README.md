# Samples

Two runnable projects. Read them in this order:

- **`PaidApi`** — a minimal API that charges for two of its three endpoints. `Program.cs` is the
  whole story: register `AddX402`, protect a route with `UseX402`, protect a handler with
  `IX402PaymentGate`, done.
- **`PayingAgent`** — a console agent that calls all three endpoints and prints the receipts. Its
  code contains no payment logic at all — every call goes through a plain `HttpClient` carrying
  `X402PaymentHandler`. It also has a `--probe` mode, described below, that answers a question
  the x402 protocol itself cannot.

Both target Base Sepolia and accept EURC and USDC, euro first (see
[ADR 0002](../docs/adr/0002-multi-asset-with-eurc-as-a-first-class-citizen.md)).

## Running them

```bash
dotnet run --project samples/PaidApi &
dotnet run --project samples/PayingAgent
```

`PaidApi` listens on `http://localhost:8402` (fixed in `Program.cs`, so `PayingAgent`'s default
`X402_API_URL` needs no configuration to find it).

### What you actually get, with no wallet behind it

`PayingAgent` signs with a throwaway key generated for this sample
(`0x2e3c7D875Ba3561895739Ebdf4e2B6Ceb8a20c55`) that has never held any funds, on any network. This
is the real console output of the run above, unedited:

```
Paying agent — three calls to http://localhost:8402, no payment logic below.

GET  /weather           -> 200 (no payment required)
GET  /weather/detailed  -> payment failed: The server demanded payment again for
                            'http://localhost:8402/weather/detailed' after this client
                            paid for it: invalid_exact_evm_insufficient_balance.
                            Refusing to retry a second time.
POST /analyze           -> payment failed: The server demanded payment again for
                            'http://localhost:8402/analyze' after this client paid for
                            it: invalid_exact_evm_insufficient_balance. Refusing to
                            retry a second time.
```

The free call answers normally. Both paid calls get a 402, sign an authorization, and replay it —
exactly as designed — and the *real* `https://x402.org/facilitator` refuses to settle an empty
wallet. `PaidApi`'s own log names the reason plainly:

```
x402 VerificationFailed: http://localhost:8402/weather/detailed 10000 of
0x808456652fdb597867f38412077A9182bf77359F on eip155:84532
(payer 0x2e3c7D875Ba3561895739Ebdf4e2B6Ceb8a20c55) — invalid_exact_evm_insufficient_balance
```

Two things worth noticing in that line. First, `invalid_exact_evm_insufficient_balance` is not one
of the codes `X402ErrorReason` declares (the library's own constant is `insufficient_funds`) — the
real facilitator uses a different string, and this library passes it through verbatim rather than
guessing at it, exactly as documented on `X402ErrorReason`. Second, this is the *whole* failure
mode: no crash, no hang, one exception with a message you can act on
(`X402.Client.PaymentRejectedException`, caught in `PayingAgent/Program.cs` and printed) — and that
reason is not just embedded in the printed text: `PayingAgent` never reads `PaidApi`'s log (a
third-party API's log is never readable from the paying side at all), so the handler decodes the
second `PAYMENT-REQUIRED` itself and puts `invalid_exact_evm_insufficient_balance` on
`PaymentRejectedException.Reason`, with the full refused demand on `.PaymentRequired` — a caller can
branch on the reason programmatically, not just log the message. An example that fails this cleanly
is worth more than one that requires a funded wallet before it proves anything.

### Getting a wallet that actually pays

1. Generate your own key — `PayingAgent` reads `X402_PRIVATE_KEY` if it is set, falling back to
   the committed throwaway key otherwise. **Use your own for anything beyond a first look**: the
   default key is public, right here in this repository, so any testnet funds sent to it are
   spendable by anyone who reads this file.
2. Get testnet EURC and USDC on Base Sepolia from <https://faucet.circle.com/> — no account
   needed, both assets and that network are supported directly.
3. Export the key and run again:

   ```bash
   export X402_PRIVATE_KEY=0x...
   dotnet run --project samples/PayingAgent
   ```

   With a funded wallet, `/weather/detailed` and `/analyze` settle for real and print a
   transaction hash instead of failing.

### Running fully offline

Both of the above need the real `https://x402.org/facilitator`, reachable over the network. To
run the whole demo with no facilitator process and no wallet at all, start `PaidApi` with
`UseFakeFacilitator=true`:

```bash
UseFakeFacilitator=true dotnet run --project samples/PaidApi &
dotnet run --project samples/PayingAgent
```

This swaps the two named facilitator `HttpClient`s (`x402-verify`, `x402-settle`) for an
in-process `X402.TestKit.FakeFacilitator` — see the `if` block near the top of
`PaidApi/Program.cs`. It verifies EIP-712 signatures for real, so this still proves the client's
signing and the server's settlement pipeline are wired correctly; it just never checks an on-chain
balance, so every well-formed request settles. Real, unedited output:

```
Paying agent — three calls to http://localhost:8402, no payment logic below.

GET  /weather           -> 200 (no payment required)
GET  /weather/detailed  -> 200, paid 10000 atomic units, settled 0x1efc6c60c7ac3b5890197a9331620968ac1ecc68c8d66f5ca6cd2a345aee3c2f
POST /analyze           -> 200, paid 97 atomic units, settled 0x75fb0b633e2dbb5526a2a3f4d4da92e4fbbf9525393abde3049242abb34c4ad5
```

`appsettings.Development.json` is a *different* switch, for a *different* purpose: it overrides
only `X402:FacilitatorUrl`, to point at a facilitator you run yourself (this repo's own reference
implementation, or anything else speaking the same wire protocol) instead of the public one.
Activate it with `ASPNETCORE_ENVIRONMENT=Development`. It is independent of
`UseFakeFacilitator`, which works regardless of environment.

## The facilitator probe

Nothing in the x402 protocol says which *assets* a facilitator will settle. `GET /supported`
(`IFacilitatorClient.GetSupportedAsync`) lists scheme-and-network pairs only — `exact` on
`eip155:84532`, for instance — never the tokens behind them. An operator picking a facilitator for
a euro-priced service has no way to confirm, ahead of time, that it settles EURC and not only USDC.

`PayingAgent --probe` answers this the only way it can be answered: empirically. For each
configured asset it attempts a real settlement of the smallest amount that asset can represent —
one atomic unit, 0.000001 EURC or USDC — and reports what actually happened.

```bash
dotnet run --project samples/PayingAgent -- --probe
```

By default this probes `https://x402.org/facilitator`. To probe a different facilitator — the one
you are actually evaluating for your own service — set `X402_FACILITATOR_URL` before running:

```bash
export X402_FACILITATOR_URL=https://your-candidate-facilitator.example
dotnet run --project samples/PayingAgent -- --probe
```

Real output, same unfunded throwaway key as above:

```
Probing https://x402.org/facilitator on eip155:84532

/supported advertises (scheme × network pairs only — never assets):
  exact  eip155:84532
  upto  eip155:84532
  batch-settlement  eip155:84532
  exact  solana:EtWTRABZaYq6iMfeYKouRu166VU2xqa1
  exact  algorand:SGO1GKSzyE7IEPItTxCByw9x8FmnrCDe
  exact  aptos:2
  exact  stellar:testnet
  exact  hedera:testnet
  exact  xrpl:1
  exact  base-sepolia
  exact  solana-devnet

Nothing above says which assets settle. Trying each configured asset for
real, one atomic unit at a time:

  EURC  0.000001  ->  refused  invalid_exact_evm_insufficient_balance
  USDC  0.000001  ->  refused  invalid_exact_evm_insufficient_balance

This facilitator settled none of the configured assets. Nothing in the x402 protocol advertises which
assets a facilitator handles, so this probe is the only way to find out — see
docs/adr/0002 and the README.
```

An empty wallet cannot distinguish "this facilitator does not settle EURC" from "this wallet holds
neither asset" — both refuse the same way. Fund the key with EURC only, or USDC only, from
<https://faucet.circle.com/> and run the probe again to see the two outcomes differ; that
difference *is* the answer §2.1.6 asks for.

`--probe --fake` runs the identical probe against the in-process fake facilitator instead, for a
dry run with no network calls and no wallet — useful for seeing the probe's shape, not for
learning anything about a real facilitator (the fake settles anything well-formed):

```
Probing the in-process fake facilitator on eip155:84532 (--fake: always settles; proves the mechanics, not real asset support)

/supported advertises (scheme × network pairs only — never assets):
  exact  eip155:84532
  exact  eip155:8453

Nothing above says which assets settle. Trying each configured asset for
real, one atomic unit at a time:

  EURC  0.000001  ->  settled  0x9749e769dda0aa87652acb658102f19e3560d183c0faf2cc62905516ddf9446a
  USDC  0.000001  ->  settled  0xefc5134bd0851475d8da89a48a0d22b1c55f9c0ebccc317586db3788ca5ccc39

This facilitator settles EURC and USDC. Nothing in the x402 protocol advertises which
assets a facilitator handles, so this probe is the only way to find out — see
docs/adr/0002 and the README.
```

## The reservation this whole task exists to surface

This library supports EURC end to end — signing, settlement, spending limits, all of it, on equal
footing with USDC (see ADR 0002). Whether a *given facilitator* settles EURC is a property of that
facilitator, not of this library, and nothing in the x402 wire protocol lets you ask it directly.
`https://x402.org/facilitator` advertises the `exact` scheme on `eip155:84532` (Base Sepolia) —
confirmed above — but not on Base mainnet for EVM. Whether it actually settles EURC on the network
it does advertise is exactly what `--probe` is for. Run it against whichever facilitator you are
considering before you rely on it in production: this library supporting EURC is not evidence that
your facilitator will settle EURC — those are two separate facts, and only `--probe` establishes
the second one.
