using System.Data;
using Dapper;
using Forge.Core.Agents;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Logging;
using Forge.Core.Model;
using Forge.Core.Secrets;
using Forge.Core.Tools;
using Forge.Core.Workspaces;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Core.Scheduling;

public sealed record TaskRunOutcome(long TaskId, EndReason End, TaskStatus Status, string Summary);

/// <summary>
/// One serial worker (spec §1: v1 is one worker; the cap is config, not
/// architecture). Claims a task, gives an agent instance a jailed workspace,
/// then decides what actually happened by looking at git — not by believing the
/// agent's report.
/// </summary>
public sealed class TaskRunner(
    ForgePaths paths,
    string project,
    IDbConnection conn,
    ILlmClient llm,
    SecretsVault vault,
    PromptLibrary prompts,
    ForgeLogger? logger = null)
{
    private readonly TaskRepository _tasks = new(conn);
    private readonly MessageRepository _messages = new(conn);
    private readonly WorkspaceManager _workspaces = new(paths, project);
    private readonly ForgeLogger _log = logger ?? ForgeLogger.Null;

    /// <summary>
    /// Resume before claim, deliberately: a task left in_progress is a task whose
    /// worker was killed, and its workspace is still on disk. Picking up new work
    /// while abandoned work exists is how a queue leaks tasks.
    /// </summary>
    public TaskRecord? NextTask(AgentRole role)
    {
        var roleName = SnakeCaseEnum.ToSnakeCase(role);
        var row = conn.QueryFirstOrDefault<long?>("""
            SELECT id FROM tasks
            WHERE assigned_role = @roleName AND status IN ('in_progress', 'claimed')
            ORDER BY id LIMIT 1
            """, new { roleName })
            ?? conn.QueryFirstOrDefault<long?>("""
            SELECT t.id FROM tasks t
            WHERE t.assigned_role = @roleName AND t.status = 'ready'
              AND NOT EXISTS (
                SELECT 1 FROM task_deps d
                JOIN tasks dep ON dep.id = d.depends_on
                WHERE d.task_id = t.id AND dep.status != 'done')
            ORDER BY t.id LIMIT 1
            """, new { roleName });

        return row is { } id ? _tasks.Get(id) : null;
    }

    public async Task<TaskRunOutcome?> RunNextAsync(AgentRole role, CancellationToken ct = default)
    {
        var task = NextTask(role);
        return task is null ? null : await RunAsync(task, ct).ConfigureAwait(false);
    }

    public async Task<TaskRunOutcome> RunAsync(TaskRecord task, CancellationToken ct = default)
    {
        var recipe = AgentRecipe.For(task.AssignedRole
            ?? throw new InvalidOperationException($"Task {task.Id} has no assigned role."));
        var log = _log.For(task.Id);

        log.Message($"Starting task {task.Id}: {task.Title}");
        task = Claim(task, log);
        var branch = task.BranchName ?? WorkspaceManager.BranchName(task);
        if (task.BranchName is null) SetBranch(task.Id, branch);

        _workspaces.Prepare(task, branch);
        log.Event(EventType.GitBranch, $"prepared workspace on {branch}");
        var executor = new ToolExecutor(_workspaces.Path(task.Id), recipe.ToolAllowlist, vault);

        var loop = new AgentLoop(llm, conn, new PromptAssembler(prompts), recipe, _log);
        var result = await loop.RunAsync(_tasks.Get(task.Id), executor, ct).ConfigureAwait(false);

        return result.End == EndReason.Done
            ? Integrate(task, branch, result, log)
            : Park(task, result, log);
    }

    /// <summary>
    /// Claiming is a status transition guarded by the legal-transition map and an
    /// optimistic UPDATE, which is what makes it safe when the worker count rises
    /// above one without any change here.
    /// </summary>
    private TaskRecord Claim(TaskRecord task, ForgeLogger log)
    {
        if (task.Status == TaskStatus.Ready) Transition(task.Id, TaskStatus.Claimed, log);
        if (_tasks.Get(task.Id).Status == TaskStatus.Claimed)
            Transition(task.Id, TaskStatus.InProgress, log);
        return _tasks.Get(task.Id);
    }

    /// <summary>Every status change is one log line, so the task's walk through the board is legible.</summary>
    private void Transition(long taskId, TaskStatus to, ForgeLogger log)
    {
        var from = _tasks.Get(taskId).Status;
        _tasks.Transition(taskId, to);
        log.Event(EventType.TaskTransition,
            $"{SnakeCaseEnum.ToSnakeCase(from)} → {SnakeCaseEnum.ToSnakeCase(to)}");
    }

    private void SetBranch(long taskId, string branch) =>
        conn.Execute("UPDATE tasks SET branch_name = @branch WHERE id = @taskId", new { taskId, branch });

    /// <summary>
    /// The agent said it was done. Whether it was is decided here, from git.
    ///
    /// M1 has no reviewer and no QA, so the task walks in_review → merging → qa →
    /// done mechanically with a status message recording that the gates were
    /// unmanned. The state machine stays honest and M4/M5 replace these auto-passes
    /// with real ones rather than adding new states.
    /// </summary>
    private TaskRunOutcome Integrate(TaskRecord task, string branch, AgentRunResult result, ForgeLogger log)
    {
        _workspaces.CommitAll(task.Id, $"task({task.Id}): {task.Title}");

        if (!_workspaces.HasCommitsAhead(task.Id, branch))
        {
            var note = "Agent reported done but produced no commits — nothing to merge.";
            _tasks.SetProgressNote(task.Id, $"{note} Previous note: {result.ProgressNote}");
            Transition(task.Id, TaskStatus.Blocked, log);
            log.Event(EventType.ErrorInternal, note);
            Notify(task.Id, MessageType.Escalation, "pm", note);
            return new TaskRunOutcome(task.Id, result.End, TaskStatus.Blocked, note);
        }

        _workspaces.PushBranch(task.Id, branch);
        log.Event(EventType.GitPush, $"pushed {branch}");
        Transition(task.Id, TaskStatus.InReview, log);
        Notify(task.Id, MessageType.Status, "pm",
            "M1: no reviewer configured — review gate auto-passed. Principal review lands in M4.");

        Transition(task.Id, TaskStatus.Merging, log);
        var sha = _workspaces.MergeToTrunk(task.Id, branch, $"merge {branch} into {WorkspaceManager.TrunkBranch}");
        log.Event(EventType.GitMerge, $"{branch} → {WorkspaceManager.TrunkBranch} @ {sha[..Math.Min(8, sha.Length)]}");

        Transition(task.Id, TaskStatus.Qa, log);
        Notify(task.Id, MessageType.Status, "pm",
            "M1: no QA configured — QA gate auto-passed. Black-box QA lands in M5.");

        Transition(task.Id, TaskStatus.Done, log);
        _workspaces.Discard(task.Id);

        var summary = $"Merged {branch} into {WorkspaceManager.TrunkBranch} at {sha[..Math.Min(8, sha.Length)]}.";
        log.Message($"Task {task.Id} complete — {summary}");
        Notify(task.Id, MessageType.Status, "pm", $"{summary} {result.ProgressNote}");
        return new TaskRunOutcome(task.Id, result.End, TaskStatus.Done, summary);
    }

    /// <summary>
    /// Budget, iteration cap, escalation or crash. The workspace is deliberately
    /// left on disk: it plus the progress note are what the next instance resumes
    /// from. Nothing is thrown away until it is merged.
    /// </summary>
    private TaskRunOutcome Park(TaskRecord task, AgentRunResult result, ForgeLogger log)
    {
        _workspaces.CommitAll(task.Id, $"wip(task {task.Id}): {result.End} after {result.Iterations} turns");

        var current = _tasks.Get(task.Id).Status;
        if (current != TaskStatus.Blocked && TaskTransitions.IsLegal(current, TaskStatus.Blocked))
            Transition(task.Id, TaskStatus.Blocked, log);

        var summary = $"Instance {result.InstanceId} ended: {SnakeCaseEnum.ToSnakeCase(result.End)} " +
                      $"after {result.Iterations} turns. Workspace kept for resume.";

        // The supervisor already escalated a budget kill; don't double-report it.
        if (result.End is not (EndReason.Budget or EndReason.Escalated))
            Notify(task.Id, MessageType.Escalation, "pm", $"{summary} {result.Detail}".Trim());

        return new TaskRunOutcome(task.Id, result.End, _tasks.Get(task.Id).Status, summary);
    }

    private void Notify(long taskId, MessageType type, string to, string payload) =>
        _messages.Insert(Message.Create(type, "system", to, payload, taskId));
}
