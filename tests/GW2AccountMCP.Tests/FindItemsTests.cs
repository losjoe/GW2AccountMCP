using GW2AccountMCP.Items;
using GW2AccountMCP.Tools;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class FindItemsTests
{
    [Fact]
    public async Task FindItemsAsync_normalizes_query_uses_default_limit_and_returns_search_result_shape()
    {
        var index = new RecordingItemSearchIndex { Result = new ItemSearchResult([new ItemSearchCandidate(123, "Beta Blade", "Weapon", "Rare", 80, "Exact")], true) };
        var result = await new FindItemsTool(index).FindItemsAsync("  Beta\tBlade  ", null, CancellationToken.None);

        Assert.Equal("Beta Blade", result.NormalizedQuery);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal((123L, "Beta Blade", "Weapon", "Rare", 80, "Exact"), (candidate.Id, candidate.Name, candidate.Type, candidate.Rarity, candidate.Level, candidate.MatchKind));
        Assert.True(result.HasMore);
        Assert.Equal(("Beta Blade", 10), (index.Query, index.Limit));
    }

    [Theory]
    [MemberData(nameof(InvalidQueries))]
    public async Task FindItemsAsync_rejects_invalid_queries_before_index_calls(string? query)
    {
        var index = new RecordingItemSearchIndex();
        await Assert.ThrowsAsync<McpException>(() => new FindItemsTool(index).FindItemsAsync(query!, null, CancellationToken.None));
        Assert.Equal(0, index.CallCount);
    }

    public static TheoryData<string?> InvalidQueries => new() { null, " \t\n ", "x", new string('x', 101) };

    [Theory]
    [InlineData(0)]
    [InlineData(26)]
    public async Task FindItemsAsync_rejects_invalid_limits_before_index_calls(int limit)
    {
        var index = new RecordingItemSearchIndex();
        await Assert.ThrowsAsync<McpException>(() => new FindItemsTool(index).FindItemsAsync("Beta", limit, CancellationToken.None));
        Assert.Equal(0, index.CallCount);
    }

    [Theory]
    [InlineData("cache")]
    [InlineData("timeout")]
    public async Task FindItemsAsync_maps_cache_failures_to_redacted_mcp_error(string failure)
    {
        var index = new RecordingItemSearchIndex
        {
            Exception = failure == "cache"
                ? new ItemCacheException("private cache detail")
                : new OperationCanceledException("private timeout detail")
        };

        var error = await Assert.ThrowsAsync<McpException>(() => new FindItemsTool(index).FindItemsAsync("Beta", null, CancellationToken.None));
        Assert.Equal("Guild Wars 2 item search is unavailable. Try again later.", error.Message);
        Assert.DoesNotContain("private", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FindItemsAsync_propagates_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var index = new RecordingItemSearchIndex { Exception = new OperationCanceledException(cancellation.Token) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FindItemsTool(index).FindItemsAsync("Beta", null, cancellation.Token));
    }

    [Fact]
    public void NormalizeWhitespace_collapses_unicode_whitespace_and_preserves_case() =>
        Assert.Equal("Alpha Beta Gamma", ItemSearchIndex.NormalizeWhitespace(" \tAlpha\n\u00A0Beta\u2003Gamma  "));

    [Fact]
    public async Task SearchAsync_returns_complete_cached_metadata_with_exact_before_contains_and_deterministic_order()
    {
        var reader = new FakeCacheReader(Snapshot((1, "Beta Armor", "Armor", "Fine", 20), (2, "beta", "Consumable", "Masterwork", 30), (3, "Beta Blade", "Weapon", "Rare", 80), (4, "Beta", "Trinket", "Exotic", 80), (5, "Beta", "UpgradeComponent", "Ascended", 0), (6, "Beta\t Blade", "Weapon", "Legendary", 80)));
        var index = CreateIndex(reader);

        var exact = await index.SearchAsync("beta", 10, CancellationToken.None);
        var whitespace = await index.SearchAsync("beta blade", 10, CancellationToken.None);

        Assert.Equal([(4L, "Beta", "Trinket", "Exotic", 80, "Exact"), (5L, "Beta", "UpgradeComponent", "Ascended", 0, "Exact"), (2L, "beta", "Consumable", "Masterwork", 30, "Exact"), (1L, "Beta Armor", "Armor", "Fine", 20, "Contains"), (6L, "Beta\t Blade", "Weapon", "Legendary", 80, "Contains"), (3L, "Beta Blade", "Weapon", "Rare", 80, "Contains")], exact.Candidates.Select(item => (item.Id, item.Name, item.Type, item.Rarity, item.Level, item.MatchKind)));
        Assert.Equal([(6L, "Beta\t Blade", "Exact"), (3L, "Beta Blade", "Exact")], whitespace.Candidates.Select(item => (item.Id, item.Name, item.MatchKind)));
    }

    [Fact]
    public async Task SearchAsync_applies_limit_has_more_and_performs_no_enrichment()
    {
        var reader = new FakeCacheReader(Snapshot((4, "Zeta Beta", "Armor", "Fine", 1), (2, "Beta Amulet", "Trinket", "Rare", 2), (3, "Beta Amulet", "Trinket", "Exotic", 3), (1, "Beta Charm", "Trinket", "Masterwork", 4)));
        var result = await CreateIndex(reader).SearchAsync("BETA", 3, CancellationToken.None);

        Assert.Equal([2L, 3L, 1L], result.Candidates.Select(item => item.Id));
        Assert.All(result.Candidates, item => Assert.Equal("Contains", item.MatchKind));
        Assert.True(result.HasMore);
    }

    [Fact]
    public async Task SearchAsync_is_lazy_and_reuses_the_cache()
    {
        var reader = new FakeCacheReader(Snapshot((1, "Item One", "Armor", "Fine", 1)));
        var index = CreateIndex(reader);
        await index.SearchAsync("item", 1, CancellationToken.None);
        await index.SearchAsync("item", 1, CancellationToken.None);
        Assert.Equal(1, reader.LoadCallCount);
    }

    [Fact]
    public async Task SearchAsync_unchanged_no_match_checks_fingerprint()
    {
        var reader = new FakeCacheReader(Snapshot((1, "Beta", "Armor", "Fine", 1)));
        var result = await CreateIndex(reader).SearchAsync("missing", 1, CancellationToken.None);
        Assert.Empty(result.Candidates);
        Assert.False(result.HasMore);
        Assert.Equal((1, 1), (reader.LoadCallCount, reader.FingerprintCallCount));
    }

    [Fact]
    public async Task SearchAsync_changed_no_match_reloads_once_and_reruns_for_concurrent_callers()
    {
        var oldSnapshot = Snapshot((1, "Beta", "Armor", "Fine", 1));
        var newSnapshot = Snapshot(2, (2, "Needle", "Weapon", "Rare", 80));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new FakeCacheReader(oldSnapshot) { CurrentSnapshot = newSnapshot };
        reader.LoadResponse = (call, token) =>
        {
            if (call == 2) { reader.ReloadStarted.TrySetResult(); release.Task.Wait(token); }
            return call == 1 ? oldSnapshot : newSnapshot;
        };
        var index = CreateIndex(reader);
        await index.SearchAsync("beta", 1, CancellationToken.None);

        var first = index.SearchAsync("needle", 1, CancellationToken.None);
        await reader.ReloadStarted.Task;
        var second = index.SearchAsync("needle", 1, CancellationToken.None);
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(2L, Assert.Single(result.Candidates).Id));
        Assert.Equal(2, reader.LoadCallCount);
    }

    [Fact]
    public async Task SearchAsync_failed_reload_propagates_and_retains_old_index()
    {
        var oldSnapshot = Snapshot((1, "Beta", "Armor", "Fine", 1));
        var reader = new FakeCacheReader(oldSnapshot) { CurrentSnapshot = Snapshot(2, (2, "Needle", "Weapon", "Rare", 80)) };
        reader.LoadResponse = (call, _) => call == 2 ? throw new ItemCacheException("reload failed") : oldSnapshot;
        var index = CreateIndex(reader);
        await index.SearchAsync("beta", 1, CancellationToken.None);
        await Assert.ThrowsAsync<ItemCacheException>(() => index.SearchAsync("needle", 1, CancellationToken.None));
        Assert.Equal(1L, Assert.Single((await index.SearchAsync("beta", 1, CancellationToken.None)).Candidates).Id);
    }

    [Fact]
    public async Task SearchAsync_concurrent_initial_callers_share_one_load_and_caller_cancellation_does_not_poison_it()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new FakeCacheReader(Snapshot((1, "Item", "Armor", "Fine", 1)));
        reader.LoadResponse = (_, token) => { reader.LoadStarted.TrySetResult(); release.Task.Wait(token); return reader.CurrentSnapshot; };
        var index = CreateIndex(reader);
        using var cancellation = new CancellationTokenSource();

        var cancelled = index.SearchAsync("item", 1, cancellation.Token);
        await reader.LoadStarted.Task;
        var waiting = index.SearchAsync("item", 1, CancellationToken.None);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.SetResult();

        Assert.Single((await waiting).Candidates);
        Assert.Equal(1, reader.LoadCallCount);
    }

    [Fact]
    public async Task SearchAsync_application_stopping_cancels_the_shared_load()
    {
        var lifetime = new FakeHostApplicationLifetime();
        var reader = new FakeCacheReader(Snapshot((1, "Item", "Armor", "Fine", 1)));
        reader.LoadResponse = (_, token) => { reader.LoadStarted.TrySetResult(); Task.Delay(Timeout.InfiniteTimeSpan, token).GetAwaiter().GetResult(); throw new InvalidOperationException(); };
        var search = new ItemSearchIndex(reader, lifetime).SearchAsync("item", 1, CancellationToken.None);
        await reader.LoadStarted.Task;
        lifetime.StopApplication();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
    }

    [Fact]
    public async Task SearchAsync_failed_initial_load_retries_on_a_later_search()
    {
        var snapshot = Snapshot((1, "Item", "Armor", "Fine", 1));
        var reader = new FakeCacheReader(snapshot) { LoadResponse = (call, _) => call == 1 ? throw new ItemCacheException("initial failed") : snapshot };
        var index = CreateIndex(reader);
        await Assert.ThrowsAsync<ItemCacheException>(() => index.SearchAsync("item", 1, CancellationToken.None));
        Assert.Single((await index.SearchAsync("item", 1, CancellationToken.None)).Candidates);
        Assert.Equal(2, reader.LoadCallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SearchAsync_rejects_non_positive_direct_limits(int limit) =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CreateIndex(new FakeCacheReader(Snapshot((1, "Item", "Armor", "Fine", 1)))).SearchAsync("item", limit, CancellationToken.None));

    private static ItemSearchIndex CreateIndex(FakeCacheReader reader) => new(reader, new FakeHostApplicationLifetime());
    private static ItemCacheSnapshot Snapshot(params (long Id, string Name, string Type, string Rarity, int Level)[] items) => Snapshot(1, items);
    private static ItemCacheSnapshot Snapshot(int version, params (long Id, string Name, string Type, string Rarity, int Level)[] items) => new(items.Select(item => new CachedItem(item.Id, item.Name, item.Type, item.Rarity, item.Level)).ToArray(), new ItemCacheFingerprint(new ItemCachePathFingerprint("items.manifest.json", new DateTime(version, DateTimeKind.Utc), version), new ItemCachePathFingerprint("items.csv", new DateTime(version, DateTimeKind.Utc), version)), new DateTime(version, DateTimeKind.Utc));

    private sealed class FakeCacheReader(ItemCacheSnapshot initialSnapshot) : IItemCacheReader
    {
        private int loadCallCount;
        private int fingerprintCallCount;
        public ItemCacheSnapshot CurrentSnapshot { get; set; } = initialSnapshot;
        public Func<int, CancellationToken, ItemCacheSnapshot>? LoadResponse { get; set; }
        public int LoadCallCount => Volatile.Read(ref loadCallCount);
        public int FingerprintCallCount => Volatile.Read(ref fingerprintCallCount);
        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ItemCacheFingerprint GetCurrentFingerprint() { Interlocked.Increment(ref fingerprintCallCount); return CurrentSnapshot.Fingerprint; }
        public ItemCacheSnapshot Load(CancellationToken cancellationToken) { var call = Interlocked.Increment(ref loadCallCount); cancellationToken.ThrowIfCancellationRequested(); return LoadResponse?.Invoke(call, cancellationToken) ?? CurrentSnapshot; }
    }

    private sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => stopping.Cancel();
    }

    private sealed class RecordingItemSearchIndex : IItemSearchIndex
    {
        public ItemSearchResult Result { get; set; } = new([], false);
        public Exception? Exception { get; set; }
        public int CallCount { get; private set; }
        public string? Query { get; private set; }
        public int? Limit { get; private set; }
        public Task<ItemSearchResult> SearchAsync(string normalizedQuery, int limit, CancellationToken cancellationToken) { CallCount++; Query = normalizedQuery; Limit = limit; return Exception is null ? Task.FromResult(Result) : Task.FromException<ItemSearchResult>(Exception); }
    }
}
