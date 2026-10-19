using AccFiltering.Config;
using AccFiltering.Models;
using AccFiltering.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;

if (!CliOptions.TryParse(args, out var options, out var parseError, out var showHelp))
{
    if (showHelp)
    {
        AnsiConsole.WriteLine(CliOptions.HelpText);
        return 0;
    }

    AnsiConsole.MarkupLine($"[red]{Markup.Escape(parseError ?? "Invalid arguments.")}[/]");
    AnsiConsole.WriteLine();
    AnsiConsole.WriteLine(CliOptions.HelpText);
    return 2;
}

AppSettings settings;
try
{
    settings = AppSettings.Load();
}
catch (Exception exception)
{
    AnsiConsole.MarkupLine($"[red]Could not load appsettings.json. {Markup.Escape(exception.Message)}[/]");
    return 1;
}

var prompter = new ConsolePrompter();
var connection = prompter.PromptConnection(settings);
var excelPath = prompter.PromptExcelPath();
var sourcePath = prompter.PromptFolderPath("Source folder path in ACC", settings.SourceFolderPath);
var destinationPath = prompter.PromptFolderPath(
    "Destination folder path in ACC (e.g. 04.Coordination > Projectwide > NWC)",
    defaultValue: null);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
    ApplicationName = "AccFiltering"
});
builder.Configuration.Sources.Clear();
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new SpectreLoggerProvider(options.Verbose ? LogLevel.Debug : LogLevel.Information));

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(connection);
builder.Services.AddSingleton<ApsClient>();
builder.Services.AddSingleton<FolderResolver>();
builder.Services.AddSingleton<ExcelReader>();
builder.Services.AddSingleton<NameMatcher>();
builder.Services.AddSingleton<CopyService>();
builder.Services.AddSingleton<ReportWriter>();
builder.Services.AddSingleton<ConsolePrompter>();
builder.Services.AddSingleton<CopyApp>();

using var host = builder.Build();
var app = host.Services.GetRequiredService<CopyApp>();

try
{
    return await app.RunAsync(excelPath, sourcePath, destinationPath, cancellation.Token);
}
catch (ApsAuthException exception)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
    return 1;
}
catch (ApsForbiddenException exception)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
    return 1;
}
catch (OperationCanceledException)
{
    AnsiConsole.MarkupLine("[yellow]Cancelled.[/]");
    return 1;
}
catch (ApsRequestException exception)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
    return 1;
}
