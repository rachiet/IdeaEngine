using System.ComponentModel;
using Forge.Core;
using Forge.Core.Db;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Forge.Cli.Commands;

public sealed class LogCommand : Command<LogCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<project>")]
        [Description("Project name under the Forge data root.")]
        public required string Project { get; init; }

        [CommandOption("-t|--task <ID>")]
        [Description("Only show the trail for one task.")]
        public long? TaskId { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var paths = ForgePaths.Resolve();
        var dbPath = paths.ProjectDb(settings.Project);
        if (!File.Exists(dbPath))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]No project '{settings.Project}' at {dbPath}.[/]");
            return 1;
        }

        using var conn = Database.OpenProject(dbPath);
        var messages = new MessageRepository(conn).Log(settings.TaskId);
        var ledger = new LedgerRepository(conn);

        if (messages.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No messages logged yet.[/]");
        }
        else
        {
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("When (UTC)");
            table.AddColumn("From → To");
            table.AddColumn("Type");
            table.AddColumn("Task");
            table.AddColumn("Message");
            foreach (var m in messages)
            {
                table.AddRow(
                    Markup.Escape(m.CreatedAt ?? ""),
                    Markup.Escape($"{m.FromAgent} → {m.ToAgent}"),
                    Markup.Escape(Forge.Core.Model.SnakeCaseEnum.ToSnakeCase(m.Type)),
                    m.TaskId?.ToString() ?? "-",
                    Markup.Escape(m.Payload));
            }
            AnsiConsole.Write(table);
        }

        var (tokensIn, tokensOut, cost) = settings.TaskId is { } taskId
            ? ledger.TaskTotals(taskId)
            : ledger.ProjectTotals();
        var scope = settings.TaskId is { } t ? $" (task {t})" : "";
        AnsiConsole.MarkupLineInterpolated(
            $"[grey]Token spend{scope}: {tokensIn:N0} in / {tokensOut:N0} out, ${cost:F4}[/]");
        return 0;
    }
}
