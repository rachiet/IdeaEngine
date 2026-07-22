using System.Data;
using System.Text;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Logging;
using Forge.Core.Model;
using Forge.Core.Tools;

namespace Forge.Core.Agents;

public sealed record AgentRunResult(
    string InstanceId,
    EndReason End,
    int Iterations,
    string? ProgressNote,
    string? Detail = null,
    string? Reply = null);

/// <summary>
/// The harness's inner loop (spec §4.1):
///   assemble context → call LLM → parse tool calls → execute jailed tools →
///   append observations → repeat until done | budget | iteration cap | escalation.
///
/// Stateful within a run (the conversation is the working memory), stateless
/// across runs (nothing survives but the workspace, the progress note, and rows
/// in the DB). Everything in here is trusted mechanical code; everything coming
/// back from the model is untrusted output under supervision (Principle 6).
/// </summary>
public sealed class AgentLoop(
    ILlmClient llm,
    IDbConnection conn,
    PromptAssembler assembler,
    AgentRecipe recipe,
    ForgeLogger? logger = null)
{
    /// <summary>Turns with no tool call are dead weight; three in a row means the model is lost.</summary>
    private const int MaxEmptyTurns = 3;

    /// <summary>Validated here, not only in the factories: `with` bypasses those.</summary>
    private readonly AgentRecipe _recipe = recipe.Validate();

    private readonly TaskRepository _tasks = new(conn);
    private readonly MessageRepository _messages = new(conn);
    private readonly AgentInstanceRepository _instances = new(conn);
    private readonly ForgeLogger _baseLog = logger ?? ForgeLogger.Null;

    /// <summary>Work a task: the packet is the opening turn.</summary>
    public Task<AgentRunResult> RunAsync(
        TaskRecord task, ToolExecutor executor, CancellationToken ct = default) =>
        RunAsync(
            assembler.SystemPrompt(_recipe, task, executor.Jail),
            [new LlmMessage("user", PromptAssembler.TaskPacket(task))],
            executor, task, ct);

    /// <summary>Answer a client: the conversation so far is the opening state.</summary>
    public Task<AgentRunResult> RunChatAsync(
        IReadOnlyList<LlmMessage> conversation, ToolExecutor executor, CancellationToken ct = default) =>
        RunAsync(assembler.ChatSystemPrompt(_recipe, executor.Jail), conversation, executor, task: null, ct);

    private async Task<AgentRunResult> RunAsync(
        string system,
        IReadOnlyList<LlmMessage> seed,
        ToolExecutor executor,
        TaskRecord? task,
        CancellationToken ct)
    {
        // Task-scoped when working a task, project-scoped for a chat turn — so
        // every line this run emits carries the right correlation automatically.
        var log = task is { } scoped ? _baseLog.For(scoped.Id) : _baseLog;

        var instanceId = _instances.NewId(_recipe.InstancePrefix);
        _instances.Start(instanceId, _recipe.Role, _recipe.Model, task?.Id);
        log.Event(EventType.InstanceStart,
            $"{instanceId} ({SnakeCaseEnum.ToSnakeCase(_recipe.Role)}, {_recipe.Model})");

        var toolset = new AgentToolset(executor, conn, _recipe, task, log);
        var attribution = new LlmAttribution(instanceId, _recipe.Role, task?.Id);

        List<LlmMessage> conversation = [.. seed];
        var iterations = 0;
        var emptyTurns = 0;

        for (var turn = 1; turn <= _recipe.IterationCap; turn++)
        {
            iterations = turn;

            LlmResponse response;
            try
            {
                response = await llm.CompleteAsync(new LlmRequest
                {
                    Model = _recipe.Model,
                    System = system,
                    // Snapshot, not the live list: a request is a value, and an
                    // adapter that queues or retries must not see later turns appear
                    // inside a request it already accepted.
                    Messages = [.. conversation],
                    MaxTokens = _recipe.MaxTokens,
                    Attribution = attribution,
                }, ct).ConfigureAwait(false);
            }
            catch (BudgetExhaustedException ex)
            {
                // The supervisor already blocked the task and told the PM. The loop's
                // only job is to stop — budgets are enforced by not making the call.
                log.Event(EventType.LlmRefused, ex.Message);
                return Finish(instanceId, EndReason.Budget, iterations, toolset, task, log, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The provider is a network boundary: auth failures, rate limits and
                // outages are expected operating conditions, not bugs. Park the work
                // with its workspace and note intact so it can be resumed once the
                // condition clears — never lose work to someone else's 429.
                log.Event(EventType.ErrorProvider, $"turn {turn}: {ex.Message}");
                return Finish(instanceId, EndReason.Crash, iterations, toolset, task, log,
                    $"LLM call failed on turn {turn}: {ex.Message}");
            }

            log.Event(EventType.LlmCall,
                $"turn {turn}: {response.Usage.TokensIn + response.Usage.TokensOut} tokens " +
                $"(in {response.Usage.TokensIn} / out {response.Usage.TokensOut})");

            conversation.Add(new LlmMessage("assistant", response.Content));

            var calls = ToolCallParser.Parse(response.Content);
            if (calls.Count == 0)
            {
                if (++emptyTurns >= MaxEmptyTurns)
                {
                    return Finish(instanceId, EndReason.Crash, iterations, toolset, task, log,
                        $"No tool call in {MaxEmptyTurns} consecutive turns; the model is not acting.");
                }
                conversation.Add(new LlmMessage("user",
                    "No tool call found in your last turn. Nothing happened. Emit a " +
                    "<tool name=\"...\"> block, or finish your turn with the tool that ends it."));
                continue;
            }

            emptyTurns = 0;
            var observations = new StringBuilder();
            EndReason? end = null;

            foreach (var call in calls)
            {
                var outcome = await toolset.ExecuteAsync(call, ct).ConfigureAwait(false);
                observations.AppendLine($"[{call.Name}]").AppendLine(outcome.Observation).AppendLine();
                if (outcome.End is not { } reason) continue;
                end = reason;
                break; // the ending tool ends the turn; later calls in the batch are moot.
            }

            if (end is { } finalReason)
                return Finish(instanceId, finalReason, iterations, toolset, task, log, observations.ToString().Trim());

            AppendPendingMessages(task?.Id, observations, log);
            conversation.Add(new LlmMessage("user", observations.ToString().TrimEnd()));
        }

        return Finish(instanceId, EndReason.Iterations, iterations, toolset, task, log,
            $"Iteration cap of {_recipe.IterationCap} turns reached.");
    }

    /// <summary>
    /// Deliver anything the harness queued for this role mid-loop — most importantly
    /// the supervisor's 70% budget nudge, which is useless if it only lands in a table.
    /// </summary>
    private void AppendPendingMessages(long? taskId, StringBuilder observations, ForgeLogger log)
    {
        foreach (var message in _messages.Pending(SnakeCaseEnum.ToSnakeCase(_recipe.Role)))
        {
            if (message.TaskId != taskId) continue;
            observations
                .AppendLine($"[message: {SnakeCaseEnum.ToSnakeCase(message.Type)} from {message.FromAgent}]")
                .AppendLine(message.Payload)
                .AppendLine();
            _messages.SetStatus(message.Id, MessageStatus.Done);
            if (message.Type == MessageType.SystemNudge)
                log.Event(EventType.LlmNudge, message.Payload);
        }
    }

    /// <summary>
    /// Close out the instance. For task work a progress note is written
    /// unconditionally: the resume path depends on one existing, so it cannot be
    /// left to the model's discretion (Principle 6 — the harness enforces).
    /// </summary>
    private AgentRunResult Finish(
        string instanceId, EndReason end, int iterations,
        AgentToolset toolset, TaskRecord? task, ForgeLogger log, string? detail)
    {
        var note = toolset.LastProgressNote;
        if (note is null && task is not null)
        {
            note = $"Instance {instanceId} ended ({SnakeCaseEnum.ToSnakeCase(end)}) after {iterations} turns " +
                   $"without writing a progress note. {detail}".Trim();
            _tasks.SetProgressNote(task.Id, note);
        }

        _instances.End(instanceId, end);
        log.Event(EventType.InstanceEnd,
            $"{instanceId} ended: {SnakeCaseEnum.ToSnakeCase(end)} after {iterations} turns");
        return new AgentRunResult(instanceId, end, iterations, note, detail, toolset.LastReply);
    }
}
