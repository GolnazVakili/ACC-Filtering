namespace AccFiltering.Models;

/// <summary>A folder returned by the Data Management API.</summary>
public sealed record AccFolder(
    string Id,
    string Name,
    string? FolderType,
    IReadOnlyList<string> AllowedTypes,
    IReadOnlyList<string> VisibleTypes,
    IReadOnlyList<string> ParentTitles);

/// <summary>A file item in an ACC folder.</summary>
public sealed class AccItem
{
    /// <summary>Creates an item. <paramref name="tipVersionId"/> may be filled in later.</summary>
    public AccItem(string id, string displayName, string? tipVersionId = null)
    {
        Id = id;
        DisplayName = displayName;
        TipVersionId = tipVersionId;
    }

    /// <summary>Item URN (<c>urn:adsk.wipprod:dm.lineage:...</c>).</summary>
    public string Id { get; }

    /// <summary>File name shown in ACC, including the extension.</summary>
    public string DisplayName { get; }

    /// <summary>Tip version URN (<c>urn:adsk.wipprod:fs.file:vf....?version=N</c>).</summary>
    public string? TipVersionId { get; set; }
}

/// <summary>Result of walking a <c>&gt;</c>-separated folder path.</summary>
public abstract record FolderResolveResult
{
    /// <summary>The path matched a folder.</summary>
    public sealed record Success(AccFolder Folder, IReadOnlyList<AccFolder> Chain) : FolderResolveResult;

    /// <summary>A path segment did not match any child.</summary>
    public sealed record NotFound(string Segment, string? ParentName, IReadOnlyList<string> AvailableNames) : FolderResolveResult;

    /// <summary>The same segment exists under more than one top folder.</summary>
    public sealed record Ambiguous(string Segment, IReadOnlyList<string> Options) : FolderResolveResult;
}

/// <summary>Whether a Docs folder accepts files or plan documents.</summary>
public enum DocsFolderKind
{
    /// <summary>Under Project Files. Copies use <c>items:autodesk.bim360:File</c>.</summary>
    ProjectFiles,

    /// <summary>Under Plans. Copies would use <c>items:autodesk.bim360:Document</c>.</summary>
    Plans,

    /// <summary>The folder metadata does not identify a supported Docs area.</summary>
    Unsupported
}
