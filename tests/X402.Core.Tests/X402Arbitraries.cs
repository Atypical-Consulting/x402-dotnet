using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using X402.Assets;
using X402.Json;
using X402.Protocol;

namespace X402.PropertyTesting;

/// <summary>
/// FsCheck generators for structurally valid x402 v2 protocol objects. Linked into the AspNetCore
/// and Client test projects too, so every boundary is fuzzed from the same valid shapes.
/// </summary>
internal static class X402Arbitraries
{
    /// <summary>Every catalogued asset the generators draw from.</summary>
    public static AssetDescriptor[] Assets { get; } =
    [
        KnownAssets.EurcBaseSepolia, KnownAssets.UsdcBaseSepolia,
        KnownAssets.EurcBaseMainnet, KnownAssets.UsdcBaseMainnet,
    ];

    private static readonly char[] HexDigits = "0123456789abcdefABCDEF".ToCharArray();

    public static Gen<AssetDescriptor> Asset { get; } = Gen.Elements(Assets);

    /// <summary>Arbitrary .NET strings, including empty, control and non-BMP characters.</summary>
    public static Gen<string> Text { get; } =
        ArbMap.Default.GeneratorFor<NonNull<string>>().Select(s => s.Get);

    /// <summary><c>0x</c> followed by <paramref name="hexChars"/> hex digits in mixed case.</summary>
    public static Gen<string> Hex(int hexChars) =>
        Gen.Elements(HexDigits).ArrayOf(hexChars).Select(cs => "0x" + new string(cs));

    /// <summary>A 32-byte nonce, <c>0x</c> + 64 hex characters in mixed case.</summary>
    public static Gen<string> Nonce { get; } = Hex(64);

    /// <summary>A 20-byte EVM address in mixed (not checksum) case.</summary>
    public static Gen<string> Address { get; } = Hex(40);

    /// <summary>A decimal digit string of 1 to 10 000 digits, mostly short.</summary>
    public static Gen<string> AtomicAmount { get; } = Gen.Frequency(
        (8, Digits(1, 30)),
        (1, Digits(31, 10_000)));

    private static Gen<string> Digits(int min, int max) =>
        from length in Gen.Choose(min, max)
        from digits in Gen.Elements("0123456789".ToCharArray()).ArrayOf(length)
        select new string(digits);

    private static Gen<T?> OrNull<T>(Gen<T> gen) where T : class =>
        Gen.Frequency((1, Gen.Constant<T?>(null)), (3, gen.Select(v => (T?)v)));

    public static Gen<ResourceInfo> Resource { get; } =
        from url in Text
        from description in OrNull(Text)
        from mimeType in OrNull(Text)
        from tags in OrNull(Text.ListOf().Select(l => (IReadOnlyList<string>)l.ToList()))
        select new ResourceInfo { Url = url, Description = description, MimeType = mimeType, Tags = tags };

    public static Gen<PaymentRequirements> Requirements { get; } =
        from asset in Asset
        from amount in AtomicAmount
        from payTo in Address
        from timeout in Gen.Choose(0, int.MaxValue)
        from withExtra in Gen.Elements(true, false)
        select new PaymentRequirements
        {
            Scheme = "exact",
            Network = asset.Network,
            Amount = amount,
            Asset = asset.Address,
            PayTo = payTo,
            MaxTimeoutSeconds = timeout,
            Extra = withExtra
                ? JsonSerializer.SerializeToElement(
                    new Dictionary<string, string> { ["name"] = asset.Eip712Name, ["version"] = asset.Eip712Version },
                    X402Json.Options)
                : null,
        };

    public static Gen<PaymentRequired> PaymentRequired { get; } =
        from error in OrNull(Text)
        from resource in Resource
        from accepts in Requirements.ListOf()
        select new PaymentRequired { Error = error, Resource = resource, Accepts = accepts.ToList() };

    public static Gen<Eip3009Authorization> Authorization { get; } =
        from payer in Address
        from to in Address
        from value in AtomicAmount
        from validAfter in AtomicAmount
        from validBefore in AtomicAmount
        from nonce in Nonce
        select new Eip3009Authorization
        {
            From = payer,
            To = to,
            Value = value,
            ValidAfter = validAfter,
            ValidBefore = validBefore,
            Nonce = nonce,
        };

    public static Gen<PaymentPayload> PaymentPayload { get; } =
        from resource in OrNull(Resource)
        from accepted in Requirements
        from signature in Hex(130)
        from authorization in Authorization
        select new PaymentPayload
        {
            Resource = resource,
            Accepted = accepted,
            Payload = JsonSerializer.SerializeToElement(
                new ExactEvmPayload { Signature = signature, Authorization = authorization },
                X402Json.Options),
        };

    public static Gen<SettleResponse> SettleResponse { get; } =
        from success in Gen.Elements(true, false)
        from errorReason in OrNull(Text)
        from payer in OrNull(Address)
        from transaction in OrNull(Hex(64))
        from asset in Asset
        from amount in OrNull(AtomicAmount)
        select new SettleResponse
        {
            Success = success,
            ErrorReason = errorReason,
            Payer = payer,
            Transaction = transaction,
            Network = asset.Network,
            Amount = amount,
        };
}
