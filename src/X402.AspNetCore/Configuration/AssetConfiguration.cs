namespace X402.AspNetCore.Configuration;

/// <summary>
/// An accepted asset, declared either by ticker symbol — resolved against the built-in catalogue
/// for the configured network — or described in full.
/// </summary>
public sealed class AssetConfiguration
{
    /// <summary>Ticker symbol, for example <c>EURC</c>. Resolved against the catalogue.</summary>
    public string? Symbol { get; set; }

    /// <summary>Token contract address. Required when the symbol is not catalogued.</summary>
    public string? Address { get; set; }

    /// <summary>Number of decimals. Required when the symbol is not catalogued.</summary>
    public int? Decimals { get; set; }

    /// <summary>The token's EIP-712 domain name. Required when the symbol is not catalogued.</summary>
    public string? Eip712Name { get; set; }

    /// <summary>The token's EIP-712 domain version. Required when the symbol is not catalogued.</summary>
    public string? Eip712Version { get; set; }
}
