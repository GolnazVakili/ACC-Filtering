using AccFiltering.Config;
using AccFiltering.Models;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace AccFiltering.Services;

/// <summary>Runs folder resolution, matching, the optional copy, and the report.</summary>
public sealed class CopyApp
{
    private readonly ApsClient _client;
    private readonly FolderResolver _folders;
    private readonly ExcelReader _excel;
    private readonly NameMatcher _matcher;
    private readonly CopyService _copies;
    private readonly ReportWriter _reports;
    private readonly ConsolePrompter _prompter;
    private readonly CliOptions _options;
    private readonly ApsConnection _connection;
    private readonly ILogger<CopyApp> _logger;

    /// <summary>Creates the workflow.</summary>
    public CopyApp(
        ApsClient client,
        FolderResolver folders,
        ExcelReader excel,
        NameMatcher matcher,
        CopyService copies,
        ReportWriter reports,
        ConsolePrompter prompter,
        CliOptions options,
        ApsConnection connection,
        ILogger<CopyApp> logger)
    {
        _client = client;
        _folders = folders;
        _excel = excel;
        _matcher = matcher;
        _copies = copies;
        _reports = reports;
        _prompter = prompter;
        _options = options;
        _connection = connection;
        _logger = logger;
    }

    /// <summary>Resolves both folders, matches names, and copies only when <c>--execute</c> was set and confirmed.</summary>
    public async Task<int> RunAsync(
        string excelPath,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connection.ClientId))
        {
            _logger.LogInformation("APS client ID was not provided.");
        }
        else
        {
            _logger.LogInformation("APS client ID {ClientId}", _connection.ClientId);
        }

        _logger.LogInformation("Hub {HubId}, project {ProjectId}", _connection.HubId, _connection.ProjectId);
        if (_options.ExecuteOverridesDryRun)
        {
            _logger.LogWarning("Both --dry-run and --execute were set. The copy will run.");
        }

        _logger.LogInformation(_options.Execute ? "Mode: execute" : "Mode: dry-run");

        var source = await ResolveAsync("Source folder path in ACC", sourcePath, requireProjectFiles: false, cancellationToken)
            .ConfigureAwait(false);
        var destination = await ResolveAsync(
            "Destination folder path in ACC (e.g. 04.Coordination > Projectwide > NWC)",
            destinationPath,
            requireProjectFiles: true,
            cancellationToken).ConfigureAwait(false);

        ExcelReadResult excel;
        try
        {
            excel = _excel.Read(excelPath);
        }
        catch (InvalidOperationException exception)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
            return 1;
        }
        catch (Exception exception)
        {
            AnsiConsole.MarkupLine($"[red]Could not read the Excel file. {Markup.Escape(exception.Message)}[/]");
            return 1;
        }

        foreach (var duplicate in excel.DuplicateNames)
        {
            AnsiConsole.MarkupLine($"[yellow]Duplicate Excel name ignored:[/] {Markup.Escape(duplicate)}");
        }

        if (excel.Names.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]The Model Name column has no values.[/]");
            return 1;
        }

        _logger.LogInformation(
            "Read {Count} names from sheet {Sheet}.",
            excel.Names.Count,
            excel.SheetName);

        var sourceItems = await _client.ListItemsAsync(source.Folder.Id, cancellationToken).ConfigureAwait(false);
        var destinationItemsTask = _client.ListItemsAsync(destination.Folder.Id, cancellationToken);
        var tipsTask = _client.EnsureTipVersionsAsync(sourceItems, cancellationToken);
        await Task.WhenAll(destinationItemsTask, tipsTask).ConfigureAwait(false);
        var destinationItems = await destinationItemsTask.ConfigureAwait(false);

        _logger.LogInformation(
            "Source folder has {SourceCount} files. Destination folder has {DestinationCount} files.",
            sourceItems.Count,
            destinationItems.Count);

        var match = _matcher.Match(excel.Names, sourceItems);
        foreach (var key in match.AmbiguousSourceKeys)
        {
            _logger.LogWarning("More than one source file normalizes to {Name}. The .nwc file is used when one exists.", key);
        }

        var destinationByKey = new Dictionary<string, AccItem>(StringComparer.Ordinal);
        foreach (var item in destinationItems)
        {
            var key = NameMatcher.Normalize(item.DisplayName);
            if (key.Length > 0 && !destinationByKey.ContainsKey(key))
            {
                destinationByKey[key] = item;
            }
        }

        var rows = _copies.BuildPlan(
            excel.Names,
            match,
            destinationByKey,
            _options.Execute,
            _options.Overwrite,
            _options.Limit);

        PrintSuggestions(rows);
        PrintPlan(source, destination, excel, rows);

        var pending = rows.Count(row => row.Status is CopyStatus.Pending or CopyStatus.DryRun);
        var reportWritten = false;
        try
        {
            if (_options.Execute && pending > 0)
            {
                if (!AnsiConsole.Confirm("Copy these files on the server?", defaultValue: false))
                {
                    foreach (var row in rows.Where(row => row.Status == CopyStatus.Pending))
                    {
                        row.Status = CopyStatus.DryRun;
                        row.Error = "Copy cancelled.";
                    }

                    WriteReport(rows, excel.Names.Count, dryRunHint: false);
                    reportWritten = true;
                    AnsiConsole.MarkupLine("Copy cancelled. Nothing was copied.");
                    return 0;
                }

                await _copies.ExecuteAsync(rows, destination.Folder.Id, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!reportWritten)
            {
                foreach (var row in rows.Where(row => row.Status == CopyStatus.Pending))
                {
                    row.Status = CopyStatus.Failed;
                    if (string.IsNullOrWhiteSpace(row.Error))
                    {
                        row.Error = "Stopped before this file was copied.";
                    }
                }

                WriteReport(rows, excel.Names.Count, dryRunHint: !_options.Execute);
            }
        }

        return rows.Any(row => row.Status == CopyStatus.Failed) ? 1 : 0;
    }

    private void WriteReport(IReadOnlyList<CopyReportRow> rows, int excelCount, bool dryRunHint)
    {
        var reportPath = _reports.Write(rows);
        PrintSummary(rows, excelCount);
        AnsiConsole.MarkupLine($"Report: {Markup.Escape(reportPath)}");
        if (dryRunHint)
        {
            AnsiConsole.MarkupLine("Dry run only. No files were copied. Run with [bold]--execute --limit 2[/] for the first real copy.");
        }
    }

    private async Task<ResolvedFolder> ResolveAsync(
        string label,
        string initialPath,
        bool requireProjectFiles,
        CancellationToken cancellationToken)
    {
        var path = initialPath;
        while (true)
        {
            var result = await _folders.ResolveAsync(path, cancellationToken).ConfigureAwait(false);
            if (result is FolderResolveResult.Success success)
            {
                var detailed = await _client.GetFolderAsync(success.Folder.Id, cancellationToken).ConfigureAwait(false);
                var parentTitles = detailed.ParentTitles
                    .Concat(success.Chain.Take(success.Chain.Count - 1).Select(folder => folder.Name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                detailed = detailed with { ParentTitles = parentTitles };
                var display = string.Join(" > ", success.Chain.Select(folder => folder.Name));
                if (!requireProjectFiles)
                {
                    _logger.LogInformation("Source folder {Path} ({FolderId})", display, detailed.Id);
                    return new ResolvedFolder(detailed, display);
                }

                var kind = FolderKindDetector.Detect(detailed);
                if (kind == DocsFolderKind.ProjectFiles)
                {
                    _logger.LogInformation(
                        "Destination folder {Path} ({FolderId}) uses {Extension}",
                        display,
                        detailed.Id,
                        FolderKindDetector.FileItemExtension);
                    return new ResolvedFolder(detailed, display);
                }

                AnsiConsole.MarkupLine($"[red]{Markup.Escape(FolderKindDetector.ExplainUnsupported(detailed, kind))}[/]");
            }
            else if (result is FolderResolveResult.NotFound missing)
            {
                _prompter.PrintMissingSegment(missing.Segment, missing.ParentName, missing.AvailableNames);
            }
            else if (result is FolderResolveResult.Ambiguous ambiguous)
            {
                _prompter.PrintAmbiguous(ambiguous.Segment, ambiguous.Options);
            }

            path = _prompter.PromptFolderPath(label, null);
        }
    }

    private static void PrintSuggestions(IReadOnlyList<CopyReportRow> rows)
    {
        var suggestions = rows.Where(row => row.Status == CopyStatus.NotFound && row.Error.Length > 0).ToList();
        if (suggestions.Count == 0)
        {
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).Title("Closest names (not copied)");
        table.AddColumn("Excel name");
        table.AddColumn("Suggestion");
        foreach (var row in suggestions)
        {
            table.AddRow(Markup.Escape(row.ExcelName), Markup.Escape(row.Error));
        }

        AnsiConsole.Write(table);
    }

    private void PrintPlan(
        ResolvedFolder source,
        ResolvedFolder destination,
        ExcelReadResult excel,
        IReadOnlyList<CopyReportRow> rows)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("Plan");
        table.AddColumn("Item");
        table.AddColumn("Value");
        table.AddRow("Source", Markup.Escape(source.DisplayPath));
        table.AddRow("Destination", Markup.Escape(destination.DisplayPath));
        table.AddRow("Excel names", excel.Names.Count.ToString());
        table.AddRow("Duplicates ignored", excel.DuplicateNames.Count.ToString());
        table.AddRow("Matched", rows.Count(row => row.Status != CopyStatus.NotFound).ToString());
        table.AddRow("Not found", rows.Count(row => row.Status == CopyStatus.NotFound).ToString());
        table.AddRow("Already in destination", rows.Count(row => row.Status == CopyStatus.SkippedExists).ToString());
        table.AddRow(_options.Execute ? "Will copy" : "Would copy", rows.Count(row => row.Status is CopyStatus.Pending or CopyStatus.DryRun).ToString());
        table.AddRow("Mode", _options.Execute ? "execute" : "dry-run");
        table.AddRow("Overwrite", _options.Overwrite ? "yes, new version via copyFrom" : "no");
        table.AddRow("Limit", _options.Limit?.ToString() ?? "none");
        AnsiConsole.Write(table);
    }

    private static void PrintSummary(IReadOnlyList<CopyReportRow> rows, int excelCount)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("Summary");
        table.AddColumn("Result");
        table.AddColumn("Count");
        table.AddRow("Excel names", excelCount.ToString());
        table.AddRow("Matched", rows.Count(row => row.Status != CopyStatus.NotFound).ToString());
        table.AddRow("Copied", rows.Count(row => row.Status == CopyStatus.Copied).ToString());
        table.AddRow("Skipped", rows.Count(row => row.Status == CopyStatus.SkippedExists).ToString());
        table.AddRow("Not found", rows.Count(row => row.Status == CopyStatus.NotFound).ToString());
        table.AddRow("Not attempted", rows.Count(row => row.Status == CopyStatus.NotAttempted).ToString());
        table.AddRow("Dry run", rows.Count(row => row.Status == CopyStatus.DryRun).ToString());
        table.AddRow("Failed", rows.Count(row => row.Status == CopyStatus.Failed).ToString());
        AnsiConsole.Write(table);
    }

    private sealed record ResolvedFolder(AccFolder Folder, string DisplayPath);
}
