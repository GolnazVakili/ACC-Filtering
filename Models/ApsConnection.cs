namespace AccFiltering.Models;

/// <summary>Values the user typed to connect to ACC. The token stays in memory only.</summary>
public sealed class ApsConnection
{
    /// <summary>API host without a trailing slash.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>APS app client ID, when the user supplied one. Not a substitute for the token.</summary>
    public string? ClientId { get; init; }

    /// <summary>Three-legged access token. Never write this to a log, report, or settings file.</summary>
    public required string AccessToken { get; init; }

    /// <summary>Hub ID including the <c>b.</c> prefix.</summary>
    public required string HubId { get; init; }

    /// <summary>Project ID including the <c>b.</c> prefix.</summary>
    public required string ProjectId { get; init; }
}
