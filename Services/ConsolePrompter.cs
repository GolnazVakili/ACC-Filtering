using AccFiltering.Config;
using AccFiltering.Models;
using Spectre.Console;

namespace AccFiltering.Services;

/// <summary>Interactive prompts. The access token is masked and is never taken from the environment.</summary>
public sealed class ConsolePrompter
{
    /// <summary>Asks for client ID, token, hub ID, and project ID, then lets the user confirm or retype them.</summary>
    public ApsConnection PromptConnection(AppSettings settings)
    {
        AnsiConsole.Write(new Rule("[bold]ACC server-side copy[/]"));
        AnsiConsole.MarkupLine("Enter the APS credentials for this ACC project.");
        AnsiConsole.MarkupLine("The access token is [bold]not[/] read from the environment or from appsettings, so a token saved for another project is not used.");
        AnsiConsole.MarkupLine("Hub ID and project ID get a [bold]b.[/] prefix when you leave it off. Press Enter to accept a saved non-secret value when one is shown.");
        AnsiConsole.WriteLine();

        while (true)
        {
            var clientId = PromptOptional("APS client ID (optional, used only in the log)", settings.ClientId);
            var token = PromptToken();
            var hub = ApsIdentifiers.EnsureDataManagementPrefix(PromptRequired("APS hub ID", settings.HubId));
            var project = ApsIdentifiers.EnsureDataManagementPrefix(PromptRequired("APS project ID", settings.ProjectId));

            var table = new Table().Border(TableBorder.Rounded).AddColumn("Setting").AddColumn("Value");
            table.AddRow("Client ID", string.IsNullOrWhiteSpace(clientId) ? "(not set)" : clientId);
            table.AddRow("Hub ID", hub);
            table.AddRow("Project ID", project);
            table.AddRow("API", settings.BaseUrl);
            table.AddRow("Access token", "provided, hidden");
            AnsiConsole.Write(table);

            if (AnsiConsole.Confirm("Use these connection settings?", defaultValue: true))
            {
                return new ApsConnection
                {
                    BaseUrl = settings.BaseUrl,
                    ClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId,
                    AccessToken = token,
                    HubId = hub,
                    ProjectId = project
                };
            }

            AnsiConsole.MarkupLine("Enter the connection settings again.");
        }
    }

    /// <summary>Asks for an Excel path until the file exists. Surrounding quotes are removed.</summary>
    public string PromptExcelPath()
    {
        while (true)
        {
            var entered = AnsiConsole.Prompt(new TextPrompt<string>("Path to the Excel file:"));
            var path = NormalizePastedPath(entered);
            if (File.Exists(path))
            {
                return Path.GetFullPath(path);
            }

            AnsiConsole.MarkupLine($"[red]File not found:[/] {Markup.Escape(path)}");
        }
    }

    /// <summary>Asks for an ACC folder path. <paramref name="defaultValue"/> is accepted with Enter when set.</summary>
    public string PromptFolderPath(string label, string? defaultValue)
    {
        var prompt = new TextPrompt<string>(label.EndsWith(':') ? label : label + ":");
        if (!string.IsNullOrWhiteSpace(defaultValue))
        {
            prompt.DefaultValue(defaultValue);
        }

        prompt.Validate(value =>
            FolderResolver.SplitPath(value).Count == 0
                ? ValidationResult.Error("Enter a folder path. Separate folder names with >.")
                : ValidationResult.Success());

        var entered = AnsiConsole.Prompt(prompt);
        return string.Join(" > ", FolderResolver.SplitPath(entered));
    }

    /// <summary>Prints the folders the user can choose at the level that failed.</summary>
    public void PrintMissingSegment(string segment, string? parentName, IReadOnlyList<string> available)
    {
        var where = parentName is null ? "the top of the project" : $"'{parentName}'";
        AnsiConsole.MarkupLine($"[yellow]Folder '{Markup.Escape(segment)}' was not found under {Markup.Escape(where)}.[/]");
        PrintNames("Folders at this level:", available);
    }

    /// <summary>Prints the places a segment was found when the path is ambiguous.</summary>
    public void PrintAmbiguous(string segment, IReadOnlyList<string> options)
    {
        AnsiConsole.MarkupLine($"[yellow]'{Markup.Escape(segment)}' was found in more than one place. Include the top folder in the path.[/]");
        PrintNames("Options:", options);
    }

    /// <summary>Strips quotes and whitespace added by drag-and-drop.</summary>
    public static string NormalizePastedPath(string input)
    {
        var value = input.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }
        else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
        {
            value = value[1..^1];
        }

        return value.Trim();
    }

    /// <summary>Trims a pasted token and removes a leading Bearer prefix.</summary>
    public static string NormalizeToken(string input)
    {
        var token = input.Trim().Trim('"');
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            token = token[7..].Trim();
        }

        return token.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
    }

    private static void PrintNames(string heading, IReadOnlyList<string> names)
    {
        AnsiConsole.MarkupLine(Markup.Escape(heading));
        if (names.Count == 0)
        {
            AnsiConsole.MarkupLine("  [grey](none)[/]");
            return;
        }

        foreach (var name in names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(80))
        {
            AnsiConsole.MarkupLine($"  - {Markup.Escape(name)}");
        }

        if (names.Count > 80)
        {
            AnsiConsole.MarkupLine($"  [grey]... {names.Count - 80} more[/]");
        }
    }

    private static string PromptRequired(string label, string? defaultValue)
    {
        var prompt = new TextPrompt<string>($"{label}:");
        if (!string.IsNullOrWhiteSpace(defaultValue))
        {
            prompt.DefaultValue(defaultValue);
        }

        prompt.Validate(value =>
            string.IsNullOrWhiteSpace(value)
                ? ValidationResult.Error("A value is required.")
                : ValidationResult.Success());
        return AnsiConsole.Prompt(prompt).Trim();
    }

    private static string PromptOptional(string label, string? defaultValue)
    {
        var prompt = new TextPrompt<string>($"{label}:").AllowEmpty();
        if (!string.IsNullOrWhiteSpace(defaultValue))
        {
            prompt.DefaultValue(defaultValue);
        }

        return AnsiConsole.Prompt(prompt).Trim();
    }

    private static string PromptToken()
    {
        var prompt = new TextPrompt<string>("APS access token:")
            .Secret()
            .PromptStyle("green")
            .Validate(value =>
                NormalizeToken(value).Length == 0
                    ? ValidationResult.Error("Paste an access token. This app does not read one from the environment.")
                    : ValidationResult.Success());
        return NormalizeToken(AnsiConsole.Prompt(prompt));
    }
}
