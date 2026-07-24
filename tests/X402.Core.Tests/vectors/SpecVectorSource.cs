using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace X402.Core.Tests.Vectors;

/// <summary>A protocol JSON example lifted verbatim from a vendored specification document.</summary>
public sealed record SpecVector(string File, int Index, string Kind, JsonNode Json)
{
    public override string ToString() => $"{File}#{Index} ({Kind})";
}

public static class SpecVectorSource
{
    private static readonly Regex JsonBlock =
        new(@"```json\s*\n(?<body>.*?)\n```", RegexOptions.Singleline | RegexOptions.Compiled);

    private static string SpecDirectory =>
        Path.Combine(AppContext.BaseDirectory, "vectors", "_spec");

    /// <summary>Every recognised protocol object found in the vendored specifications.</summary>
    public static IReadOnlyList<SpecVector> All { get; } = Load();

    public static IEnumerable<SpecVector> OfKind(string kind) =>
        All.Where(v => v.Kind == kind);

    private static List<SpecVector> Load()
    {
        var vectors = new List<SpecVector>();

        foreach (var path in Directory.EnumerateFiles(SpecDirectory, "*.md").OrderBy(p => p))
        {
            var name = Path.GetFileName(path);
            var index = 0;

            foreach (Match match in JsonBlock.Matches(File.ReadAllText(path)))
            {
                index++;

                // Some fenced blocks are illustrative and contain "..." placeholders.
                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(match.Groups["body"].Value);
                }
                catch (System.Text.Json.JsonException)
                {
                    continue;
                }

                foreach (var (kind, json) in Classify(node))
                {
                    vectors.Add(new SpecVector(name, index, kind, json));
                }
            }
        }

        return vectors;
    }

    /// <summary>
    /// Maps a JSON object to the protocol type it represents. Envelopes (JSON-RPC, facilitator
    /// requests) yield their inner protocol objects instead of themselves.
    /// </summary>
    private static IEnumerable<(string Kind, JsonNode Json)> Classify(JsonNode? node)
    {
        if (node is not JsonObject o)
        {
            yield break;
        }

        // JSON-RPC envelope from the MCP transport.
        if (o.ContainsKey("jsonrpc"))
        {
            var structured = o["result"]?["structuredContent"];
            if (structured is not null)
            {
                foreach (var inner in Classify(structured)) { yield return inner; }
            }

            var meta = o["params"]?["_meta"]?["x402/payment"];
            if (meta is not null)
            {
                foreach (var inner in Classify(meta)) { yield return inner; }
            }

            var response = o["result"]?["_meta"]?["x402/payment-response"];
            if (response is not null)
            {
                foreach (var inner in Classify(response)) { yield return inner; }
            }

            yield break;
        }

        // Facilitator request envelope: yields both of its members.
        if (o.ContainsKey("paymentPayload") && o.ContainsKey("paymentRequirements"))
        {
            foreach (var inner in Classify(o["paymentPayload"])) { yield return inner; }
            foreach (var inner in Classify(o["paymentRequirements"])) { yield return inner; }
            yield break;
        }

        if (o.ContainsKey("accepts")) { yield return ("PaymentRequired", o); yield break; }
        if (o.ContainsKey("accepted") && o.ContainsKey("payload"))
                                       { yield return ("PaymentPayload", o); yield break; }
        if (o.ContainsKey("isValid"))  { yield return ("VerifyResponse", o); yield break; }
        if (o.ContainsKey("kinds"))    { yield return ("SupportedResponse", o); yield break; }
        if (o.ContainsKey("success") && o.ContainsKey("transaction"))
                                       { yield return ("SettleResponse", o); yield break; }
        if (o.ContainsKey("scheme") && o.ContainsKey("payTo"))
                                       { yield return ("PaymentRequirements", o); yield break; }
    }
}
