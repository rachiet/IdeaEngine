using System.Data;
using Forge.Core.Db;

namespace Forge.Core.Design;

/// <summary>One requirement file and whether a task claims to implement it.</summary>
public sealed record RequirementCoverage(string File, bool Covered, IReadOnlyList<long> TaskIds);

public sealed record CoverageReport(IReadOnlyList<RequirementCoverage> Requirements)
{
    public IReadOnlyList<string> Uncovered =>
        Requirements.Where(r => !r.Covered).Select(r => r.File).ToList();

    public bool Complete => Uncovered.Count == 0;
}

/// <summary>
/// The PM coverage gate (spec §7): every requirement section must map to a task.
/// A mechanical check, not an LLM judgement — it compares the requirement files on
/// disk against the requirements_ref each task carries, so "did the Principal
/// leave a requirement unbuilt?" is answered from ground truth, not a claim.
/// </summary>
public static class CoverageGate
{
    public static CoverageReport Check(IDbConnection conn, string workspaceRoot)
    {
        var requirementsDir = Path.Combine(workspaceRoot, "docs", "requirements");
        var requirementFiles = Directory.Exists(requirementsDir)
            ? Directory.EnumerateFiles(requirementsDir, "*.md")
                .Select(Path.GetFileName)
                .Where(name => name is not null &&
                    !name.Equals("INDEX.md", StringComparison.OrdinalIgnoreCase))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList()
            : [];

        // Which tasks name each requirement file (the version is ignored for coverage —
        // a task against v2 still covers the requirement).
        var byFile = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var task in new TaskRepository(conn).List())
            if (task.RequirementsRef is { } req)
                (byFile.TryGetValue(req.File, out var list) ? list : byFile[req.File] = []).Add(task.Id);

        var coverage = requirementFiles
            .Select(file => new RequirementCoverage(
                file,
                byFile.ContainsKey(file),
                byFile.TryGetValue(file, out var ids) ? ids : []))
            .ToList();

        return new CoverageReport(coverage);
    }
}
