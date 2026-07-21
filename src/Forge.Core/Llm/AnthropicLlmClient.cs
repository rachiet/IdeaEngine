using Anthropic;
using Anthropic.Models.Messages;
using ForgeMessage = Forge.Core.Llm.LlmMessage;

namespace Forge.Core.Llm;

/// <summary>
/// The provider adapter (spec §11). Deliberately thin: it translates Forge's
/// request/response records to the Anthropic SDK and back, and does nothing else.
///
/// Never hand one of these to an agent loop directly — wrap it in
/// MeteredLlmClient so the ledger is written and budgets are enforced.
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    public const string TokenVariable = "ANTHROPIC_AUTH_TOKEN";

    private readonly AnthropicClient _client;

    /// <summary>
    /// Forge authenticates with an OAuth token (`sk-ant-oat…`), read from the
    /// environment that forge_env populates at startup — never from the database
    /// or a task packet. [DECIDED] we do not support API keys: one credential
    /// path means one thing to configure and one failure mode to recognise.
    ///
    /// The token rides `Authorization: Bearer`. Note that `ANTHROPIC_API_KEY`, if
    /// set anywhere in the environment, is picked up by the SDK and takes
    /// precedence — which is why that name must stay out of forge_env.
    /// </summary>
    public AnthropicLlmClient(string? authToken = null)
    {
        authToken ??= Environment.GetEnvironmentVariable(TokenVariable);

        // With no token in the environment, let the SDK resolve an `ant auth login`
        // profile itself — that is the same OAuth credential, just stored and
        // refreshed for us instead of pasted in by hand.
        _client = string.IsNullOrWhiteSpace(authToken)
            ? new AnthropicClient()
            : new AnthropicClient { AuthToken = authToken };
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var parameters = new MessageCreateParams
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            Messages = [.. request.Messages.Select(ToSdkMessage)],
        };
        if (request.System is { Length: > 0 } system) parameters = parameters with { System = system };

        var message = await _client.Messages.Create(parameters, cancellationToken: ct).ConfigureAwait(false);

        var text = string.Concat(message.Content
            .Select(block => block.Value)
            .OfType<TextBlock>()
            .Select(block => block.Text));

        return new LlmResponse
        {
            Content = text,
            StopReason = message.StopReason?.ToString(),
            Usage = new LlmUsage((int)message.Usage.InputTokens, (int)message.Usage.OutputTokens),
        };
    }

    private static MessageParam ToSdkMessage(ForgeMessage message) => new()
    {
        Role = message.Role == "assistant" ? Role.Assistant : Role.User,
        Content = message.Content,
    };
}
