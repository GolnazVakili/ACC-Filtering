using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AccFiltering.Config;
using AccFiltering.Models;
using Microsoft.Extensions.Logging;

namespace AccFiltering.Services;

/// <summary>
/// Data Management HTTP client: one shared <see cref="HttpClient"/>, pagination, and retries.
/// Request headers are never logged.
/// </summary>
public sealed class ApsClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ApsConnection _connection;
    private readonly AppSettings _settings;
    private readonly ILogger<ApsClient> _logger;

    /// <summary>Creates the client and attaches the bearer token to the shared handler.</summary>
    public ApsClient(ApsConnection connection, AppSettings settings, ILogger<ApsClient> logger)
    {
        _connection = connection;
        _settings = settings;
        _logger = logger;
        _http = new HttpClient
        {
            BaseAddress = new Uri(connection.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(3)
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.AccessToken);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.api+json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AccFiltering/1.0");
    }

    /// <summary>Lists the project's top folders.</summary>
    public async Task<IReadOnlyList<AccFolder>> GetTopFoldersAsync(CancellationToken cancellationToken) =>
        await ListPagesAsync(ApsUrls.TopFolders(_connection.HubId, _connection.ProjectId), ApsJson.ParseFolder, cancellationToken).ConfigureAwait(false);

    /// <summary>Lists child folders of <paramref name="folderId"/>.</summary>
    public async Task<IReadOnlyList<AccFolder>> ListChildFoldersAsync(string folderId, CancellationToken cancellationToken) =>
        await ListPagesAsync(ContentsUrl(folderId), ApsJson.ParseFolder, cancellationToken).ConfigureAwait(false);

    /// <summary>Lists file items in <paramref name="folderId"/>. Tip ids are included when the listing returns them.</summary>
    public async Task<IReadOnlyList<AccItem>> ListItemsAsync(string folderId, CancellationToken cancellationToken) =>
        await ListPagesAsync(ContentsUrl(folderId), ApsJson.ParseItem, cancellationToken).ConfigureAwait(false);

    /// <summary>Loads one folder, including the extension data used to detect Project Files versus Plans.</summary>
    public async Task<AccFolder> GetFolderAsync(string folderId, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(ApsUrls.Folder(_connection.ProjectId, folderId), cancellationToken);
        var root = document.RootElement;
        var node = root.TryGetProperty("data", out var data) ? data : root;
        return ApsJson.ParseFolder(node)
            ?? throw new ApsRequestException(404, "The folder was returned without a usable id or name.");
    }

    /// <summary>Fills missing tip version ids, with the configured parallelism.</summary>
    public async Task EnsureTipVersionsAsync(IReadOnlyList<AccItem> items, CancellationToken cancellationToken)
    {
        var pending = items.Where(item => string.IsNullOrWhiteSpace(item.TipVersionId)).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(_settings.MaxParallelism);
        var tasks = pending.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                item.TipVersionId = await GetTipVersionIdAsync(item.Id, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Returns the tip version URN, or null when the item has none.</summary>
    public async Task<string?> GetTipVersionIdAsync(string itemId, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(ApsUrls.ItemTip(_connection.ProjectId, itemId), cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var data))
        {
            return null;
        }

        return data.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    /// <summary>Creates a new item by copying a version on the server.</summary>
    public Task<CreateCopyResult> CopyItemAsync(
        string displayName,
        string parentFolderId,
        string sourceVersionId,
        string itemExtension,
        string versionExtension,
        CancellationToken cancellationToken)
    {
        var json = CopyPayloadBuilder.BuildCreateItemJson(displayName, parentFolderId, itemExtension, versionExtension);
        var url = ApsUrls.CreateItem(_connection.ProjectId, sourceVersionId);
        return PostCopyAsync(url, json, cancellationToken);
    }

    /// <summary>Adds a version to an existing item by copying a version on the server.</summary>
    public Task<CreateCopyResult> CopyVersionAsync(
        string displayName,
        string existingItemId,
        string sourceVersionId,
        string versionExtension,
        CancellationToken cancellationToken)
    {
        var json = CopyPayloadBuilder.BuildCreateVersionJson(displayName, existingItemId, versionExtension);
        var url = ApsUrls.CreateVersion(_connection.ProjectId, sourceVersionId);
        return PostCopyAsync(url, json, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private string ContentsUrl(string folderId) =>
        ApsUrls.FolderContents(_connection.ProjectId, folderId, _settings.PageLimit);

    private async Task<CreateCopyResult> PostCopyAsync(string url, string json, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                var content = new StringContent(json, Encoding.UTF8);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.api+json");
                request.Content = content;
                return request;
            },
            url,
            cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return new CreateCopyResult(Conflict: true, ResourceId: null);
        }

        if (!response.IsSuccessStatusCode)
        {
            ThrowForStatus(response.StatusCode, body);
        }

        string? resourceId = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("data", out var data)
                && data.TryGetProperty("id", out var id))
            {
                resourceId = id.GetString();
            }
        }

        return new CreateCopyResult(Conflict: false, resourceId);
    }

    private async Task<List<T>> ListPagesAsync<T>(
        string firstUrl,
        Func<JsonElement, T?> parse,
        CancellationToken cancellationToken)
    {
        var results = new List<T>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? url = firstUrl;

        while (!string.IsNullOrEmpty(url))
        {
            if (!seen.Add(url))
            {
                break;
            }

            if (seen.Count > 100)
            {
                throw new ApsRequestException(0, "Folder listing exceeded 100 pages.");
            }

            using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in data.EnumerateArray())
                {
                    var parsed = parse(element);
                    if (parsed is not null)
                    {
                        results.Add(parsed);
                    }
                }
            }

            url = NextLink(document.RootElement);
        }

        return results;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            url,
            cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            ThrowForStatus(response.StatusCode, body);
        }

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        string urlForLog,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = requestFactory();
                var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("{Method} {Url} -> {Status}", request.Method, DisplayUrl(urlForLog), status);
                }

                if (status is 429 || status >= 500)
                {
                    if (attempt < _settings.MaxRetries)
                    {
                        var delay = RetryDelay(response, attempt);
                        _logger.LogWarning(
                            "HTTP {Status} from {Url}. Retry {Attempt}/{Max} in {DelaySeconds:0}s.",
                            status,
                            DisplayUrl(urlForLog),
                            attempt + 1,
                            _settings.MaxRetries,
                            delay.TotalSeconds);
                        response.Dispose();
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                return response;
            }
            catch (HttpRequestException) when (attempt < _settings.MaxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning(
                    "Network error calling {Url}. Retry {Attempt}/{Max} in {DelaySeconds:0}s.",
                    DisplayUrl(urlForLog),
                    attempt + 1,
                    _settings.MaxRetries,
                    delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < _settings.MaxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning(
                    "Request timed out calling {Url}. Retry {Attempt}/{Max} in {DelaySeconds:0}s.",
                    DisplayUrl(urlForLog),
                    attempt + 1,
                    _settings.MaxRetries,
                    delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void ThrowForStatus(HttpStatusCode status, string body)
    {
        if (status == HttpStatusCode.Unauthorized)
        {
            throw new ApsAuthException();
        }

        if (status == HttpStatusCode.Forbidden)
        {
            throw new ApsForbiddenException();
        }

        var detail = Sanitize(ApsJson.ErrorDetail(body));
        var message = string.IsNullOrWhiteSpace(detail)
            ? $"APS returned HTTP {(int)status}."
            : $"APS returned HTTP {(int)status}. {detail}";
        throw new ApsRequestException((int)status, message);
    }

    private string? Sanitize(string? detail)
    {
        if (string.IsNullOrEmpty(detail) || string.IsNullOrEmpty(_connection.AccessToken))
        {
            return detail;
        }

        return detail.Replace(_connection.AccessToken, "[redacted]", StringComparison.Ordinal);
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta && delta > backoff)
        {
            backoff = delta;
        }
        else if (retryAfter?.Date is DateTimeOffset date)
        {
            var until = date - DateTimeOffset.UtcNow;
            if (until > backoff)
            {
                backoff = until;
            }
        }

        if (backoff > TimeSpan.FromMinutes(5))
        {
            backoff = TimeSpan.FromMinutes(5);
        }

        return backoff < TimeSpan.Zero ? TimeSpan.Zero : backoff;
    }

    private static string? NextLink(JsonElement root)
    {
        if (!root.TryGetProperty("links", out var links)
            || !links.TryGetProperty("next", out var next)
            || !next.TryGetProperty("href", out var href))
        {
            return null;
        }

        var value = href.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private string DisplayUrl(string url)
    {
        var absolute = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : new Uri(_http.BaseAddress!, url).ToString();
        var tokenIndex = absolute.IndexOf("access_token", StringComparison.OrdinalIgnoreCase);
        return tokenIndex >= 0 ? absolute[..tokenIndex] + "access_token=redacted" : absolute;
    }
}
