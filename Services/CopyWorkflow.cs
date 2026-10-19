using AccFiltering.Config;
using AccFiltering.Models;
using Microsoft.Extensions.Logging;

namespace AccFiltering.Services;

/// <summary>Inputs for one non-interactive copy run. The token is not written anywhere.</summary>
public sealed class CopyWorkflowRequest
{
    /// <summary>Connection values typed by the user.</summary>
    public required ApsConnection Connection { get; init; }

    /// <summary>Full path of the uploaded Excel file.</summary>
    public required string ExcelPath { get; init; }

    /// <summary>Source folder path inside ACC.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Destination folder path inside ACC.</summary>
    public required string DestinationPath { get; init; }

    /// <summary>When true, perform the server-side copy. When false, only build the plan.</summary>
    public bool Execute { get; init; }

    /// <summary>When true, an existing destination name gets a new version.</summary>
    public bool Overwrite { get; init; }

    /// <summary>Maximum number of copy operations.</summary>
    public int? Limit { get; init; }

    /// <summary>Retry and page settings.</summary>
    public AppSettings Settings { get; init; } = new()
    {
        MaxParallelism = 3,
        PageLimit = 200,
        MaxRetries = 5,
        SourceFolderPath = AppSettings.DefaultSourceFolderPath
    };
}

/// <summary>Outcome of a non-interactive run, including the report rows.</summary>
public sealed class CopyWorkflowResult
{
    /// <summary>True when the run finished, including a dry-run with no failed copies.</summary>
    public bool Ok { get; init; }

    /// <summary>User-facing error. Empty when the run produced a report.</summary>
    public string Message { get; init; } = "";

    /// <summary>Folders the user can pick when a path segment was not found.</summary>
    public IReadOnlyList<string> AvailableFolders { get; init; } = [];

    /// <summary>Resolved source path.</summary>
    public string SourcePath { get; init; } = "";

    /// <summary>Resolved destination path.</summary>
    public string DestinationPath { get; init; } = "";

    /// <summary>Unique Excel names.</summary>
    public int ExcelCount { get; init; }

    /// <summary>Duplicate Excel values that were ignored.</summary>
    public IReadOnlyList<string> Duplicates { get; init; } = [];

    /// <summary>One row per Excel name.</summary>
    public IReadOnlyList<CopyReportRow> Rows { get; init; } = [];

    /// <summary>CSV text for the report download.</summary>
    public string ReportCsv { get; init; } = "";
}

/// <summary>Runs the copy without console prompts. Folder problems are returned to the caller.</summary>
public sealed class CopyWorkflow
{
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Creates the workflow.</summary>
    public CopyWorkflow(ILoggerFactory loggerFactory) => _loggerFactory = loggerFactory;

    /// <summary>Resolves folders, matches names, and copies when <see cref="CopyWorkflowRequest.Execute"/> is set.</summary>
    public async Task<CopyWorkflowResult> RunAsync(CopyWorkflowRequest request, CancellationToken cancellationToken)
    {
        using var client = new ApsClient(
            request.Connection,
            request.Settings,
            _loggerFactory.CreateLogger<ApsClient>());
        var folders = new FolderResolver(client, _loggerFactory.CreateLogger<FolderResolver>());
        var copies = new CopyService(client, request.Settings, _loggerFactory.CreateLogger<CopyService>());

        try
        {
            var source = await ResolveOnceAsync(client, folders, request.SourcePath, requireProjectFiles: false, cancellationToken)
                .ConfigureAwait(false);
            if (source.Failure is not null)
            {
                return source.Failure;
            }

            var destination = await ResolveOnceAsync(client, folders, request.DestinationPath, requireProjectFiles: true, cancellationToken)
                .ConfigureAwait(false);
            if (destination.Failure is not null)
            {
                return new CopyWorkflowResult
                {
                    Ok = false,
                    Message = destination.Failure.Message,
                    AvailableFolders = destination.Failure.AvailableFolders,
                    SourcePath = source.DisplayPath
                };
            }

            ExcelReadResult excel;
            try
            {
                excel = new ExcelReader().Read(request.ExcelPath);
            }
            catch (InvalidOperationException exception)
            {
                return Fail(exception.Message);
            }
            catch (Exception exception)
            {
                return Fail("Could not read the Excel file. " + exception.Message);
            }

            if (excel.Names.Count == 0)
            {
                return Fail("The Model Name column has no values.");
            }

            var sourceItems = await client.ListItemsAsync(source.Folder!.Id, cancellationToken).ConfigureAwait(false);
            var destinationItemsTask = client.ListItemsAsync(destination.Folder!.Id, cancellationToken);
            var tipsTask = client.EnsureTipVersionsAsync(sourceItems, cancellationToken);
            await Task.WhenAll(destinationItemsTask, tipsTask).ConfigureAwait(false);

            var match = new NameMatcher().Match(excel.Names, sourceItems);
            var destinationByKey = new Dictionary<string, AccItem>(StringComparer.Ordinal);
            foreach (var item in await destinationItemsTask.ConfigureAwait(false))
            {
                var key = NameMatcher.Normalize(item.DisplayName);
                if (key.Length > 0 && !destinationByKey.ContainsKey(key))
                {
                    destinationByKey[key] = item;
                }
            }

            var rows = copies.BuildPlan(excel.Names, match, destinationByKey, request.Execute, request.Overwrite, request.Limit);
            if (request.Execute && rows.Any(row => row.Status == CopyStatus.Pending))
            {
                await copies.ExecuteAsync(rows, destination.Folder.Id, cancellationToken).ConfigureAwait(false);
            }

            foreach (var row in rows.Where(row => row.Status == CopyStatus.Pending))
            {
                row.Status = CopyStatus.Failed;
                if (string.IsNullOrWhiteSpace(row.Error))
                {
                    row.Error = "Stopped before this file was copied.";
                }
            }

            var failed = rows.Any(row => row.Status == CopyStatus.Failed);
            return new CopyWorkflowResult
            {
                Ok = !failed,
                Message = failed
                    ? "Finished with one or more failed files."
                    : request.Execute ? "Copy finished." : "Dry run finished. No files were copied.",
                SourcePath = source.DisplayPath,
                DestinationPath = destination.DisplayPath,
                ExcelCount = excel.Names.Count,
                Duplicates = excel.DuplicateNames,
                Rows = rows,
                ReportCsv = ReportWriter.ToCsv(rows)
            };
        }
        catch (ApsAuthException exception)
        {
            return Fail(exception.Message);
        }
        catch (ApsForbiddenException exception)
        {
            return Fail(exception.Message);
        }
        catch (ApsRequestException exception)
        {
            return Fail(exception.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail("Cancelled.");
        }
    }

    private async Task<Resolved> ResolveOnceAsync(
        ApsClient client,
        FolderResolver folders,
        string path,
        bool requireProjectFiles,
        CancellationToken cancellationToken)
    {
        var result = await folders.ResolveAsync(path, cancellationToken).ConfigureAwait(false);
        if (result is FolderResolveResult.NotFound missing)
        {
            var where = missing.ParentName is null ? "the top of the project" : $"'{missing.ParentName}'";
            return Resolved.Failed(Fail(
                $"Folder '{missing.Segment}' was not found under {where}.",
                missing.AvailableNames));
        }

        if (result is FolderResolveResult.Ambiguous ambiguous)
        {
            return Resolved.Failed(Fail(
                $"'{ambiguous.Segment}' was found in more than one place. Include the top folder in the path.",
                ambiguous.Options));
        }

        if (result is not FolderResolveResult.Success success)
        {
            return Resolved.Failed(Fail("The folder path could not be resolved."));
        }

        var detailed = await client.GetFolderAsync(success.Folder.Id, cancellationToken).ConfigureAwait(false);
        var parentTitles = detailed.ParentTitles
            .Concat(success.Chain.Take(success.Chain.Count - 1).Select(folder => folder.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        detailed = detailed with { ParentTitles = parentTitles };
        var display = string.Join(" > ", success.Chain.Select(folder => folder.Name));

        if (!requireProjectFiles)
        {
            return new Resolved(detailed, display, null);
        }

        var kind = FolderKindDetector.Detect(detailed);
        if (kind == DocsFolderKind.ProjectFiles)
        {
            return new Resolved(detailed, display, null);
        }

        return Resolved.Failed(Fail(FolderKindDetector.ExplainUnsupported(detailed, kind)));
    }

    private static CopyWorkflowResult Fail(string message, IReadOnlyList<string>? available = null) =>
        new()
        {
            Ok = false,
            Message = message,
            AvailableFolders = available ?? []
        };

    private sealed record Resolved(AccFolder? Folder, string DisplayPath, CopyWorkflowResult? Failure)
    {
        public static Resolved Failed(CopyWorkflowResult failure) => new(null, "", failure);
    }
}
