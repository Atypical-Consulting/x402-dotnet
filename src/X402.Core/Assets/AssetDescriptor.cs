namespace X402.Assets;

/// <summary>
/// Everything needed to price a resource in a token and to sign an EIP-3009 authorization for it.
/// </summary>
/// <remarks>
/// <see cref="Eip712Name"/> and <see cref="Eip712Version"/> are the token contract's own EIP-712
/// domain values, read from its <c>name()</c> and <c>version()</c> functions. They are not
/// cosmetic: a wrong value produces a signature that recovers to a different address, and the
/// facilitator rejects the payment without an actionable reason. USDC, for one, does not use the
/// same domain name on Base Sepolia and Base mainnet.
/// </remarks>
public sealed record AssetDescriptor
{
    /// <summary>CAIP-2 identifier of the network the token is deployed on.</summary>
    public required string Network { get; init; }

    /// <summary>Token contract address.</summary>
    public required string Address { get; init; }

    /// <summary>Ticker symbol, used to select the asset from configuration.</summary>
    public required string Symbol { get; init; }

    /// <summary>Number of decimals the token uses.</summary>
    public required int Decimals { get; init; }

    /// <summary>The token contract's EIP-712 domain name.</summary>
    public required string Eip712Name { get; init; }

    /// <summary>The token contract's EIP-712 domain version.</summary>
    public required string Eip712Version { get; init; }
}
