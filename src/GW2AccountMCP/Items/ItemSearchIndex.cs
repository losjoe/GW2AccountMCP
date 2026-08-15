using System.Text;
using Microsoft.Extensions.Hosting;

namespace GW2AccountMCP.Items;

public interface IItemSearchIndex
{
    Task<ItemSearchResult> SearchAsync(string normalizedQuery, int limit, CancellationToken cancellationToken);
}

public sealed record ItemSearchResult(IReadOnlyList<ItemSearchCandidate> Candidates, bool HasMore);

public sealed record ItemSearchCandidate(long Id, string Name, string Type, string Rarity, int Level, string MatchKind);

public sealed class ItemSearchIndex : IItemSearchIndex
{
    private readonly IItemCacheReader cacheReader;
    private readonly IHostApplicationLifetime applicationLifetime;
    private readonly object stateLock = new();
    private IndexData? currentData;
    private Task<IndexData>? loadTask;

    public ItemSearchIndex(IItemCacheReader cacheReader, IHostApplicationLifetime applicationLifetime)
    {
        this.cacheReader = cacheReader;
        this.applicationLifetime = applicationLifetime;
    }

    public async Task<ItemSearchResult> SearchAsync(string normalizedQuery, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(normalizedQuery);
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var index = await GetInitialLoad().WaitAsync(cancellationToken).ConfigureAwait(false);
        var selection = Search(index, normalizedQuery.ToUpperInvariant(), limit);
        if (selection.Matches.Count == 0)
        {
            var fingerprint = cacheReader.GetCurrentFingerprint();
            if (fingerprint == index.Fingerprint)
            {
                return new ItemSearchResult([], false);
            }

            index = await GetOrStartReload(index).WaitAsync(cancellationToken).ConfigureAwait(false);
            selection = Search(index, normalizedQuery.ToUpperInvariant(), limit);
            if (selection.Matches.Count == 0)
            {
                return new ItemSearchResult([], false);
            }
        }

        var candidates = selection.Matches
            .Select(match => new ItemSearchCandidate(match.Entry.Id, match.Entry.Name, match.Entry.Type, match.Entry.Rarity, match.Entry.Level, match.MatchKind))
            .ToArray();

        return new ItemSearchResult(candidates, selection.HasMore);
    }

    public static string NormalizeWhitespace(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var normalized = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = normalized.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                normalized.Append(' ');
                pendingSpace = false;
            }

            normalized.Append(character);
        }

        return normalized.ToString();
    }

    private Task<IndexData> GetInitialLoad()
    {
        lock (stateLock)
        {
            if (currentData is not null)
            {
                return Task.FromResult(currentData);
            }

            if (loadTask is null || loadTask.IsCompleted)
            {
                loadTask = StartLoadLocked();
            }

            return loadTask;
        }
    }

    private Task<IndexData> GetOrStartReload(IndexData searchedIndex)
    {
        lock (stateLock)
        {
            if (currentData is not null && !ReferenceEquals(currentData, searchedIndex))
            {
                return Task.FromResult(currentData);
            }

            if (loadTask is null || loadTask.IsCompleted)
            {
                loadTask = StartLoadLocked();
            }

            return loadTask;
        }
    }

    private Task<IndexData> StartLoadLocked()
    {
        var stoppingToken = applicationLifetime.ApplicationStopping;
        return Task.Run(() => BuildAndPublish(stoppingToken), stoppingToken);
    }

    private IndexData BuildAndPublish(CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();
        var snapshot = cacheReader.Load(stoppingToken);
        var entries = snapshot.Items
            .Select(item => new SearchEntry(item.Id, item.Name, item.Type, item.Rarity, item.Level, NormalizeWhitespace(item.Name).ToUpperInvariant()))
            .ToArray();
        var data = new IndexData(
            snapshot.Fingerprint,
            entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ThenBy(entry => entry.Id).ToArray(),
            entries.OrderBy(entry => entry.MatchKey, StringComparer.Ordinal).ThenBy(entry => entry.Name, StringComparer.Ordinal).ThenBy(entry => entry.Id).ToArray());

        lock (stateLock)
        {
            currentData = data;
        }

        return data;
    }

    private static SearchSelection Search(IndexData index, string queryKey, int limit)
    {
        var matches = new List<SearchMatch>();
        var hasMore = false;

        foreach (var entry in index.ExactOrder)
        {
            if (entry.MatchKey == queryKey && !TryAdd(matches, new SearchMatch(entry, "Exact"), limit, ref hasMore))
            {
                return new SearchSelection(matches, hasMore);
            }
        }

        foreach (var entry in index.ContainsOrder)
        {
            if (entry.MatchKey != queryKey
                && entry.MatchKey.Contains(queryKey, StringComparison.Ordinal)
                && !TryAdd(matches, new SearchMatch(entry, "Contains"), limit, ref hasMore))
            {
                return new SearchSelection(matches, hasMore);
            }
        }

        return new SearchSelection(matches, hasMore);
    }

    private static bool TryAdd(List<SearchMatch> matches, SearchMatch match, int limit, ref bool hasMore)
    {
        if (matches.Count == limit)
        {
            hasMore = true;
            return false;
        }

        matches.Add(match);
        return true;
    }

    private sealed record SearchEntry(long Id, string Name, string Type, string Rarity, int Level, string MatchKey);
    private sealed record SearchMatch(SearchEntry Entry, string MatchKind);
    private sealed record SearchSelection(IReadOnlyList<SearchMatch> Matches, bool HasMore);
    private sealed record IndexData(ItemCacheFingerprint Fingerprint, SearchEntry[] ExactOrder, SearchEntry[] ContainsOrder);
}
