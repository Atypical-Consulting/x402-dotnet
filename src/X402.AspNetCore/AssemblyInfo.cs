using System.Runtime.CompilerServices;

// HttpFacilitatorClient is internal: the test project registers it directly (as the concrete
// implementation type behind IFacilitatorClient) to exercise its two named HttpClients, and
// EnsureTrailingSlash is tested directly as a unit, without going through a live HTTP round trip.
[assembly: InternalsVisibleTo("X402.AspNetCore.Tests")]
