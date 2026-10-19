namespace AccFiltering.Models;

/// <summary>Outcome written to the CSV report.</summary>
public enum CopyStatus
{
    /// <summary>Selected for an execute run and not finished yet.</summary>
    Pending,

    /// <summary>The server-side copy succeeded.</summary>
    Copied,

    /// <summary>The destination already has this file name.</summary>
    SkippedExists,

    /// <summary>The Excel name is not in the source folder.</summary>
    NotFound,

    /// <summary>The copy was attempted and the API rejected it.</summary>
    Failed,

    /// <summary>Dry-run: this file would be copied.</summary>
    DryRun,

    /// <summary>Matched, but left out because of <c>--limit</c>.</summary>
    NotAttempted
}

/// <summary>One Excel name and what happened to it.</summary>
public sealed class CopyReportRow
{
    /// <summary>Value from the Model Name column.</summary>
    public required string ExcelName { get; init; }

    /// <summary>ACC display name that matched, including <c>.nwc</c>.</summary>
    public string MatchedName { get; set; } = "";

    /// <summary>Source item id.</summary>
    public string SourceItemId { get; set; } = "";

    /// <summary>Source tip version id.</summary>
    public string SourceVersionId { get; set; } = "";

    /// <summary>Result for this row.</summary>
    public CopyStatus Status { get; set; }

    /// <summary>Item id in the destination folder, after a copy or when the name already exists.</summary>
    public string DestinationItemId { get; set; } = "";

    /// <summary>Failure detail, skip reason, or a fuzzy suggestion. Suggestions are never copied.</summary>
    public string Error { get; set; } = "";

    /// <summary>CSV value for <see cref="Status"/>.</summary>
    public string StatusText => Status switch
    {
        CopyStatus.Copied => "COPIED",
        CopyStatus.SkippedExists => "SKIPPED_EXISTS",
        CopyStatus.NotFound => "NOT_FOUND",
        CopyStatus.Failed => "FAILED",
        CopyStatus.DryRun => "DRY_RUN",
        CopyStatus.NotAttempted => "NOT_ATTEMPTED",
        CopyStatus.Pending => "FAILED",
        _ => "FAILED"
    };
}

/// <summary>An Excel name that matched one source item.</summary>
public sealed record MatchedName(string ExcelName, AccItem Item);

/// <summary>An Excel name with an optional nearest source name. The suggestion is not a match.</summary>
public sealed record UnmatchedName(string ExcelName, string? SuggestionName, double? SuggestionScore);

/// <summary>Name comparison between the Excel list and the source folder.</summary>
public sealed record MatchResult(
    IReadOnlyList<MatchedName> Matched,
    IReadOnlyList<UnmatchedName> Unmatched,
    IReadOnlyList<string> AmbiguousSourceKeys);

/// <summary>Model names read from the workbook.</summary>
public sealed record ExcelReadResult(
    IReadOnlyList<string> Names,
    IReadOnlyList<string> DuplicateNames,
    string SheetName);

/// <summary>Result of one create-item or create-version call.</summary>
public sealed record CreateCopyResult(bool Conflict, string? ResourceId);
