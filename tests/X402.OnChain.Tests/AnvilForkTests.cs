using System.Numerics;
using Nethereum.Signer;
using X402.Assets;

namespace X402.OnChain.Tests;

[Trait("Category", "OnChain")]
[Collection(nameof(AnvilCollection))]
public sealed class AnvilForkTests(AnvilFork chain)
{
    [Fact]
    public async Task The_fork_runs_the_real_EURC_contract()
    {
        OnChain.RequireOptIn();

        var name = await chain.NameAsync(KnownAssets.EurcBaseSepolia);

        name.ShouldBe("EURC");
    }

    [Fact]
    public async Task Minting_credits_the_holder()
    {
        OnChain.RequireOptIn();
        var holder = EthECKey.GenerateKey().GetPublicAddress();

        await chain.MintAsync(KnownAssets.EurcBaseSepolia, holder, 1_000_000);

        (await chain.BalanceOfAsync(KnownAssets.EurcBaseSepolia, holder)).ShouldBe(new BigInteger(1_000_000));
    }
}
