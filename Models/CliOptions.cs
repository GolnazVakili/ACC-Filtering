namespace AccFiltering.Models;

/// <summary>Command-line switches. Dry-run is the default.</summary>
public sealed class CliOptions
{
    /// <summary>When true, matched files are copied. When false, the app only writes the plan.</summary>
    public bool Execute { get; init; }

    /// <summary>When true, an existing destination file gets a new version via server-side copy.</summary>
    public bool Overwrite { get; init; }

    /// <summary>Maximum number of copy operations. Null means no limit.</summary>
    public int? Limit { get; init; }

    /// <summary>Log each API URL and status code. Headers and tokens are never logged.</summary>
    public bool Verbose { get; init; }

    /// <summary>Both mode flags were passed. Execute is used.</summary>
    public bool ExecuteOverridesDryRun { get; init; }

    /// <summary>Usage text printed for <c>--help</c> and invalid arguments.</summary>
    public const string HelpText =
        """
        ACC server-side copy

        Copies .nwc files inside one ACC project with the Data Management API.
        File bytes are not downloaded or uploaded. Dry-run is the default.

        Usage:
          dotnet run -- [options]

        Options:
          --dry-run       Resolve folders, match names, and write the report. Copy nothing. Default.
          --execute       Perform the server-side copy after confirmation.
          --overwrite     Create a new version when the destination name already exists.
          --limit N       Copy at most N files. Use 2 or 3 for the first test.
          --verbose       Log API method, URL, and status. Never logs headers or tokens.
          --help          Show this text.

        The app asks for the access token, client ID, hub ID, project ID, Excel path,
        and folder paths at startup. It does not read APS_ACCESS_TOKEN from the environment.
        """;

    /// <summary>Parses process arguments.</summary>
    public static bool TryParse(string[] args, out CliOptions options, out string? error, out bool help)
    {
        options = new CliOptions();
        error = null;
        help = false;

        var execute = false;
        var dryRun = false;
        var overwrite = false;
        var verbose = false;
        int? limit = null;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg is "--help" or "-h" or "/?")
            {
                help = true;
                return false;
            }

            if (arg == "--execute")
            {
                execute = true;
                continue;
            }

            if (arg == "--dry-run")
            {
                dryRun = true;
                continue;
            }

            if (arg == "--overwrite")
            {
                overwrite = true;
                continue;
            }

            if (arg == "--verbose")
            {
                verbose = true;
                continue;
            }

            if (arg == "--limit" || arg.StartsWith("--limit=", StringComparison.Ordinal))
            {
                var raw = arg.StartsWith("--limit=", StringComparison.Ordinal)
                    ? arg["--limit=".Length..]
                    : index + 1 < args.Length ? args[++index] : null;
                if (!int.TryParse(raw, out var parsed) || parsed < 1)
                {
                    error = "--limit must be a positive integer.";
                    return false;
                }

                limit = parsed;
                continue;
            }

            error = $"Unknown argument '{arg}'.";
            return false;
        }

        options = new CliOptions
        {
            Execute = execute,
            Overwrite = overwrite,
            Limit = limit,
            Verbose = verbose,
            ExecuteOverridesDryRun = execute && dryRun
        };
        return true;
    }
}
