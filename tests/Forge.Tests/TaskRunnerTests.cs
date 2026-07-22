using Forge.Core;
using Forge.Core.Agents;
using Forge.Core.Db;
using Forge.Core.Llm;
using Forge.Core.Model;
using Forge.Core.Scheduling;
using Forge.Core.Secrets;
using Forge.Core.Workspaces;
using Microsoft.Data.Sqlite;
using TaskStatus = Forge.Core.Model.TaskStatus;

namespace Forge.Tests;

/// <summary>
/// The M1 acceptance tests: a task goes on the board and a commit comes out of
/// the bare repo — and a killed instance is replaced by a fresh one that resumes
/// from nothing but the workspace and the progress note.
/// </summary>
public class TaskRunnerTests : IDisposable
{
    private const string Project = "demo";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"forge-run-{Guid.NewGuid():N}");
    private readonly ForgePaths _paths;
    private readonly SqliteConnection _conn;
    private readonly TaskRepository _tasks;
    private readonly WorkspaceManager _workspaces;

    public TaskRunnerTests()
    {
        _paths = new ForgePaths(_dataRoot);
        ProjectBootstrap.Init(_paths, Project);
        _conn = Database.OpenProject(_paths.ProjectDb(Project));
        _tasks = new TaskRepository(_conn);
        _workspaces = new WorkspaceManager(_paths, Project);
    }

    public void Dispose()
    {
        _conn.Dispose();
        Directory.Delete(_dataRoot, recursive: true);
    }

    private TaskRecord ReadyTask(int budget = 100_000) =>
        _tasks.Transition(
            _tasks.Insert(TaskRecord.Create(
                TaskType.Feature, "Add greeting", "Create greeting.txt containing 'hello'", budget,
                assignedRole: AgentRole.Engineer, createdBy: "human")).Id,
            TaskStatus.Ready);

    /// <summary>Metered, as in production — an agent never sees an undecorated client.</summary>
    private TaskRunner Runner(ILlmClient llm, Forge.Core.Logging.ForgeLogger? logger = null) => new(
        _paths, Project, _conn,
        new MeteredLlmClient(llm, _conn, ModelPricing.Default),
        new SecretsVault(_paths.VaultDir), PromptLibrary.Resolve(), logger);

    /// <summary>Read a file out of the bare repo — the source of truth, not the workspace.</summary>
    private string ShowFromTrunk(string path) =>
        Git.Require(_paths.ProjectBareRepo(Project), "show", $"{WorkspaceManager.TrunkBranch}:{path}").Stdout;

    [Fact]
    public void Bootstrap_seeds_a_trunk_commit_so_the_first_task_has_something_to_branch_from()
    {
        Assert.Contains("# demo", ShowFromTrunk("PROJECT.md"));
        Assert.True(Directory.Exists(_paths.WorkspacesDir(Project)));
    }

    [Fact]
    public async Task A_task_run_emits_a_correlated_stream_queryable_at_project_and_task_scope()
    {
        var sink = new MemoryLogSink();
        var logger = new Forge.Core.Logging.ForgeLogger(sink, Project);

        var task = ReadyTask();
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("write_file", ("path", "greeting.txt"), ("content", "hello")),
            ScriptedLlmClient.Tool("done", ("summary", "Created greeting.txt.")));

        await Runner(llm, logger).RunAsync(_tasks.Get(task.Id));

        // The whole story is present, in order: claim → workspace → instance →
        // the model's calls and tool actions → merge → done.
        Assert.Contains("lifecycle.task_transition", sink.Types);   // claimed / in_progress / …
        Assert.Contains("lifecycle.instance_start", sink.Types);
        Assert.Contains("llm.call", sink.Types);
        Assert.Contains("tool.write_file", sink.Types);
        Assert.Contains("git.merge", sink.Types);
        Assert.Contains("lifecycle.instance_end", sink.Types);

        // The file-creation line reads like the client's own example.
        Assert.Contains(sink.Entries, e =>
            e.Type == Forge.Core.Logging.EventType.ToolWriteFile && e.Message.Contains("greeting.txt"));

        // Correlation: every one of these lines is scoped to this task, so a
        // task-level query returns them and a project-level query is a superset.
        Assert.NotEmpty(sink.ForTask(task.Id));
        Assert.All(sink.ForTask(task.Id), e => Assert.Equal(Project, e.Project));
        Assert.All(sink.Entries, e => Assert.Equal(task.Id, e.Task));  // all task-scoped in this run
    }

    [Fact]
    public void Initialising_a_project_that_already_exists_is_refused()
    {
        // The setup already created 'demo'; a second init must not clobber it.
        var ex = Assert.Throws<InvalidOperationException>(() => ProjectBootstrap.Init(_paths, Project));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public void A_registry_row_with_no_directory_still_reports_the_project_as_existing()
    {
        // Simulate a half-finished init: 'demo' is registered but its directory is
        // gone. A directory-only check would let a re-init through and then blow up
        // on the registry INSERT; the three-way check reports it cleanly instead.
        _conn.Dispose(); // release the file handle so the directory can be removed
        Directory.Delete(_paths.ProjectDir(Project), recursive: true);
        Assert.False(Directory.Exists(_paths.ProjectDir(Project)));

        var ex = Assert.Throws<InvalidOperationException>(() => ProjectBootstrap.Init(_paths, Project));
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public async Task A_completed_task_lands_in_the_bare_repo_and_the_workspace_is_cleaned_up()
    {
        var task = ReadyTask();
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("write_file", ("path", "greeting.txt"), ("content", "hello")),
            ScriptedLlmClient.Tool("done", ("summary", "Created greeting.txt with 'hello'.")));

        var outcome = await Runner(llm).RunNextAsync(AgentRole.Engineer);

        Assert.NotNull(outcome);
        Assert.Equal(TaskStatus.Done, outcome.Status);
        Assert.Equal("hello\n", ShowFromTrunk("greeting.txt"));
        Assert.False(_workspaces.Exists(task.Id));

        var record = _tasks.Get(task.Id);
        Assert.Equal(TaskStatus.Done, record.Status);
        Assert.Equal($"task/{task.Id}-add-greeting", record.BranchName);
        Assert.True(record.TokensSpent > 0);
    }

    [Fact]
    public async Task Merge_state_is_read_from_git_so_a_false_done_claim_blocks_instead_of_merging()
    {
        var task = ReadyTask();
        // The agent claims success without ever writing anything.
        var llm = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("list_dir"),
            ScriptedLlmClient.Tool("done", ("summary", "All done — the feature works great.")));

        var outcome = await Runner(llm).RunAsync(_tasks.Get(task.Id));

        Assert.Equal(TaskStatus.Blocked, outcome.Status);
        Assert.Contains("no commits", outcome.Summary);
        Assert.Contains("produced no commits", _tasks.Get(task.Id).ProgressNote!);

        var escalation = new MessageRepository(_conn).Pending("pm").Last();
        Assert.IsType<EscalationMessage>(escalation);
    }

    [Fact]
    public async Task Tasks_are_claimed_in_order_and_a_drained_board_returns_nothing()
    {
        var first = ReadyTask();
        var second = ReadyTask();
        var runner = Runner(new ScriptedLlmClient { Fallback = ScriptedLlmClient.Tool("done", ("summary", "ok")) });

        Assert.Equal(first.Id, (await runner.RunNextAsync(AgentRole.Engineer))!.TaskId);
        Assert.Equal(second.Id, (await runner.RunNextAsync(AgentRole.Engineer))!.TaskId);
        Assert.Null(await runner.RunNextAsync(AgentRole.Engineer));
    }

    [Fact]
    public async Task A_killed_instance_is_resumed_by_a_fresh_one_carrying_only_the_note_and_the_workspace()
    {
        var task = ReadyTask();

        // --- Instance 1: does half the work, writes a note, then burns its turns. ---
        var dying = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("write_file", ("path", "greeting.txt"), ("content", "hello")),
            ScriptedLlmClient.Tool("progress_note",
                ("note", "greeting.txt written. Still to do: add farewell.txt containing 'bye'.")))
        {
            Fallback = ScriptedLlmClient.Tool("list_dir"),
        };
        // Stand in for the process being killed: the loop is cut off mid-task.
        var recipe = AgentRecipe.Engineer with { IterationCap = 4 };
        var killed = await new AgentLoop(dying, _conn, new PromptAssembler(PromptLibrary.Resolve()), recipe)
            .RunAsync(Claim(task), new Forge.Core.Tools.ToolExecutor(
                _workspaces.Path(task.Id), recipe.ToolAllowlist, new SecretsVault(_paths.VaultDir)));

        Assert.Equal(EndReason.Iterations, killed.End);
        Assert.True(_workspaces.Exists(task.Id), "the workspace must survive so work isn't lost");
        _tasks.Transition(task.Id, TaskStatus.Blocked);

        // --- Instance 2: a genuinely fresh client. It has never seen the conversation. ---
        var resuming = new ScriptedLlmClient(
            ScriptedLlmClient.Tool("write_file", ("path", "farewell.txt"), ("content", "bye")),
            ScriptedLlmClient.Tool("done", ("summary", "Resumed and added farewell.txt.")));

        _tasks.Transition(task.Id, TaskStatus.Ready);
        var outcome = await Runner(resuming).RunNextAsync(AgentRole.Engineer);

        // It was handed the predecessor's note and nothing else.
        Assert.Contains("Still to do: add farewell.txt", resuming.Requests[0].Messages[0].Content);
        Assert.Single(resuming.Requests[0].Messages); // a fresh conversation, not a continued one

        // Both halves of the work are in the bare repo.
        Assert.NotNull(outcome);
        Assert.Equal(TaskStatus.Done, outcome.Status);
        Assert.Equal("hello\n", ShowFromTrunk("greeting.txt"));
        Assert.Equal("bye\n", ShowFromTrunk("farewell.txt"));

        // Two distinct agent instances are on the record for the one task.
        var instances = new AgentInstanceRepository(_conn).ForTask(task.Id);
        Assert.Equal(2, instances.Count);
        Assert.Equal([EndReason.Iterations, EndReason.Done], instances.Select(i => i.EndReason));
    }

    /// <summary>Drive the claim + workspace half of the runner without running the loop.</summary>
    private TaskRecord Claim(TaskRecord task)
    {
        _tasks.Transition(task.Id, TaskStatus.Claimed);
        _tasks.Transition(task.Id, TaskStatus.InProgress);
        _workspaces.Prepare(task, WorkspaceManager.BranchName(task));
        return _tasks.Get(task.Id);
    }
}
