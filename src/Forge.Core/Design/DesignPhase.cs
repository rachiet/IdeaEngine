using System.Data;
using Forge.Core.Agents;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Logging;
using Forge.Core.Model;
using Forge.Core.Secrets;
using Forge.Core.Tools;
using Forge.Core.Workspaces;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Core.Design;

public sealed record DesignOutcome(
    EndReason End,
    int TasksCreated,
    CoverageReport Coverage,
    string Summary);

/// <summary>
/// The design phase (spec §7, M3): the Principal reads the requirements and
/// authors the structure — tree, CONVENTIONS.md, contracts, acceptance criteria —
/// and breaks the work into a task DAG.
///
/// It runs the same agent loop as everyone else, seeded with a design brief
/// instead of a task packet or a chat, on a long-lived clone of trunk (like the
/// PM's doc work). Two gates follow: the PM coverage gate is checked here
/// mechanically; the client sign-off gate is separate — tasks are born `created`
/// and only `Approve` makes them claimable.
/// </summary>
public sealed class DesignPhase(
    ForgePaths paths,
    string project,
    IDbConnection conn,
    ILlmClient llm,
    SecretsVault vault,
    PromptLibrary prompts,
    ForgeLogger? logger = null)
{
    private readonly AgentRecipe _recipe = AgentRecipe.Principal;
    private readonly TaskRepository _tasks = new(conn);
    private readonly ForgeLogger _log = logger ?? ForgeLogger.Null;

    public string WorkspacePath => paths.RoleWorkspace(project, "principal");

    public async Task<DesignOutcome> RunAsync(CancellationToken ct = default)
    {
        var before = _tasks.List().Count;
        _log.Message("Design phase: Principal starting");

        var workspace = new WorkspaceManager(paths, project).PrepareTrunkClone(WorkspacePath);
        var executor = new ToolExecutor(workspace, _recipe.ToolAllowlist, vault);
        var loop = new AgentLoop(llm, conn, new PromptAssembler(prompts), _recipe, _log);

        var result = await loop
            .RunChatAsync([new LlmMessage("user", Brief())], executor, ct)
            .ConfigureAwait(false);

        // The design docs are the Principal's artifacts; they go straight to trunk,
        // the same way the PM's requirements do. The client reviews via sign-off.
        var committed = new WorkspaceManager(paths, project)
            .CommitAndPushTrunk(WorkspacePath, "design: structure, conventions, contracts, task plan");
        if (committed) _log.Event(EventType.GitCommit, "committed design to trunk");

        var created = _tasks.List().Count - before;

        // PM coverage gate: read from the freshly-committed workspace so it reflects
        // exactly what the Principal wrote this run.
        var coverage = CoverageGate.Check(conn, workspace);
        _log.Message(coverage.Complete
            ? $"Coverage gate: all requirements mapped to tasks ({created} tasks created)"
            : $"Coverage gate: {coverage.Uncovered.Count} requirement(s) with no task: "
              + string.Join(", ", coverage.Uncovered));

        // The Principal ends with done(summary), not reply — its recipe has no reply
        // tool — so the plain-language summary for the PM and client rides the
        // progress note. Reply is kept first for any future chat-shaped design turn.
        var summary = result.Reply ?? result.ProgressNote
            ?? $"Design ended ({SnakeCaseEnum.ToSnakeCase(result.End)}) after {result.Iterations} turns.";
        return new DesignOutcome(result.End, created, coverage, summary);
    }

    /// <summary>
    /// The client sign-off gate (spec §7): the design was created as `created`
    /// tasks, claimable by no one. Approval flips every one of them to `ready`,
    /// which is the single point where "the client accepted the design" becomes
    /// mechanically true and engineers can start. Returns how many were released.
    ///
    /// Static because sign-off needs only the board and a log — no model, no
    /// workspace — so the CLI does not have to build a provider adapter to record
    /// a human decision.
    /// </summary>
    public static int Approve(IDbConnection conn, ForgeLogger? logger = null)
    {
        var tasks = new TaskRepository(conn);
        var pending = tasks.List().Where(t => t.Status == TaskStatus.Created).ToList();
        foreach (var task in pending)
            tasks.Transition(task.Id, TaskStatus.Ready);

        (logger ?? ForgeLogger.Null)
            .Message($"Client signed off on the design — {pending.Count} task(s) released to the board");
        return pending.Count;
    }

    private static string Brief() => """
        # Design brief

        The requirements for this project are in `docs/requirements/`. Read the
        INDEX and every section first, then design the system.

        Produce, on disk and on the board:
        - `CONVENTIONS.md` — the rules engineers follow (under a page).
        - The folder tree with a `MODULE.md` per module.
        - External contracts under `docs/design/03-contracts/` — the observable
          boundary QA will test against.
        - Acceptance criteria per feature.
        - A task DAG: one `create_task` per unit of work, each naming the
          requirement it implements, with `add_dependency` edges for ordering.

        Every requirement section must map to at least one task. Call `done` with a
        plain-language summary of the design when the plan is complete; it goes to
        the PM for a coverage check and to the client for sign-off.
        """;
}
