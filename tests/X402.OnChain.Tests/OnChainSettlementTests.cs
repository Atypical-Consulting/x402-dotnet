using System.Net;
using System.Numerics;
using Nethereum.Signer;
using X402.Assets;
using X402.Integration.Tests;
using X402.TestKit;

namespace X402.OnChain.Tests;

/// <summary>
/// A real agent pays the real paid API, whose real facilitator client reaches a facilitator that
/// really submits <c>transferWithAuthorization</c> to Circle's <c>FiatToken</c> on the fork — and
/// the chain itself, read back independently, is the second witness.
/// </summary>
[Trait("Category", "OnChain")]
[Collection(nameof(AnvilCollection))]
public sealed class OnChainSettlementTests(AnvilFork chain)
{
    [Fact]
    public async Task A_euro_payment_moves_the_EURC_balance_on_chain()
    {
        OnChain.RequireOptIn();
        var eurc = KnownAssets.EurcBaseSepolia;
        var payerKey = EthECKey.GenerateKey();
        await chain.MintAsync(eurc, payerKey.GetPublicAddress(), 1_000_000);
        await using var facilitator = new OnChainFacilitator(chain);
        await using var fixture = await PaidApiFixture.StartAsync(
            facilitatorHandler: facilitator.CreateHandler, payerPrivateKey: Hex(payerKey));
        var before = await chain.BalanceOfAsync(eurc, TestData.PayeeAddress);

        var response = await fixture.Agent.GetAsync("/premium", TestContext.Current.CancellationToken);

        var delta = await chain.BalanceOfAsync(eurc, TestData.PayeeAddress) - before;
        var receipt = await chain.ReceiptAsync(facilitator.LastSettleTransaction!);
        (response.StatusCode, delta, receipt.Status.Value)
            .ShouldBe((HttpStatusCode.OK, new BigInteger(10_000), BigInteger.One));
    }

    [Fact]
    public async Task A_dollar_payment_moves_the_USDC_balance_on_chain()
    {
        OnChain.RequireOptIn();
        var usdc = KnownAssets.UsdcBaseSepolia;
        var payerKey = EthECKey.GenerateKey();
        await chain.MintAsync(usdc, payerKey.GetPublicAddress(), 1_000_000);
        await using var facilitator = new OnChainFacilitator(chain);
        await using var fixture = await PaidApiFixture.StartAsync(
            configureAgent: o => o.SetLimits(usdc, perRequest: 1m, perSession: 10m),
            facilitatorHandler: facilitator.CreateHandler, payerPrivateKey: Hex(payerKey));
        var before = await chain.BalanceOfAsync(usdc, TestData.PayeeAddress);

        var response = await fixture.Agent.GetAsync("/premium", TestContext.Current.CancellationToken);

        var delta = await chain.BalanceOfAsync(usdc, TestData.PayeeAddress) - before;
        (response.StatusCode, delta).ShouldBe((HttpStatusCode.OK, new BigInteger(11_000)));
    }

    internal static string Hex(EthECKey key) => "0x" + Convert.ToHexString(key.GetPrivateKeyAsBytes());
}
