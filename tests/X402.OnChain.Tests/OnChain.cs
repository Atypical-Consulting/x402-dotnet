namespace X402.OnChain.Tests;

/// <summary>The opt-in gate: a bare <c>dotnet test</c> never needs Docker or the network.</summary>
public static class OnChain
{
    public static bool IsOptedIn => Environment.GetEnvironmentVariable("X402_ONCHAIN") == "1";

    public static void RequireOptIn() => Assert.SkipUnless(IsOptedIn,
        "On-chain tests are opt-in: set X402_ONCHAIN=1 (needs Docker and outbound HTTPS to a Base Sepolia RPC).");
}

/// <summary>
/// One Anvil fork for the whole suite, and its tests run one at a time: a test that warps the
/// chain clock (the expiry case) must not race another test's settlement.
/// </summary>
[CollectionDefinition(nameof(AnvilCollection))]
public sealed class AnvilCollection : ICollectionFixture<AnvilFork>;
