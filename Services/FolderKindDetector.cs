using AccFiltering.Models;

namespace AccFiltering.Services;

/// <summary>
/// Decides whether a destination folder is Project Files (<c>items:autodesk.bim360:File</c>)
/// or Plans (<c>items:autodesk.bim360:Document</c>).
/// </summary>
public static class FolderKindDetector
{
    /// <summary>Item extension for a folder under Project Files.</summary>
    public const string FileItemExtension = "items:autodesk.bim360:File";

    /// <summary>Version extension for a folder under Project Files.</summary>
    public const string FileVersionExtension = "versions:autodesk.bim360:File";

    /// <summary>Item extension for a folder under Plans.</summary>
    public const string DocumentItemExtension = "items:autodesk.bim360:Document";

    /// <summary>Version extension for a folder under Plans.</summary>
    public const string DocumentVersionExtension = "versions:autodesk.bim360:Document";

    /// <summary>Classifies a folder from its name, parent titles, visible types, and folder type.</summary>
    public static DocsFolderKind Detect(AccFolder folder)
    {
        if (folder.FolderType is "plan" or "drawing")
        {
            return DocsFolderKind.Plans;
        }

        var underPlans = HasTitle(folder.ParentTitles, "Plans")
            || (folder.Name.Equals("Plans", StringComparison.OrdinalIgnoreCase) && folder.ParentTitles.Count == 0);
        var underProjectFiles = HasTitle(folder.ParentTitles, "Project Files")
            || folder.Name.Equals("Project Files", StringComparison.OrdinalIgnoreCase);

        if (underPlans && !underProjectFiles)
        {
            return DocsFolderKind.Plans;
        }

        if (underProjectFiles && !underPlans)
        {
            return DocsFolderKind.ProjectFiles;
        }

        var fileVisible = EndsWithType(folder.VisibleTypes, ":File");
        var documentVisible = EndsWithType(folder.VisibleTypes, ":Document");
        if (documentVisible && !fileVisible)
        {
            return DocsFolderKind.Plans;
        }

        if (fileVisible)
        {
            return DocsFolderKind.ProjectFiles;
        }

        return DocsFolderKind.Unsupported;
    }

    /// <summary>Tells the user why an .nwc copy cannot use this folder.</summary>
    public static string ExplainUnsupported(AccFolder folder, DocsFolderKind kind)
    {
        if (kind == DocsFolderKind.Plans)
        {
            return $"'{folder.Name}' is in the Plans area. Plans accepts {DocumentItemExtension}. " +
                   $".nwc files have to be copied into a folder under Project Files, which uses {FileItemExtension}.";
        }

        return $"Could not tell whether '{folder.Name}' is under Project Files or Plans. " +
               $"Choose a folder under Project Files. Server-side .nwc copy uses {FileItemExtension}.";
    }

    private static bool HasTitle(IReadOnlyList<string> titles, string expected) =>
        titles.Any(title => title.Equals(expected, StringComparison.OrdinalIgnoreCase));

    private static bool EndsWithType(IReadOnlyList<string> types, string suffix) =>
        types.Any(type => type.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
