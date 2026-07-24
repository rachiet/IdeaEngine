namespace Forge.Core.Llm;

/// <summary>
/// USD per million tokens, keyed by model id prefix. Rates change monthly —
/// this is config, not architecture; unknown models cost 0 but still ledger
/// their token counts.
/// </summary>
public sealed class ModelPricing(IReadOnlyDictionary<string, (double InPerMTok, double OutPerMTok)> rates)
{
    public static ModelPricing Default { get; } = new(new Dictionary<string, (double, double)>
    {
        ["claude-fable-5"] = (10.00, 50.00),
        ["claude-opus-4"] = (5.00, 25.00),
        ["claude-sonnet-5"] = (3.00, 15.00),
        ["claude-haiku-4-5"] = (1.00, 5.00),
    });

    // Cache multipliers on the input rate (Anthropic ephemeral, 5-minute TTL):
    // a read is ~0.1x an uncached input token, a write ~1.25x. TokensIn is the
    // uncached remainder (full rate). Pricing these makes the ledger's cost the
    // real spend — so the caching win shows up as a lower number, not a hidden one.
    private const double CacheReadMultiplier = 0.10;
    private const double CacheWriteMultiplier = 1.25;

    public double CostUsd(string model, LlmUsage usage)
    {
        foreach (var (prefix, rate) in rates.OrderByDescending(r => r.Key.Length))
        {
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (usage.TokensIn * rate.InPerMTok
                        + usage.CacheReadTokens * rate.InPerMTok * CacheReadMultiplier
                        + usage.CacheWriteTokens * rate.InPerMTok * CacheWriteMultiplier
                        + usage.TokensOut * rate.OutPerMTok) / 1_000_000.0;
        }
        return 0.0;
    }
}
