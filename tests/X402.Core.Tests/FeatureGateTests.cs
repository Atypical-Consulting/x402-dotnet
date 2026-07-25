using X402.Licensing;

namespace X402.Core.Tests;

public sealed class FeatureGateTests
{
    [Theory]
    [InlineData(X402Features.DynamicPricing)]
    [InlineData(X402Features.BillingLedger)]
    [InlineData(X402Features.PersistedQuota)]
    [InlineData("anything.at.all")]
    public void The_default_gate_allows_everything(string feature)
    {
        // US-17: unchanged behaviour for anyone who configures nothing.
        new AllowAllFeatureGate().IsEnabled(feature).ShouldBeTrue();
    }
}
