using System.Net.Http.Json;
using System.Numerics;
using System.Security.Cryptography;
using X402.Assets;
using X402.Client.Signing;
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
        // Verification is REAL: we alter the signature by one byte and it must fail.
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
    public async Task Verify_refuses_a_non_integer_value_as_an_invalid_payload()
    {
        // Found by PaymentSignatureFuzzTests: the fake threw FormatException here, which TestServer
        // rethrew into the server under test as a 500 instead of answering with a refusal.
        var request = await TestData.SignedRequestAsync(TestData.EurcSepoliaRequirements());
        var exact = request.PaymentPayload.AsExactEvm();
        var altered = request with
        {
            PaymentPayload = request.PaymentPayload.WithExactEvm(
                exact with { Authorization = exact.Authorization with { Value = "" } }),
        };

        var response = await client.PostAsJsonAsync("/verify", altered, X402Json.Options, TestContext.Current.CancellationToken);
        var verify = await response.Content.ReadFromJsonAsync<VerifyResponse>(X402Json.Options, TestContext.Current.CancellationToken);

        verify!.InvalidReason.ShouldBe(X402ErrorReason.InvalidPayload);
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
    public async Task Verify_accepts_a_validBefore_of_two_to_the_256_minus_one()
    {
        // uint256 max is a common "never expires" bound; it must not overflow a long.
        var request = await SignedWithWindowAsync(
            validAfter: "0", validBefore: (BigInteger.Pow(2, 256) - 1).ToString());

        var verify = await VerifyAsync(request);

        verify.IsValid.ShouldBeTrue(verify.InvalidReason);
    }

    [Fact]
    public async Task Settle_succeeds_with_a_validBefore_of_two_to_the_256_minus_one()
    {
        var request = await SignedWithWindowAsync(
            validAfter: "0", validBefore: (BigInteger.Pow(2, 256) - 1).ToString());

        var response = await client.PostAsJsonAsync("/settle", request, X402Json.Options, TestContext.Current.CancellationToken);
        var settle = await response.Content.ReadFromJsonAsync<SettleResponse>(X402Json.Options, TestContext.Current.CancellationToken);

        settle!.Success.ShouldBeTrue(settle.ErrorReason);
    }

    [Fact]
    public async Task Verify_refuses_a_validAfter_above_long_max_with_the_validAfter_reason()
    {
        var request = await SignedWithWindowAsync(
            validAfter: ((BigInteger)long.MaxValue + 1).ToString(),
            validBefore: (BigInteger.Pow(2, 256) - 1).ToString());

        var verify = await VerifyAsync(request);

        verify.InvalidReason.ShouldBe(X402ErrorReason.InvalidExactEvmPayloadAuthorizationValidAfter);
    }

    [Fact]
    public async Task Verify_refuses_a_non_integer_validBefore_as_an_invalid_payload()
    {
        var request = await TestData.SignedRequestAsync(TestData.EurcSepoliaRequirements());
        var exact = request.PaymentPayload.AsExactEvm();
        var altered = request with
        {
            PaymentPayload = request.PaymentPayload.WithExactEvm(
                exact with { Authorization = exact.Authorization with { ValidBefore = "soon" } }),
        };

        var verify = await VerifyAsync(altered);

        verify.InvalidReason.ShouldBe(X402ErrorReason.InvalidPayload);
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
        // The fake facilitator does not guard against double settlement: it FLAGS it,
        // so the server's idempotency test (task 9) can fail loudly.
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
        // Simulates a facilitator that only settles USDC (§2.1.6): EURC must be refused.
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

    private async Task<VerifyResponse> VerifyAsync(FacilitatorRequest request)
    {
        var response = await client.PostAsJsonAsync("/verify", request, X402Json.Options, TestContext.Current.CancellationToken);
        var verify = await response.Content.ReadFromJsonAsync<VerifyResponse>(X402Json.Options, TestContext.Current.CancellationToken);
        return verify.ShouldNotBeNull();
    }

    // TestData takes DateTimeOffset bounds, which cannot express a uint256 above long.MaxValue.
    private static async Task<FacilitatorRequest> SignedWithWindowAsync(string validAfter, string validBefore)
    {
        var requirements = TestData.EurcSepoliaRequirements();
        var authorization = new Eip3009Authorization
        {
            From = TestData.PayerAddress,
            To = requirements.PayTo,
            Value = requirements.Amount,
            ValidAfter = validAfter,
            ValidBefore = validBefore,
            Nonce = "0x" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
        };
        var exact = await new PrivateKeyPaymentSigner(TestData.PayerPrivateKey)
            .SignAsync(requirements, authorization, KnownAssets.EurcBaseSepolia);

        return new FacilitatorRequest
        {
            PaymentPayload = new PaymentPayload
            {
                Resource = new ResourceInfo { Url = "https://api.example.com/premium" },
                Accepted = requirements,
                Payload = System.Text.Json.JsonSerializer.SerializeToElement(exact, X402Json.Options),
            },
            PaymentRequirements = requirements,
        };
    }
}
