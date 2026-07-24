using System.Net.Http.Json;
using X402.Assets;
using X402.Json;
using X402.Protocol;
using X402.TestKit;

namespace X402.TestKit.Tests;

public sealed class FakeFacilitatorTests : IAsyncLifetime
{
    private FakeFacilitator facilitator = null!;
    private HttpClient client = null!;

    public ValueTask InitializeAsync()
    {
        facilitator = new FakeFacilitator();
        client = facilitator.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await facilitator.DisposeAsync();
    }

    [Fact]
    public async Task Supported_advertises_exact_on_base_sepolia()
    {
        var supported = await client.GetFromJsonAsync<SupportedResponse>(
            "/supported", X402Json.Options, TestContext.Current.CancellationToken);

        supported.ShouldNotBeNull();
        supported.Kinds.ShouldContain(k => k.Scheme == "exact" && k.Network == "eip155:84532");
    }

    [Fact]
    public async Task Verify_accepts_a_correctly_signed_authorization()
    {
        var request = await TestData.SignedRequestAsync(TestData.EurcSepoliaRequirements());

        var response = await client.PostAsJsonAsync("/verify", request, X402Json.Options, TestContext.Current.CancellationToken);
        var verify = await response.Content.ReadFromJsonAsync<VerifyResponse>(X402Json.Options, TestContext.Current.CancellationToken);

        verify.ShouldNotBeNull();
        verify.IsValid.ShouldBeTrue(verify.InvalidReason);
        verify.Payer.ShouldBe(TestData.PayerAddress);
    }

    [Fact]
    public async Task Verify_rejects_a_signature_that_recovers_to_another_address()
    {
        // La vérification est RÉELLE : on altère la signature d'un octet et elle doit tomber.
        var request = await TestData.SignedRequestAsync(TestData.EurcSepoliaRequirements());
        var exact = request.PaymentPayload.AsExactEvm();
        var tampered = exact with { Signature = TestData.FlipOneByte(exact.Signature) };
        var altered = request with
        {
            PaymentPayload = request.PaymentPayload.WithExactEvm(tampered),
        };

        var response = await client.PostAsJsonAsync("/verify", altered, X402Json.Options, TestContext.Current.CancellationToken);
        var verify = await response.Content.ReadFromJsonAsync<VerifyResponse>(X402Json.Options, TestContext.Current.CancellationToken);

        verify!.IsValid.ShouldBeFalse();
        verify.InvalidReason.ShouldBe(X402ErrorReason.InvalidExactEvmPayloadSignature);
    }

    [Fact]
    public async Task Verify_rejects_an_amount_that_does_not_match_the_requirement()
    {
        var requirements = TestData.EurcSepoliaRequirements();
        var request = await TestData.SignedRequestAsync(requirements);
        var altered = request with
        {
            PaymentRequirements = requirements with { Amount = "999999" },
        };

        var response = await client.PostAsJsonAsync("/verify", altered, X402Json.Options, TestContext.Current.CancellationToken);
        var verify = await response.Content.ReadFromJsonAsync<VerifyResponse>(X402Json.Options, TestContext.Current.CancellationToken);

        verify!.IsValid.ShouldBeFalse();
        verify.InvalidReason.ShouldBe(
            X402ErrorReason.InvalidExactEvmPayloadAuthorizationValueMismatch);
    }

    [Fact]
    public async Task Verify_rejects_a_recipient_that_does_not_match_the_requirement()
    {
        var requirements = TestData.EurcSepoliaRequirements();
        var request = await TestData.SignedRequestAsync(requirements);
        var altered = request with
        {
            PaymentRequirements = requirements with
            {
                PayTo = "0x0000000000000000000000000000000000000001",
            },
        };

        var response = await client.PostAsJsonAsync("/verify", altered, X402Json.Options, TestContext.Current.CancellationToken);
        var verify = await response.Content.ReadFromJsonAsync<VerifyResponse>(X402Json.Options, TestContext.Current.CancellationToken);

        verify!.IsValid.ShouldBeFalse();
        verify.InvalidReason.ShouldBe(X402ErrorReason.InvalidExactEvmPayloadRecipientMismatch);
    }

    [Fact]
    public async Task Verify_rejects_an_expired_authorization()
    {
        var request = await TestData.SignedRequestAsync(
            TestData.EurcSepoliaRequirements(),
            validBefore: DateTimeOffset.UtcNow.AddMinutes(-1));

        var response = await client.PostAsJsonAsync("/verify", request, X402Json.Options, TestContext.Current.CancellationToken);
        var verify = await response.Content.ReadFromJsonAsync<VerifyResponse>(X402Json.Options, TestContext.Current.CancellationToken);

        verify!.IsValid.ShouldBeFalse();
        verify.InvalidReason.ShouldBe(
            X402ErrorReason.InvalidExactEvmPayloadAuthorizationValidBefore);
    }

    [Fact]
    public async Task Settle_returns_a_transaction_hash_and_records_the_nonce()
    {
        var request = await TestData.SignedRequestAsync(TestData.EurcSepoliaRequirements());

        var response = await client.PostAsJsonAsync("/settle", request, X402Json.Options, TestContext.Current.CancellationToken);
        var settle = await response.Content.ReadFromJsonAsync<SettleResponse>(X402Json.Options, TestContext.Current.CancellationToken);

        settle!.Success.ShouldBeTrue(settle.ErrorReason);
        settle.Transaction.ShouldStartWith("0x");
        settle.Network.ShouldBe("eip155:84532");
        facilitator.SettledNonces.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Settling_the_same_nonce_twice_is_visible_to_the_test()
    {
        // Le facilitateur simulé ne protège pas contre le double règlement : il le SIGNALE,
        // pour que le test d'idempotence du serveur (tâche 9) puisse échouer bruyamment.
        var request = await TestData.SignedRequestAsync(TestData.EurcSepoliaRequirements());

        await client.PostAsJsonAsync("/settle", request, X402Json.Options, TestContext.Current.CancellationToken);
        await client.PostAsJsonAsync("/settle", request, X402Json.Options, TestContext.Current.CancellationToken);

        facilitator.SettledNonces.Count.ShouldBe(2);
        facilitator.HasDoubleSettled.ShouldBeTrue();
    }

    [Fact]
    public async Task The_insufficient_funds_scenario_rejects_a_valid_signature()
    {
        facilitator.Scenario = FakeFacilitatorScenario.InsufficientFunds;
        var request = await TestData.SignedRequestAsync(TestData.EurcSepoliaRequirements());

        var response = await client.PostAsJsonAsync("/verify", request, X402Json.Options, TestContext.Current.CancellationToken);
        var verify = await response.Content.ReadFromJsonAsync<VerifyResponse>(X402Json.Options, TestContext.Current.CancellationToken);

        verify!.IsValid.ShouldBeFalse();
        verify.InvalidReason.ShouldBe(X402ErrorReason.InsufficientFunds);
    }

    [Fact]
    public async Task The_unsupported_asset_scenario_rejects_assets_outside_its_allow_list()
    {
        // Simule le facilitateur qui ne règle que l'USDC (§2.1.6) : l'EURC doit être refusé.
        facilitator.Scenario = FakeFacilitatorScenario.UnsupportedAsset;
        facilitator.SupportedAssets = [KnownAssets.UsdcBaseSepolia.Address];
        var request = await TestData.SignedRequestAsync(TestData.EurcSepoliaRequirements());

        var response = await client.PostAsJsonAsync("/verify", request, X402Json.Options, TestContext.Current.CancellationToken);
        var verify = await response.Content.ReadFromJsonAsync<VerifyResponse>(X402Json.Options, TestContext.Current.CancellationToken);

        verify!.IsValid.ShouldBeFalse();
        verify.InvalidReason.ShouldBe(X402ErrorReason.InvalidPaymentRequirements);
    }

    [Fact]
    public async Task The_settle_failure_scenario_reports_a_failed_settlement()
    {
        facilitator.Scenario = FakeFacilitatorScenario.SettleFailure;
        var request = await TestData.SignedRequestAsync(TestData.EurcSepoliaRequirements());

        var response = await client.PostAsJsonAsync("/settle", request, X402Json.Options, TestContext.Current.CancellationToken);
        var settle = await response.Content.ReadFromJsonAsync<SettleResponse>(X402Json.Options, TestContext.Current.CancellationToken);

        settle!.Success.ShouldBeFalse();
        settle.Transaction.ShouldBe("");
        settle.ErrorReason.ShouldBe(X402ErrorReason.InvalidTransactionState);
    }
}
