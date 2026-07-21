using Forge.Cli.Commands;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("forge");

    config.AddCommand<LogCommand>("log")
        .WithDescription("Replay the project's conversation/decision trail and token spend from SQLite.");

    config.AddBranch("project", project =>
    {
        project.SetDescription("Manage client projects under the Forge data root.");
        project.AddCommand<ProjectInitCommand>("init")
            .WithDescription("Create a project's data directory: project.db, bare repo, workspaces.");
    });

    config.AddBranch("secrets", secrets =>
    {
        secrets.SetDescription("Manage the encrypted secrets vault. Agents only ever see {{secret:NAME}}.");
        secrets.AddCommand<SecretsSetCommand>("set")
            .WithDescription("Store a secret value (prompted, hidden) and register its name.");
        secrets.AddCommand<SecretsListCommand>("list")
            .WithDescription("List registered secret names (never values).");
    });
});
return app.Run(args);
