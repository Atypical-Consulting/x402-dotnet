using System.Text;
using System.Text.Json.Nodes;
using FsCheck;
using FsCheck.Fluent;

namespace X402.PropertyTesting;

/// <summary>The families of hostile x402 header values the fuzz tests send.</summary>
public enum FuzzClass
{
    /// <summary>Arbitrary bytes, base64-encoded or sent raw as Latin-1.</summary>
    ArbitraryBytes,

    /// <summary>
    /// A valid encoding truncated or with junk inserted, plus empty and blank values. Mostly invalid
    /// base64; an inserted space still decodes, which exercises the decoder's whitespace leniency.
    /// </summary>
    NotBase64,

    /// <summary>Base64 of arbitrary UTF-8 text.</summary>
    Base64OfText,

    /// <summary>Base64 of a valid JSON document truncated at a random offset.</summary>
    TruncatedJson,

    /// <summary>Base64 of valid JSON with one member nulled, removed or retyped, at any depth.</summary>
    MutatedJson,

    /// <summary>Base64 of valid JSON with huge, negative or non-numeric protocol numerics and foreign versions.</summary>
    HostileValues,
}

/// <summary>
/// Deterministic generators of hostile header values. Linked into the Client tests too, so
/// <c>PAYMENT-SIGNATURE</c> and <c>PAYMENT-REQUIRED</c> are fuzzed from the same families.
/// </summary>
internal static class HeaderFuzz
{
    private static readonly string[] Numerics = ["amount", "value", "validAfter", "validBefore", "nonce", "maxTimeoutSeconds"];

    private static readonly string[] HostileNumerics =
    [
        new string('9', 10_000), "-1", "-0", "79228162514264337593543950336", "1e400", "0.5", " 1", "",
        "0x" + new string('z', 64), "0x" + new string('f', 64), "NaN", "١٢٣",
    ];

    private static readonly int[] Versions = [0, 1, 2, 3, -1, int.MaxValue, int.MinValue];

    /// <summary>
    /// <paramref name="count"/> hostile values of class <paramref name="fuzzClass"/>, from a fixed
    /// seed: the same inputs on every run, so a failure is reproducible from its printed header.
    /// </summary>
    public static IReadOnlyList<string> Sample(FuzzClass fuzzClass, Gen<string> validJson, int count = 200) =>
        Hostile(fuzzClass, validJson).Sample(count, new Rnd(26, (ulong)fuzzClass * 2 + 1), 50);

    private static Gen<string> Hostile(FuzzClass fuzzClass, Gen<string> validJson) => fuzzClass switch
    {
        FuzzClass.ArbitraryBytes =>
            from bytes in ArbMap.Default.GeneratorFor<byte[]>()
            from raw in Gen.Elements(true, false)
            select raw ? HeaderSafeLatin1(bytes) : Convert.ToBase64String(bytes),
        FuzzClass.NotBase64 => Gen.Frequency(
            (1, Gen.Elements("", " ", "\t", "*", "=", "====", "A", "AA=", "AAA", "e30", "e30==")),
            (4, from json in validJson
                from position in Gen.Choose(0, int.MaxValue)
                from junk in Gen.Elements("*", " ", "=", "-", "_", "%", "!", ".")
                from truncate in Gen.Elements(true, false)
                let encoded = Base64(json)
                select truncate
                    ? encoded[..(position % encoded.Length)] + junk
                    : encoded.Insert(position % encoded.Length, junk))),
        FuzzClass.Base64OfText =>
            from text in X402Arbitraries.Text
            select Base64(text),
        FuzzClass.TruncatedJson =>
            from json in validJson
            from cut in Gen.Choose(0, int.MaxValue)
            select Base64(json[..(cut % json.Length)]),
        FuzzClass.MutatedJson => Gen.Frequency(
            (1, Gen.Elements("null", "[]", "{}", "\"x\"", "42", "true", "[{}]").Select(Base64)),
            (6, from json in validJson
                from seed in Gen.Choose(0, int.MaxValue)
                select Base64(MutateOneMember(json, new Random(seed))))),
        FuzzClass.HostileValues =>
            from json in validJson
            from seed in Gen.Choose(0, int.MaxValue)
            select Base64(WithHostileValues(json, new Random(seed))),
        _ => throw new ArgumentOutOfRangeException(nameof(fuzzClass)),
    };

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    // What an HTTP header value can carry at all: no CR, LF, NUL or other control characters.
    private static string HeaderSafeLatin1(byte[] bytes) =>
        new(bytes.Where(b => b == '\t' || (b >= 0x20 && b != 0x7F)).Select(b => (char)b).ToArray());

    private static string MutateOneMember(string json, Random random)
    {
        var root = JsonNode.Parse(json)!;
        var slots = new List<(JsonNode Parent, string? Name, int Index)>();
        CollectSlots(root, slots);
        var (parent, name, index) = slots[random.Next(slots.Count)];
        var current = name is null ? parent[index] : parent[name];
        switch (random.Next(3))
        {
            case 0 when name is not null:
                parent.AsObject().Remove(name);
                break;
            case 1:
                Replace(Retype(current));
                break;
            default:
                Replace(null);
                break;
        }

        return root.ToJsonString();

        void Replace(JsonNode? replacement)
        {
            if (name is null)
            {
                parent[index] = replacement;
            }
            else
            {
                parent[name] = replacement;
            }
        }
    }

    private static JsonNode? Retype(JsonNode? node) => node?.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.String => JsonValue.Create(1),
        System.Text.Json.JsonValueKind.Number => JsonValue.Create("x"),
        System.Text.Json.JsonValueKind.Object => new JsonArray(),
        System.Text.Json.JsonValueKind.Array => new JsonObject(),
        System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => JsonValue.Create("true"),
        _ => new JsonObject(),
    };

    private static void CollectSlots(JsonNode node, List<(JsonNode Parent, string? Name, int Index)> slots)
    {
        if (node is JsonObject obj)
        {
            foreach (var (name, child) in obj.ToList())
            {
                slots.Add((obj, name, -1));
                if (child is not null)
                {
                    CollectSlots(child, slots);
                }
            }
        }
        else if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                slots.Add((array, null, i));
                if (array[i] is { } child)
                {
                    CollectSlots(child, slots);
                }
            }
        }
    }

    private static string WithHostileValues(string json, Random random)
    {
        var root = JsonNode.Parse(json)!;
        Visit(root);
        return root.ToJsonString();

        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var (name, child) in obj.ToList())
                {
                    if (name == "x402Version" && random.Next(3) == 0)
                    {
                        obj[name] = Versions[random.Next(Versions.Length)];
                    }
                    else if (Numerics.Contains(name) && random.Next(2) == 0)
                    {
                        var hostile = HostileNumerics[random.Next(HostileNumerics.Length)];
                        obj[name] = random.Next(4) == 0 && decimal.TryParse(hostile, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
                            ? JsonValue.Create(number)
                            : JsonValue.Create(hostile);
                    }
                    else
                    {
                        Visit(child);
                    }
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var child in array)
                {
                    Visit(child);
                }
            }
        }
    }
}
