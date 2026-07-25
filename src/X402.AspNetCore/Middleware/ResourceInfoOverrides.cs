namespace X402.AspNetCore.Middleware;

/// <summary>Overrides for the resource description advertised in a payment demand.</summary>
/// <remarks>
/// Defined here, in <c>Middleware</c>, because that is where route declarations
/// (<c>X402RouteBuilder.Map</c>) and the imperative gate (<c>IX402PaymentGate.RequireAsync</c>)
/// both construct one; the engine only consumes it.
/// </remarks>
public sealed class ResourceInfoOverrides
{
    /// <summary>Human-readable description of the resource.</summary>
    public string? Description { get; set; }

    /// <summary>MIME type of the expected response.</summary>
    public string? MimeType { get; set; }
}
