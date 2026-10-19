using AccFiltering.Config;
using AccFiltering.Models;
using AccFiltering.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5080");
builder.Services.AddSingleton<JobStore>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/jobs", async (HttpRequest request, JobStore jobs, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "Send the form as multipart data." });
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var token = ConsolePrompter.NormalizeToken(form["token"].ToString());
    var sourcePath = form["sourcePath"].ToString().Trim();
    var destinationPath = form["destinationPath"].ToString().Trim();
    var excel = form.Files.GetFile("excel");

    if (token.Length == 0)
    {
        return Results.BadRequest(new { message = "Paste an APS access token. This app does not read one from the environment." });
    }

    if (string.IsNullOrWhiteSpace(form["hubId"]) || string.IsNullOrWhiteSpace(form["projectId"]))
    {
        return Results.BadRequest(new { message = "Enter the hub ID and the project ID." });
    }

    if (FolderResolver.SplitPath(sourcePath).Count == 0 || FolderResolver.SplitPath(destinationPath).Count == 0)
    {
        return Results.BadRequest(new { message = "Enter the source and destination folder paths. Separate folder names with >." });
    }

    if (excel is null || excel.Length == 0)
    {
        return Results.BadRequest(new { message = "Choose the Excel file that contains the Model Name column." });
    }

    var limitText = form["limit"].ToString().Trim();
    int? limit = null;
    if (limitText.Length > 0)
    {
        if (!int.TryParse(limitText, out var parsed) || parsed < 1)
        {
            return Results.BadRequest(new { message = "Limit must be a positive whole number, or left blank." });
        }

        limit = parsed;
    }

    var tempPath = Path.Combine(Path.GetTempPath(), $"acc-filtering-{Guid.NewGuid():N}{Path.GetExtension(excel.FileName)}");
    await using (var stream = File.Create(tempPath))
    {
        await excel.CopyToAsync(stream, cancellationToken);
    }

    var workflowRequest = new CopyWorkflowRequest
    {
        Connection = new ApsConnection
        {
            BaseUrl = "https://developer.api.autodesk.com",
            ClientId = NullIfBlank(form["clientId"].ToString()),
            AccessToken = token,
            HubId = ApsIdentifiers.EnsureDataManagementPrefix(form["hubId"].ToString()),
            ProjectId = ApsIdentifiers.EnsureDataManagementPrefix(form["projectId"].ToString())
        },
        ExcelPath = tempPath,
        SourcePath = sourcePath,
        DestinationPath = destinationPath,
        Execute = form["execute"].ToString() == "true",
        Overwrite = form["overwrite"].ToString() == "true",
        Limit = limit,
        Settings = new AppSettings
        {
            MaxParallelism = 3,
            PageLimit = 200,
            MaxRetries = 5,
            SourceFolderPath = sourcePath
        }
    };

    var job = jobs.Start(workflowRequest, loggerFactory, tempPath);
    return Results.Accepted($"/api/jobs/{job.Id}", new { id = job.Id });
});

app.MapGet("/api/jobs/{id}", (string id, JobStore jobs) =>
{
    var job = jobs.Find(id);
    return job is null ? Results.NotFound() : Results.Ok(job.ToStatus());
});

app.MapGet("/api/jobs/{id}/report", (string id, JobStore jobs) =>
{
    var job = jobs.Find(id);
    if (job?.ReportCsv is null)
    {
        return Results.NotFound();
    }

    var bytes = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(job.ReportCsv);
    return Results.File(bytes, "text/csv", $"copy_report_{job.Started:yyyyMMdd_HHmm}.csv");
});

app.Run();

static string? NullIfBlank(string value)
{
    var trimmed = value.Trim();
    return trimmed.Length == 0 ? null : trimmed;
}

/// <summary>In-memory jobs for the local web app. Tokens are not included in status responses.</summary>
public sealed class JobStore
{
    private readonly Dictionary<string, CopyJob> _jobs = new();
    private readonly object _gate = new();

    /// <summary>Starts a copy run on a background task.</summary>
    public CopyJob Start(CopyWorkflowRequest request, ILoggerFactory loggerFactory, string tempExcelPath)
    {
        var job = new CopyJob(Guid.NewGuid().ToString("N"));
        lock (_gate)
        {
            _jobs[job.Id] = job;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await new CopyWorkflow(loggerFactory).RunAsync(request, CancellationToken.None);
                job.Complete(result);
            }
            catch (Exception exception)
            {
                job.Fail(exception.Message);
            }
            finally
            {
                TryDelete(tempExcelPath);
            }
        });

        return job;
    }

    /// <summary>Returns the job, or null when the id is unknown.</summary>
    public CopyJob? Find(string id)
    {
        lock (_gate)
        {
            return _jobs.TryGetValue(id, out var job) ? job : null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>One web run and the fields the page is allowed to see.</summary>
public sealed class CopyJob
{
    private readonly object _gate = new();
    private CopyWorkflowResult? _result;
    private string _message = "Working…";

    /// <summary>Creates a running job.</summary>
    public CopyJob(string id)
    {
        Id = id;
        Started = DateTime.Now;
    }

    /// <summary>Job id.</summary>
    public string Id { get; }

    /// <summary>When the job was accepted.</summary>
    public DateTime Started { get; }

    /// <summary>Report CSV after the run finishes.</summary>
    public string? ReportCsv
    {
        get
        {
            lock (_gate)
            {
                return _result?.ReportCsv;
            }
        }
    }

    /// <summary>Stores a finished workflow result.</summary>
    public void Complete(CopyWorkflowResult result)
    {
        lock (_gate)
        {
            _result = result;
            _message = result.Message;
        }
    }

    /// <summary>Stores an unexpected failure.</summary>
    public void Fail(string message)
    {
        lock (_gate)
        {
            _message = message;
            _result = new CopyWorkflowResult { Ok = false, Message = message };
        }
    }

    /// <summary>Status payload. The access token is never included.</summary>
    public object ToStatus()
    {
        lock (_gate)
        {
            var rows = _result?.Rows ?? [];
            return new
            {
                id = Id,
                state = _result is null ? "running" : _result.Ok ? "succeeded" : "failed",
                message = _message,
                sourcePath = _result?.SourcePath ?? "",
                destinationPath = _result?.DestinationPath ?? "",
                excelCount = _result?.ExcelCount ?? 0,
                duplicates = _result?.Duplicates ?? [],
                availableFolders = _result?.AvailableFolders ?? [],
                summary = new
                {
                    matched = rows.Count(row => row.Status != CopyStatus.NotFound),
                    copied = rows.Count(row => row.Status == CopyStatus.Copied),
                    skipped = rows.Count(row => row.Status == CopyStatus.SkippedExists),
                    notFound = rows.Count(row => row.Status == CopyStatus.NotFound),
                    notAttempted = rows.Count(row => row.Status == CopyStatus.NotAttempted),
                    dryRun = rows.Count(row => row.Status == CopyStatus.DryRun),
                    failed = rows.Count(row => row.Status == CopyStatus.Failed)
                },
                rows = rows.Select(row => new
                {
                    excelName = row.ExcelName,
                    matchedName = row.MatchedName,
                    status = row.StatusText,
                    error = row.Error
                })
            };
        }
    }
}
