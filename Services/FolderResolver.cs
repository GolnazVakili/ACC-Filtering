using AccFiltering.Models;
using Microsoft.Extensions.Logging;

namespace AccFiltering.Services;

/// <summary>Resolves an ACC path such as <c>04.Coordination &gt; Projectwide &gt; NWC</c> from top folders.</summary>
public sealed class FolderResolver
{
    private readonly ApsClient _client;
    private readonly ILogger<FolderResolver> _logger;

    /// <summary>Creates the resolver.</summary>
    public FolderResolver(ApsClient client, ILogger<FolderResolver> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>Splits a path on <c>&gt;</c> and trims each segment.</summary>
    public static IReadOnlyList<string> SplitPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        return path.Split('>')
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0)
            .ToArray();
    }

    /// <summary>Finds one child by name. Comparison is trimmed and case-insensitive.</summary>
    public static AccFolder? FindByName(IReadOnlyList<AccFolder> folders, string segment)
    {
        var wanted = segment.Trim();
        var matches = folders
            .Where(folder => folder.Name.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
        {
            return null;
        }

        var exact = matches.Where(folder => folder.Name.Trim() == wanted).ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }

        return matches[0];
    }

    /// <summary>
    /// Walks <paramref name="path"/> from top folders.
    /// When the first segment is not a top folder, a unique match one level down is used
    /// so a path can start at <c>02.Shared External</c> inside Project Files.
    /// </summary>
    public async Task<FolderResolveResult> ResolveAsync(string path, CancellationToken cancellationToken)
    {
        var segments = SplitPath(path);
        if (segments.Count == 0)
        {
            return new FolderResolveResult.NotFound("(empty path)", null, []);
        }

        var tops = await _client.GetTopFoldersAsync(cancellationToken).ConfigureAwait(false);
        var chain = new List<AccFolder>();
        AccFolder current;
        var index = 0;

        var topMatch = FindByName(tops, segments[0]);
        if (topMatch is not null)
        {
            chain.Add(topMatch);
            current = topMatch;
            index = 1;
        }
        else
        {
            var holders = new List<(AccFolder Top, AccFolder Child)>();
            foreach (var top in tops)
            {
                var children = await _client.ListChildFoldersAsync(top.Id, cancellationToken).ConfigureAwait(false);
                var child = FindByName(children, segments[0]);
                if (child is not null)
                {
                    holders.Add((top, child));
                }
            }

            if (holders.Count == 1)
            {
                chain.Add(holders[0].Top);
                chain.Add(holders[0].Child);
                current = holders[0].Child;
                index = 1;
                _logger.LogInformation(
                    "The path starts under top folder {TopFolder}.",
                    holders[0].Top.Name);
            }
            else if (holders.Count > 1)
            {
                var options = holders.Select(holder => $"{holder.Top.Name} > {holder.Child.Name}").ToList();
                return new FolderResolveResult.Ambiguous(segments[0], options);
            }
            else
            {
                return new FolderResolveResult.NotFound(segments[0], null, tops.Select(folder => folder.Name).ToList());
            }
        }

        for (; index < segments.Count; index++)
        {
            var children = await _client.ListChildFoldersAsync(current.Id, cancellationToken).ConfigureAwait(false);
            var next = FindByName(children, segments[index]);
            if (next is null)
            {
                return new FolderResolveResult.NotFound(
                    segments[index],
                    current.Name,
                    children.Select(folder => folder.Name).ToList());
            }

            if (children.Count(folder => folder.Name.Trim().Equals(segments[index].Trim(), StringComparison.OrdinalIgnoreCase)) > 1)
            {
                _logger.LogWarning(
                    "More than one folder is named {Name} under {Parent}. Using {FolderId}.",
                    segments[index],
                    current.Name,
                    next.Id);
            }

            chain.Add(next);
            current = next;
        }

        return new FolderResolveResult.Success(current, chain);
    }
}
