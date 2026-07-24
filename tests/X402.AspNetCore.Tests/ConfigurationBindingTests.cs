using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using X402.AspNetCore.Configuration;
using X402.AspNetCore.DependencyInjection;

namespace X402.AspNetCore.Tests;

/// <summary>
/// The configuration binder has historically had trouble populating get-only collection
/// properties on every version. This binds an actual JSON section — the way an operator's
/// appsettings.json would supply it — through a real <see cref="IConfiguration"/>, rather than
/// assuming the property shape works.
/// </summary>
public sealed class ConfigurationBindingTests
{
    [Fact]
    public void Assets_and_tags_bind_from_a_json_configuration_section()
    {
        const string json = """
            {
              "X402": {
                "PayTo": "0x209693Bc6afc0C5328bA36FaF03C514EF312287C",
                "Network": "eip155:84532",
                "FacilitatorUrl": "https://x402.org/facilitator",
                "Assets": [ { "Symbol": "EURC" }, { "Symbol": "USDC" } ],
                "Tags": [ "ai", "docs" ]
              }
            }
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddX402(configuration.GetSection("X402"));

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<X402Options>>().Value;

        options.Assets.Select(a => a.Symbol).ShouldBe(["EURC", "USDC"]);
        options.Tags.ShouldBe(["ai", "docs"]);
    }
}
