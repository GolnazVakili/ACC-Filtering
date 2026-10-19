using System.Text.Json;
using System.Text.Json.Nodes;

namespace AccFiltering.Services;

/// <summary>
/// Builds the JSON:API bodies for server-side copy.
/// The source version is passed in the <c>copyFrom</c> query string, not in this body.
/// </summary>
public static class CopyPayloadBuilder
{
    /// <summary>
    /// Body for <c>POST /data/v1/projects/{projectId}/items?copyFrom={versionUrn}</c>.
    /// <paramref name="itemExtension"/> is <c>items:autodesk.bim360:File</c> under Project Files.
    /// </summary>
    public static string BuildCreateItemJson(
        string displayName,
        string parentFolderId,
        string itemExtension,
        string versionExtension)
    {
        var payload = new JsonObject
        {
            ["jsonapi"] = new JsonObject { ["version"] = "1.0" },
            ["data"] = new JsonObject
            {
                ["type"] = "items",
                ["attributes"] = new JsonObject
                {
                    ["displayName"] = displayName,
                    ["extension"] = Extension(itemExtension)
                },
                ["relationships"] = new JsonObject
                {
                    ["tip"] = DataLink("versions", "1"),
                    ["parent"] = DataLink("folders", parentFolderId)
                }
            },
            ["included"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "versions",
                    ["id"] = "1",
                    ["attributes"] = new JsonObject
                    {
                        ["name"] = displayName,
                        ["extension"] = Extension(versionExtension)
                    }
                }
            }
        };

        return payload.ToJsonString();
    }

    /// <summary>
    /// Body for <c>POST /data/v1/projects/{projectId}/versions?copyFrom={versionUrn}</c>.
    /// Storage is omitted because the bytes come from <c>copyFrom</c>.
    /// </summary>
    public static string BuildCreateVersionJson(string displayName, string existingItemId, string versionExtension)
    {
        var payload = new JsonObject
        {
            ["jsonapi"] = new JsonObject { ["version"] = "1.0" },
            ["data"] = new JsonObject
            {
                ["type"] = "versions",
                ["attributes"] = new JsonObject
                {
                    ["name"] = displayName,
                    ["extension"] = Extension(versionExtension)
                },
                ["relationships"] = new JsonObject
                {
                    ["item"] = DataLink("items", existingItemId)
                }
            }
        };

        return payload.ToJsonString();
    }

    private static JsonObject Extension(string type) =>
        new()
        {
            ["type"] = type,
            ["version"] = "1.0"
        };

    private static JsonObject DataLink(string type, string id) =>
        new()
        {
            ["data"] = new JsonObject
            {
                ["type"] = type,
                ["id"] = id
            }
        };
}

/// <summary>Relative Data Management URLs. Version URNs are encoded once so <c>?version=</c> stays inside <c>copyFrom</c>.</summary>
public static class ApsUrls
{
    /// <summary>Create-item URL for a server-side copy.</summary>
    public static string CreateItem(string projectId, string sourceVersionId) =>
        $"data/v1/projects/{Uri.EscapeDataString(projectId)}/items?copyFrom={Uri.EscapeDataString(sourceVersionId)}";

    /// <summary>Create-version URL for a server-side copy onto an existing item.</summary>
    public static string CreateVersion(string projectId, string sourceVersionId) =>
        $"data/v1/projects/{Uri.EscapeDataString(projectId)}/versions?copyFrom={Uri.EscapeDataString(sourceVersionId)}";

    /// <summary>Top-level folders the signed-in user can see.</summary>
    public static string TopFolders(string hubId, string projectId) =>
        $"project/v1/hubs/{Uri.EscapeDataString(hubId)}/projects/{Uri.EscapeDataString(projectId)}/topFolders?excludeDeleted=true";

    /// <summary>First page of a folder's children.</summary>
    public static string FolderContents(string projectId, string folderId, int pageLimit) =>
        $"data/v1/projects/{Uri.EscapeDataString(projectId)}/folders/{Uri.EscapeDataString(folderId)}/contents?page[number]=0&page[limit]={pageLimit}";

    /// <summary>One folder, including extension data used to tell Project Files from Plans.</summary>
    public static string Folder(string projectId, string folderId) =>
        $"data/v1/projects/{Uri.EscapeDataString(projectId)}/folders/{Uri.EscapeDataString(folderId)}";

    /// <summary>Tip version of an item.</summary>
    public static string ItemTip(string projectId, string itemId) =>
        $"data/v1/projects/{Uri.EscapeDataString(projectId)}/items/{Uri.EscapeDataString(itemId)}/tip";
}

/// <summary>Small JSON helpers for Data Management payloads.</summary>
internal static class ApsJson
{
    public static AccFiltering.Models.AccFolder? ParseFolder(JsonElement element)
    {
        if (!TypeIs(element, "folders"))
        {
            return null;
        }

        var id = ReadString(element, "id");
        if (string.IsNullOrWhiteSpace(id) || !element.TryGetProperty("attributes", out var attributes))
        {
            return null;
        }

        if (ReadBool(attributes, "hidden"))
        {
            return null;
        }

        var name = ReadString(attributes, "displayName");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = ReadString(attributes, "name");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string? folderType = null;
        var allowed = new List<string>();
        var visible = new List<string>();
        var parents = new List<string>();

        if (attributes.TryGetProperty("extension", out var extension)
            && extension.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object)
        {
            folderType = ReadString(data, "folderType");
            AddStrings(data, "allowedTypes", allowed);
            AddStrings(data, "visibleTypes", visible);
            if (data.TryGetProperty("folderParents", out var folderParents) && folderParents.ValueKind == JsonValueKind.Array)
            {
                foreach (var parent in folderParents.EnumerateArray())
                {
                    var title = ReadString(parent, "title");
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        parents.Add(title);
                    }
                }
            }
        }

        return new AccFiltering.Models.AccFolder(id, name, folderType, allowed, visible, parents);
    }

    public static AccFiltering.Models.AccItem? ParseItem(JsonElement element)
    {
        if (!TypeIs(element, "items"))
        {
            return null;
        }

        var id = ReadString(element, "id");
        if (string.IsNullOrWhiteSpace(id) || !element.TryGetProperty("attributes", out var attributes))
        {
            return null;
        }

        if (ReadBool(attributes, "hidden"))
        {
            return null;
        }

        var name = ReadString(attributes, "displayName");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = ReadString(attributes, "name");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string? tip = null;
        if (element.TryGetProperty("relationships", out var relationships)
            && relationships.TryGetProperty("tip", out var tipNode)
            && tipNode.TryGetProperty("data", out var tipData))
        {
            tip = ReadString(tipData, "id");
        }

        return new AccFiltering.Models.AccItem(id, name, tip);
    }

    public static string? ErrorDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var parts = new List<string>();
            CollectMessages(document.RootElement, parts, 0);
            var text = string.Join(" ", parts.Distinct(StringComparer.Ordinal));
            if (text.Length == 0)
            {
                text = body;
            }

            return text.Length <= 500 ? text : text[..500];
        }
        catch (JsonException)
        {
            return body.Length <= 500 ? body : body[..500];
        }
    }

    private static void CollectMessages(JsonElement element, List<string> parts, int depth)
    {
        if (depth > 5)
        {
            return;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "detail", "Detail", "developerMessage", "error_description", "title", "ErrorCode" })
            {
                if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        parts.Add(text.Trim());
                    }
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    CollectMessages(property.Value, parts, depth + 1);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectMessages(item, parts, depth + 1);
            }
        }
    }

    private static bool TypeIs(JsonElement element, string type) =>
        element.TryGetProperty("type", out var typeElement)
        && string.Equals(typeElement.GetString(), type, StringComparison.Ordinal);

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static void AddStrings(JsonElement element, string name, List<string> target)
    {
        if (!element.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var text = entry.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    target.Add(text);
                }
            }
        }
    }
}
