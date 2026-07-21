using System.Data;
using System.Text.Json;
using Dapper;
using Forge.Core.Model;

namespace Forge.Core.Db;

// NOTE: enums are converted explicitly in repositories via SnakeCaseEnum, not via
// Dapper type handlers — Dapper binds enum parameters as their numeric value and
// never consults AddTypeHandler for them (verified by test; long-standing Dapper
// limitation). The CHECK constraints would reject the numbers, so explicit
// conversion at the repository boundary is the reliable path.

/// <summary>JSON list ⇄ TEXT (context_paths is JSON by design).</summary>
public sealed class StringListHandler : SqlMapper.TypeHandler<IReadOnlyList<string>>
{
    public override IReadOnlyList<string> Parse(object value) =>
        value is string s && !string.IsNullOrWhiteSpace(s)
            ? JsonSerializer.Deserialize<List<string>>(s) ?? []
            : [];

    public override void SetValue(IDbDataParameter parameter, IReadOnlyList<string>? value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value is null ? DBNull.Value : JsonSerializer.Serialize(value);
    }
}

/// <summary>"02-todos-read.md@v3" ⇄ RequirementsRef; parse-don't-validate at the DB boundary.</summary>
public sealed class RequirementsRefHandler : SqlMapper.TypeHandler<RequirementsRef?>
{
    public override RequirementsRef? Parse(object value) =>
        value is string s ? RequirementsRef.Parse(s) : null;

    public override void SetValue(IDbDataParameter parameter, RequirementsRef? value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value?.ToString() ?? (object)DBNull.Value;
    }
}

public static class TypeHandlerRegistry
{
    private static bool _registered;
    private static readonly object Gate = new();

    public static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered) return;
            SqlMapper.AddTypeHandler(new StringListHandler());
            SqlMapper.AddTypeHandler(new RequirementsRefHandler());
            _registered = true;
        }
    }
}
