namespace AccFiltering.Services;

/// <summary>The access token was rejected.</summary>
public sealed class ApsAuthException : Exception
{
    /// <summary>Creates the exception with the message shown to the user.</summary>
    public ApsAuthException()
        : base("Token expired or invalid, provide a fresh token")
    {
    }
}

/// <summary>ACC refused the call because the app or the user lacks permission.</summary>
public sealed class ApsForbiddenException : Exception
{
    /// <summary>Creates the exception with the ACC custom-integration guidance.</summary>
    public ApsForbiddenException()
        : base("Access was denied (403). The APS app must be added as a Custom Integration in ACC Account Admin, and the user needs View/Download on the source folder and Upload/Edit on the destination folder.")
    {
    }
}

/// <summary>A Data Management call failed and this file should be reported as failed.</summary>
public sealed class ApsRequestException : Exception
{
    /// <summary>Creates the exception from a status code and a sanitized API detail.</summary>
    public ApsRequestException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>HTTP status code, or 0 when the call did not return one.</summary>
    public int StatusCode { get; }
}
