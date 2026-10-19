using AccFiltering.Config;
using AccFiltering.Models;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace AccFiltering.Services;

/// <summary>Builds the copy plan and runs server-side copies with a concurrency limit.</summary>
public sealed class CopyService
{
    private readonly ApsClient _client;
    private readonly AppSettings _settings;
    private readonly ILogger<CopyService> _logger;

    /// <summary>Creates the service.</summary>
    public CopyService(ApsClient client, AppSettings settings, ILogger<CopyService> logger)
    {
        _client = client;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Turns the Excel list, source matches, and destination names into report rows.
    /// <paramref name="limit"/> applies to copy operations, not to files that already exist when overwrite is off.
    /// </summary>
    public IReadOnlyList<CopyReportRow> BuildPlan(
        IReadOnlyList<string> excelNames,
        MatchResult match,
        IReadOnlyDictionary<string, AccItem> destinationByKey,
        bool execute,
        bool overwrite,
        int? limit)
    {
        var matchedByExcel = match.Matched.ToDictionary(entry => entry.ExcelName, StringComparer.Ordinal);
        var unmatchedByExcel = match.Unmatched.ToDictionary(entry => entry.ExcelName, StringComparer.Ordinal);
        var rows = new List<CopyReportRow>(excelNames.Count);
        var plannedCopies = 0;

        foreach (var excelName in excelNames)
        {
            var row = new CopyReportRow { ExcelName = excelName };
            if (!matchedByExcel.TryGetValue(excelName, out var matched))
            {
                row.Status = CopyStatus.NotFound;
                if (unmatchedByExcel.TryGetValue(excelName, out var unmatched) && unmatched.SuggestionName is not null)
                {
                    var score = unmatched.SuggestionScore?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    row.Error = $"Closest name: {unmatched.SuggestionName} ({score}). Not copied.";
                }

                rows.Add(row);
                continue;
            }

            row.MatchedName = matched.Item.DisplayName;
            row.SourceItemId = matched.Item.Id;
            row.SourceVersionId = matched.Item.TipVersionId ?? "";

            if (string.IsNullOrWhiteSpace(matched.Item.TipVersionId))
            {
                row.Status = CopyStatus.Failed;
                row.Error = "The source item has no tip version.";
                rows.Add(row);
                continue;
            }

            var key = NameMatcher.Normalize(matched.Item.DisplayName);
            destinationByKey.TryGetValue(key, out var existing);
            if (existing is not null && !overwrite)
            {
                row.Status = CopyStatus.SkippedExists;
                row.DestinationItemId = existing.Id;
                row.Error = "Already exists in the destination folder.";
                rows.Add(row);
                continue;
            }

            if (limit is int max && plannedCopies >= max)
            {
                row.Status = CopyStatus.NotAttempted;
                row.Error = $"Outside --limit {max}.";
                if (existing is not null)
                {
                    row.DestinationItemId = existing.Id;
                }

                rows.Add(row);
                continue;
            }

            plannedCopies++;
            if (existing is not null)
            {
                row.DestinationItemId = existing.Id;
            }

            row.Status = execute ? CopyStatus.Pending : CopyStatus.DryRun;
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Copies every pending row. Authorization failures cancel the rest of the run.</summary>
    public async Task ExecuteAsync(
        IReadOnlyList<CopyReportRow> rows,
        string destinationFolderId,
        CancellationToken cancellationToken)
    {
        var work = rows.Where(row => row.Status == CopyStatus.Pending).ToList();
        if (work.Count == 0)
        {
            return;
        }

        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
            [
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn()
            ])
            .StartAsync(async context =>
            {
                var task = context.AddTask("Server-side copy", maxValue: work.Count);
                using var gate = new SemaphoreSlim(_settings.MaxParallelism);
                var copies = work.Select(row => CopyOneAsync(
                    row,
                    destinationFolderId,
                    gate,
                    task,
                    runCancellation,
                    cancellationToken));

                try
                {
                    await Task.WhenAll(copies).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    var auth = Find<ApsAuthException>(exception);
                    if (auth is not null)
                    {
                        throw auth;
                    }

                    var forbidden = Find<ApsForbiddenException>(exception);
                    if (forbidden is not null)
                    {
                        throw forbidden;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                }
            }).ConfigureAwait(false);
    }

    private async Task CopyOneAsync(
        CopyReportRow row,
        string destinationFolderId,
        SemaphoreSlim gate,
        ProgressTask task,
        CancellationTokenSource runCancellation,
        CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            await gate.WaitAsync(runCancellation.Token).ConfigureAwait(false);
            acquired = true;
            CreateCopyResult result;
            if (!string.IsNullOrEmpty(row.DestinationItemId))
            {
                result = await _client.CopyVersionAsync(
                    row.MatchedName,
                    row.DestinationItemId,
                    row.SourceVersionId,
                    FolderKindDetector.FileVersionExtension,
                    runCancellation.Token).ConfigureAwait(false);
            }
            else
            {
                result = await _client.CopyItemAsync(
                    row.MatchedName,
                    destinationFolderId,
                    row.SourceVersionId,
                    FolderKindDetector.FileItemExtension,
                    FolderKindDetector.FileVersionExtension,
                    runCancellation.Token).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(result.ResourceId))
                {
                    row.DestinationItemId = result.ResourceId;
                }
            }

            if (result.Conflict)
            {
                row.Status = CopyStatus.SkippedExists;
                row.Error = "Name already exists in the destination (409).";
                _logger.LogInformation("SKIPPED_EXISTS {Name}", row.MatchedName);
            }
            else
            {
                row.Status = CopyStatus.Copied;
                row.Error = "";
                _logger.LogInformation("COPIED {Name}", row.MatchedName);
            }
        }
        catch (ApsAuthException)
        {
            runCancellation.Cancel();
            if (row.Status == CopyStatus.Pending)
            {
                row.Status = CopyStatus.Failed;
                row.Error = "Token expired or invalid, provide a fresh token";
            }

            throw;
        }
        catch (ApsForbiddenException)
        {
            runCancellation.Cancel();
            if (row.Status == CopyStatus.Pending)
            {
                row.Status = CopyStatus.Failed;
                row.Error = "Access was denied (403).";
            }

            throw;
        }
        catch (OperationCanceledException)
        {
            if (row.Status == CopyStatus.Pending)
            {
                row.Status = CopyStatus.Failed;
                row.Error = cancellationToken.IsCancellationRequested
                    ? "Cancelled."
                    : "Stopped because the token is expired or invalid, or the run was cancelled.";
            }
        }
        catch (Exception exception)
        {
            row.Status = CopyStatus.Failed;
            row.Error = exception.Message;
            _logger.LogError("FAILED {Name}: {Message}", row.MatchedName, exception.Message);
        }
        finally
        {
            if (acquired)
            {
                task.Increment(1);
                gate.Release();
            }
        }
    }

    private static T? Find<T>(Exception exception) where T : Exception
    {
        if (exception is T match)
        {
            return match;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions.OfType<T>().FirstOrDefault();
        }

        return exception.InnerException as T;
    }
}
