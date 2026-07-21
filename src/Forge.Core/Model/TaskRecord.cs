namespace Forge.Core.Model;

/// <summary>
/// One row of the tasks table. Construct new tasks via <see cref="Create"/> —
/// never insert a naked record — and change status only through
/// <see cref="TaskTransitions"/> (repositories enforce this).
/// </summary>
public sealed record TaskRecord
{
    public long Id { get; init; }
    public long? MilestoneId { get; init; }
    public required TaskType Type { get; init; }
    public required string Title { get; init; }
    public required string Objective { get; init; }
    public string? AcceptanceCriteria { get; init; }
    public IReadOnlyList<string> ContextPaths { get; init; } = [];
    public RequirementsRef? RequirementsRef { get; init; }
    public AgentRole? AssignedRole { get; init; }
    public TaskStatus Status { get; init; } = TaskStatus.Created;
    public required int TokenBudget { get; init; }
    public int TokensSpent { get; init; }
    public string? ProgressNote { get; init; }
    public string? BranchName { get; init; }
    public string? CreatedBy { get; init; }
    public string? CreatedAt { get; init; }
    public string? UpdatedAt { get; init; }

    public static TaskRecord Create(
        TaskType type,
        string title,
        string objective,
        int tokenBudget,
        long? milestoneId = null,
        string? acceptanceCriteria = null,
        IReadOnlyList<string>? contextPaths = null,
        RequirementsRef? requirementsRef = null,
        AgentRole? assignedRole = null,
        string? createdBy = null)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Task title must be non-empty.", nameof(title));
        if (string.IsNullOrWhiteSpace(objective))
            throw new ArgumentException("Task objective must be non-empty.", nameof(objective));
        if (tokenBudget <= 0)
            throw new ArgumentOutOfRangeException(nameof(tokenBudget), tokenBudget,
                "Token budget must be positive.");

        return new TaskRecord
        {
            Type = type,
            Title = title,
            Objective = objective,
            TokenBudget = tokenBudget,
            MilestoneId = milestoneId,
            AcceptanceCriteria = acceptanceCriteria,
            ContextPaths = contextPaths ?? [],
            RequirementsRef = requirementsRef,
            AssignedRole = assignedRole,
            CreatedBy = createdBy,
        };
    }
}

/// <summary>Thrown when a status change is not in the legal-transition map.</summary>
public sealed class IllegalTaskTransitionException(TaskStatus from, TaskStatus to)
    : InvalidOperationException($"Illegal task transition: {from} → {to}.")
{
    public TaskStatus From { get; } = from;
    public TaskStatus To { get; } = to;
}

/// <summary>
/// The only way a task status may change. Routing ("who acts next") is derived
/// from status via <see cref="RoleFor"/>, never stored — no "next actor" column.
/// </summary>
public static class TaskTransitions
{
    private static readonly IReadOnlyDictionary<TaskStatus, TaskStatus[]> Legal =
        new Dictionary<TaskStatus, TaskStatus[]>
        {
            [TaskStatus.Created] = [TaskStatus.Ready, TaskStatus.Cancelled],
            [TaskStatus.Ready] = [TaskStatus.Claimed, TaskStatus.Blocked, TaskStatus.Cancelled],
            [TaskStatus.Claimed] = [TaskStatus.InProgress, TaskStatus.Ready, TaskStatus.Blocked, TaskStatus.Cancelled],
            [TaskStatus.InProgress] = [TaskStatus.InReview, TaskStatus.Blocked, TaskStatus.Cancelled],
            [TaskStatus.InReview] = [TaskStatus.Merging, TaskStatus.InProgress, TaskStatus.Blocked, TaskStatus.Cancelled],
            [TaskStatus.Merging] = [TaskStatus.Qa, TaskStatus.InProgress, TaskStatus.Blocked],
            [TaskStatus.Qa] = [TaskStatus.Done, TaskStatus.InProgress, TaskStatus.Blocked],
            [TaskStatus.Blocked] = [TaskStatus.Ready, TaskStatus.Cancelled],
            [TaskStatus.Done] = [],
            [TaskStatus.Cancelled] = [],
        };

    public static bool IsLegal(TaskStatus from, TaskStatus to) => Legal[from].Contains(to);

    public static void Require(TaskStatus from, TaskStatus to)
    {
        if (!IsLegal(from, to)) throw new IllegalTaskTransitionException(from, to);
    }

    /// <summary>Static handoff map: which role acts on a task in this status.
    /// Null means the harness itself (or nobody) owns the state.</summary>
    public static AgentRole? RoleFor(TaskStatus status) => status switch
    {
        TaskStatus.InReview => AgentRole.Principal,
        TaskStatus.Qa => AgentRole.Qa,
        TaskStatus.Blocked => AgentRole.Pm,
        TaskStatus.Created => AgentRole.Pm,
        _ => null,
    };
}
