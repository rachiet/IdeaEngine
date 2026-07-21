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
        ["claude-fable-5"] = (25.00, 125.00),
        ["claude-opus-4"] = (15.00, 75.00),
        ["claude-sonnet-5"] = (3.00, 15.00),
        ["claude-haiku-4-5"] = (1.00, 5.00),
    });

    public double CostUsd(string model, LlmUsage usage)
    {
        foreach (var (prefix, rate) in rates.OrderByDescending(r => r.Key.Length))
        {
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return (usage.TokensIn * rate.InPerMTok + usage.TokensOut * rate.OutPerMTok) / 1_000_000.0;
        }
        return 0.0;
    }
}
