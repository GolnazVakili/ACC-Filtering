using Microsoft.Extensions.Configuration;

namespace AccFiltering.Config;

/// <summary>
/// Non-secret defaults. The access token is never a property of this type and is never read from configuration.
/// </summary>
public sealed class AppSettings
{
    /// <summary>APS API host. The Data Management API uses https://developer.api.autodesk.com.</summary>
    public string BaseUrl { get; init; } = "https://developer.api.autodesk.com";

    /// <summary>Optional APS app client ID. Logged for reference and never sent as a secret.</summary>
    public string ClientId { get; init; } = "";

    /// <summary>Optional hub ID. The user can accept this at the prompt or type another one.</summary>
    public string HubId { get; init; } = "";

    /// <summary>Optional project ID. The user can accept this at the prompt or type another one.</summary>
    public string ProjectId { get; init; } = "";

    /// <summary>ACC folder path, with names separated by &gt;.</summary>
    public string SourceFolderPath { get; init; } = "";

    /// <summary>Maximum number of copy or tip requests in flight.</summary>
    public int MaxParallelism { get; init; } = 3;

    /// <summary>Folder contents page size. The API maximum is 200.</summary>
    public int PageLimit { get; init; } = 200;

    /// <summary>Extra attempts after the first request when the API returns 429 or 5xx.</summary>
    public int MaxRetries { get; init; } = 5;

    /// <summary>Default source folder when configuration does not set one.</summary>
    public const string DefaultSourceFolderPath = "02.Shared External > USEP > All > NWC > 03.03.01 NWC";

    /// <summary>
    /// Loads appsettings.json and the optional appsettings.Local.json.
    /// Environment variables are not added, so a token or hub from another project cannot leak in.
    /// </summary>
    public static AppSettings Load()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .Build();

        var baseUrl = config["Aps:BaseUrl"];
        var source = config["Aps:SourceFolderPath"];

        return new AppSettings
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl)
                ? "https://developer.api.autodesk.com"
                : baseUrl.Trim().TrimEnd('/'),
            ClientId = config["Aps:ClientId"]?.Trim() ?? "",
            HubId = config["Aps:HubId"]?.Trim() ?? "",
            ProjectId = config["Aps:ProjectId"]?.Trim() ?? "",
            SourceFolderPath = string.IsNullOrWhiteSpace(source) ? DefaultSourceFolderPath : source.Trim(),
            MaxParallelism = ReadInt(config["Copy:MaxParallelism"], fallback: 3, min: 1, max: 8),
            PageLimit = ReadInt(config["Copy:PageLimit"], fallback: 200, min: 1, max: 200),
            MaxRetries = ReadInt(config["Copy:MaxRetries"], fallback: 5, min: 0, max: 10)
        };
    }

    private static int ReadInt(string? raw, int fallback, int min, int max)
    {
        if (!int.TryParse(raw, out var value))
        {
            return fallback;
        }

        return Math.Clamp(value, min, max);
    }
}
