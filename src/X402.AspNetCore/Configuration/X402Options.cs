using X402.Networks;

namespace X402.AspNetCore.Configuration;

/// <summary>
/// How this server accepts x402 payments. Bound from the <c>X402</c> configuration section and
/// validated at start-up, so a misconfiguration fails the host rather than the first request.
/// </summary>
/// <remarks>
/// There is deliberately no property here for a private key, mnemonic or signing secret. This
/// server never signs anything and never touches the funds: payments go straight from the payer
/// to <see cref="PayTo"/>. See <c>docs/adr/0003-the-server-never-holds-a-signing-key.md</c>.
/// </remarks>
public sealed class X402Options
{
    /// <summary>Address the funds must reach. Validated for EIP-55 checksum at start-up.</summary>
    public string PayTo { get; set; } = "";

    /// <summary>Network to accept payments on, in CAIP-2 form.</summary>
    public string Network { get; set; } = KnownNetworks.BaseSepolia;

    /// <summary>Accepted assets, in the order of preference announced to payers.</summary>
    public IList<AssetConfiguration> Assets { get; } = [];

    /// <summary>Base address of the facilitator that verifies and settles payments.</summary>
    public Uri? FacilitatorUrl { get; set; }

    /// <summary>Service name advertised in payment demands. Printable ASCII, 32 characters at most.</summary>
    public string? ServiceName { get; set; }

    /// <summary>Absolute URL of a service icon advertised in payment demands.</summary>
    public string? IconUrl { get; set; }

    /// <summary>Discovery tags advertised in payment demands. Five at most.</summary>
    public IList<string> Tags { get; } = [];

    /// <summary>How long a payer has to complete a payment, in seconds.</summary>
    public int MaxTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// How much of a paid response is buffered before settlement. Beyond this, the middleware
    /// settles first and then streams — a failed settlement can no longer withhold the content.
    /// </summary>
    public long MaxBufferedResponseBytes { get; set; } = 1024 * 1024;
}
