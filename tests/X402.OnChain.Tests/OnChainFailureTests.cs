using System.Numerics;
using Nethereum.Signer;
using X402.Assets;
using X402.Client;
using X402.Integration.Tests;
using X402.Protocol;
using X402.TestKit;
using X402.Transport;
using static X402.OnChain.Tests.OnChainSettlementTests;

namespace X402.OnChain.Tests;

/// <summary>
/// The on-chain failure modes <c>FakeFacilitator</c> cannot see, each produced by Circle's real
/// <c>FiatToken</c> on the fork. The agent sees a 402 as <see cref="PaymentRejectedException"/>;
/// the payee's on-chain balance is the independent witness.
/// </summary>
[Trait("Category", "OnChain")]
[Collection(nameof(AnvilCollection))]
public sealed class OnChainFailureTests(AnvilFork chain)
{
    private static readonly AssetDescriptor Eurc = KnownAssets.EurcBaseSepolia;

    [Fact]
    public async Task An_unfunded_payer_is_refused_before_any_transaction()
    {
        OnChain.RequireOptIn();
        await using var facilitator = new OnChainFacilitator(chain);
        await using var fixture = await StartAsync(facilitator, EthECKey.GenerateKey());
        var before = await PayeeBalanceAsync();

        await Should.ThrowAsync<PaymentRejectedException>(() => PayAsync(fixture));

        (facilitator.SettleCallCount, await PayeeBalanceAsync() - before).ShouldBe((0, BigInteger.Zero));
    }

    [Fact]
    public async Task A_transfer_that_reverts_at_settlement_withholds_the_content()
    {
        OnChain.RequireOptIn();
        var payerKey = EthECKey.GenerateKey();
        var payer = payerKey.GetPublicAddress();
        await chain.MintAsync(Eurc, payer, 10_000);
        await using var facilitator = new OnChainFacilitator(chain);
        await using var fixture = await StartAsync(facilitator, payerKey);
        // Verification passed against a funded payer; the funds leave before the transfer lands.
        facilitator.BeforeSettle = () => chain.ImpersonatedTransferAsync(
            Eurc, payer, EthECKey.GenerateKey().GetPublicAddress(), 10_000);
        var before = await PayeeBalanceAsync();

        await Should.ThrowAsync<PaymentRejectedException>(() => PayAsync(fixture));

        var receipt = await chain.ReceiptAsync(facilitator.LastSettleTransaction!);
        var nonceUsed = await chain.AuthorizationStateAsync(Eurc, payer, NonceOf(fixture));
        (receipt.Status.Value, await PayeeBalanceAsync() - before, nonceUsed)
            .ShouldBe((BigInteger.Zero, BigInteger.Zero, false));
    }

    [Fact]
    public async Task A_nonce_already_used_on_chain_settles_exactly_once()
    {
        OnChain.RequireOptIn();
        var payerKey = EthECKey.GenerateKey();
        var payer = payerKey.GetPublicAddress();
        await chain.MintAsync(Eurc, payer, 1_000_000);
        await using var facilitator = new OnChainFacilitator(chain);
        await using var fixture = await StartAsync(facilitator, payerKey);
        // Someone else lands the very same authorization first (a front-run, or a second facilitator).
        facilitator.BeforeSettle = () => chain.SubmitAuthorizationAsync(Eurc, PayloadOf(fixture));
        var before = await PayeeBalanceAsync();

        await Should.ThrowAsync<PaymentRejectedException>(() => PayAsync(fixture));

        var nonceUsed = await chain.AuthorizationStateAsync(Eurc, payer, NonceOf(fixture));
        (await PayeeBalanceAsync() - before, nonceUsed).ShouldBe((new BigInteger(10_000), true));
    }

    [Fact]
    public async Task An_authorization_expired_on_chain_is_rejected_by_the_contract()
    {
        OnChain.RequireOptIn();
        var payerKey = EthECKey.GenerateKey();
        await chain.MintAsync(Eurc, payerKey.GetPublicAddress(), 1_000_000);
        await using var facilitator = new OnChainFacilitator(chain);
        await using var fixture = await StartAsync(facilitator, payerKey);
        // Far past validBefore (MaxTimeoutSeconds, 60 by default) on the chain's clock only: the
        // server never sees chain time, so the contract — not our validators — must refuse it.
        facilitator.BeforeSettle = () => chain.IncreaseTimeAsync(3600);
        var before = await PayeeBalanceAsync();

        try
        {
            await Should.ThrowAsync<PaymentRejectedException>(() => PayAsync(fixture));

            var receipt = await chain.ReceiptAsync(facilitator.LastSettleTransaction!);
            (receipt.Status.Value, await PayeeBalanceAsync() - before).ShouldBe((BigInteger.Zero, BigInteger.Zero));
        }
        finally
        {
            await chain.SyncClockAsync();
        }
    }

    private static Task<PaidApiFixture> StartAsync(OnChainFacilitator facilitator, EthECKey payerKey) =>
        PaidApiFixture.StartAsync(
            configureAgent: o => o.SetLimits(Eurc, perRequest: 1m, perSession: 10m),
            facilitatorHandler: facilitator.CreateHandler,
            payerPrivateKey: Hex(payerKey));

    private static Task<HttpResponseMessage> PayAsync(PaidApiFixture fixture) =>
        fixture.Agent.GetAsync("/premium", TestContext.Current.CancellationToken);

    private Task<BigInteger> PayeeBalanceAsync() => chain.BalanceOfAsync(Eurc, TestData.PayeeAddress);

    private static PaymentPayload PayloadOf(PaidApiFixture fixture) =>
        X402Codec.TryDecode<PaymentPayload>(fixture.LastPaymentSignature, out var payload, out var error)
            ? payload!
            : throw new InvalidOperationException($"No decodable PAYMENT-SIGNATURE was sent: {error}");

    private static string NonceOf(PaidApiFixture fixture) => PayloadOf(fixture).AsExactEvm().Authorization.Nonce;
}
