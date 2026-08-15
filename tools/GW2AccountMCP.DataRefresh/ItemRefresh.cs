using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Globalization;

namespace GW2AccountMCP.DataRefresh;

public sealed record ItemCatalogItem(long Id, string Name, string Type, string Rarity, int Level);
public sealed record ItemRefreshSummary(int ItemCount, int HttpAttemptCount, string CsvFileName, string CsvSha256, bool IsTestCache = false)
{
    public int SourceItemCount { get; init; } = ItemCount;
    public int NamedItemCount => ItemCount;
    public int ExcludedBlankNameCount => SourceItemCount - ItemCount;
}

public interface IItemCatalogDownloadClient
{
    int AttemptCount { get; }
    Task<IReadOnlyList<long>> GetRootIdsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ItemCatalogItem>> GetDefinitionsAsync(IReadOnlyCollection<long> requestedIds, CancellationToken cancellationToken);
}

public interface IApiStartGate { Task WaitAsync(CancellationToken cancellationToken); }

public sealed class ApiStartGate(TimeProvider timeProvider) : IApiStartGate
{
    private static readonly TimeSpan MinimumStartInterval = TimeSpan.FromMilliseconds(250);
    private readonly SemaphoreSlim mutex = new(1, 1);
    private DateTimeOffset nextStart;

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = timeProvider.GetUtcNow();
            if (nextStart > now)
            {
                await Task.Delay(nextStart - now, timeProvider, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            nextStart = timeProvider.GetUtcNow() + MinimumStartInterval;
        }
        finally
        {
            mutex.Release();
        }
    }
}

public enum ItemCatalogDownloadFailureKind { Timeout, Transport, HttpStatus, InvalidResponse }
public sealed class ItemCatalogDownloadException : Exception
{
    public ItemCatalogDownloadException() : this(ItemCatalogDownloadFailureKind.InvalidResponse) { }
    public ItemCatalogDownloadException(ItemCatalogDownloadFailureKind kind) : base("The Guild Wars 2 item catalog response is invalid.") => Kind = kind;
    public ItemCatalogDownloadFailureKind Kind { get; }
}

public sealed class ItemCatalogDownloadClient(HttpClient httpClient, TimeProvider? timeProvider = null, IApiStartGate? startGate = null, TimeSpan? attemptTimeout = null) : IItemCatalogDownloadClient
{
    public const string SchemaVersion = "2025-08-29T01:00:00.000Z";
    public static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromSeconds(15);
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IApiStartGate startGate = startGate ?? new ApiStartGate(timeProvider ?? TimeProvider.System);
    private readonly TimeSpan attemptTimeout = attemptTimeout ?? DefaultAttemptTimeout;
    private int attemptCount;
    public int AttemptCount => Volatile.Read(ref attemptCount);

    public async Task<IReadOnlyList<long>> GetRootIdsAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"/v2/items?lang=en&v={Uri.EscapeDataString(SchemaVersion)}", cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            throw new ItemCatalogDownloadException();
        }

        var ids = new List<long>();
        var unique = new HashSet<long>();
        foreach (var value in document.RootElement.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var id) || id <= 0 || !unique.Add(id))
            {
                throw new ItemCatalogDownloadException();
            }
            ids.Add(id);
        }
        return ids;
    }

    public async Task<IReadOnlyList<ItemCatalogItem>> GetDefinitionsAsync(IReadOnlyCollection<long> requestedIds, CancellationToken cancellationToken)
    {
        if (requestedIds.Count is < 1 or > 200 || requestedIds.Any(id => id <= 0) || requestedIds.Distinct().Count() != requestedIds.Count)
        {
            throw new ArgumentException("Item definition batches must contain 1 through 200 unique positive IDs.", nameof(requestedIds));
        }

        var ids = requestedIds.Order().ToArray();
        using var document = await GetJsonAsync("/v2/items?ids=" + string.Join(',', ids) + $"&lang=en&v={Uri.EscapeDataString(SchemaVersion)}", cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ItemCatalogDownloadException();
        }

        var expected = ids.ToHashSet();
        var actual = new List<ItemCatalogItem>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!TryReadItem(element, out var item) || !expected.Remove(item.Id))
            {
                throw new ItemCatalogDownloadException();
            }
            actual.Add(item);
        }
        if (expected.Count != 0)
        {
            throw new ItemCatalogDownloadException();
        }
        return actual;
    }

    private async Task<JsonDocument> GetJsonAsync(string relativeUri, CancellationToken cancellationToken)
    {
        for (var retry = 0; retry < 2; retry++)
        {
            await startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref attemptCount);
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCancellation.CancelAfter(attemptTimeout);
            try
            {
                using var response = await httpClient.GetAsync(relativeUri, HttpCompletionOption.ResponseHeadersRead, attemptCancellation.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    try
                    {
                        await using var stream = await response.Content.ReadAsStreamAsync(attemptCancellation.Token).ConfigureAwait(false);
                        return await JsonDocument.ParseAsync(stream, cancellationToken: attemptCancellation.Token).ConfigureAwait(false);
                    }
                    catch (JsonException)
                    {
                        throw new ItemCatalogDownloadException(ItemCatalogDownloadFailureKind.InvalidResponse);
                    }
                }

                if (retry == 0 && IsTransient(response.StatusCode))
                {
                    var retryAfter = GetRetryAfter(response.Headers.RetryAfter);
                    if (retryAfter is { } delay)
                    {
                        await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }
                throw new ItemCatalogDownloadException(ItemCatalogDownloadFailureKind.HttpStatus);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (attemptCancellation.IsCancellationRequested && retry == 0) { }
            catch (OperationCanceledException) when (attemptCancellation.IsCancellationRequested) { throw new ItemCatalogDownloadException(ItemCatalogDownloadFailureKind.Timeout); }
            catch (HttpRequestException) when (retry == 0) { }
            catch (HttpRequestException) { throw new ItemCatalogDownloadException(ItemCatalogDownloadFailureKind.Transport); }
            catch (ItemCatalogDownloadException) { throw; }
            catch (Exception) { throw new ItemCatalogDownloadException(ItemCatalogDownloadFailureKind.InvalidResponse); }
        }
        throw new ItemCatalogDownloadException(ItemCatalogDownloadFailureKind.Timeout);
    }

    private TimeSpan? GetRetryAfter(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta && delta >= TimeSpan.Zero) return delta;
        if (retryAfter?.Date is { } date)
        {
            var delay = date - timeProvider.GetUtcNow();
            return delay >= TimeSpan.Zero ? delay : null;
        }
        return null;
    }

    private static bool IsTransient(HttpStatusCode status) => status is (HttpStatusCode)429 or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static bool TryReadItem(JsonElement element, out ItemCatalogItem item)
    {
        item = default!;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.Number || !idValue.TryGetInt64(out var id) || id <= 0 ||
            !TryString(element, "name", out var name) || !TryRequiredString(element, "type", out var type) || !TryRequiredString(element, "rarity", out var rarity) ||
            !element.TryGetProperty("level", out var levelValue) || levelValue.ValueKind != JsonValueKind.Number || !levelValue.TryGetInt32(out var level) || level < 0)
        {
            return false;
        }
        item = new ItemCatalogItem(id, name, type, rarity, level);
        return true;
    }

    private static bool TryRequiredString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(property, out var json) && json.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value = json.GetString()!);
    }
    private static bool TryString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(property, out var json) && json.ValueKind == JsonValueKind.String && (value = json.GetString()!) is not null;
    }
}

public interface IItemCacheRefreshService
{
    Task<ItemRefreshSummary> RefreshAsync(string outputDirectory, CancellationToken cancellationToken);
    Task<ItemRefreshSummary> RefreshTestAsync(string outputDirectory, CancellationToken cancellationToken);
}

public sealed class ItemStagingException(bool freshPreflight) : Exception
{
    public bool FreshPreflight { get; } = freshPreflight;
}
public sealed class ItemCacheIncompleteException(int completedBatches, int totalBatches, int timeoutCount, int transportCount, int httpStatusCount, int invalidResponseCount) : Exception
{
    public int CompletedBatches { get; } = completedBatches;
    public int TotalBatches { get; } = totalBatches;
    public int TimeoutCount { get; } = timeoutCount;
    public int TransportCount { get; } = transportCount;
    public int HttpStatusCount { get; } = httpStatusCount;
    public int InvalidResponseCount { get; } = invalidResponseCount;
}

public sealed class ItemCacheRefreshService(IItemCatalogDownloadClient client, ItemCachePublisher publisher) : IItemCacheRefreshService
{
    private const int BatchSize = 200;
    private const int StateVersion = 1;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex StateTempName = new("^items\\.resume-state\\.[0-9a-f]{32}\\.tmp$", RegexOptions.CultureInvariant);
    private static readonly Regex BatchName = new("^items\\.batch\\.([0-9]+)\\.([0-9a-f]{64})\\.json$", RegexOptions.CultureInvariant);
    private static readonly Regex BatchTempName = new("^items\\.batch\\.[0-9a-f]{32}\\.tmp$", RegexOptions.CultureInvariant);

    public async Task<ItemRefreshSummary> RefreshAsync(string outputDirectory, CancellationToken cancellationToken)
    {
        var output = Path.GetFullPath(outputDirectory);
        var stage = Path.Combine(output, ".items-staging");
        if (File.Exists(stage)) throw new ItemStagingException(false);
        ResumeState? existingState = null;
        Dictionary<int, string>? existingCompleted = null;
        if (Directory.Exists(stage))
        {
            existingState = ReadState(stage, freshPreflight: false);
            if (existingState.Status == "publishing") return RecoverPublication(output, stage, existingState, cancellationToken);
            existingCompleted = ValidatePartialShards(stage, existingState);
        }

        var roots = (await client.GetRootIdsAsync(cancellationToken).ConfigureAwait(false)).Order().ToArray();
        ValidateRootIds(roots);
        var rootHash = HashRootIds(roots);
        var batchCount = (roots.Length + BatchSize - 1) / BatchSize;
        ResumeState state;
        Dictionary<int, string> completed;
        if (existingState is not null)
        {
            state = existingState;
            if (state.Status != "downloading" || state.RootCount != roots.Length || !string.Equals(state.RootSha256, rootHash, StringComparison.Ordinal)) throw new ItemStagingException(false);
            completed = ValidateShards(stage, state, roots, batchCount, existingCompleted!);
        }
        else
        {
            Directory.CreateDirectory(stage);
            state = new ResumeState(StateVersion, ItemCatalogDownloadClient.SchemaVersion, "en", BatchSize, roots.Length, rootHash, "downloading");
            WriteState(stage, state);
            completed = [];
        }

        var failures = await DownloadMissingAsync(stage, state, roots, batchCount, completed, cancellationToken).ConfigureAwait(false);
        completed = ValidateShards(stage, state, roots, batchCount);
        cancellationToken.ThrowIfCancellationRequested();
        if (completed.Count != batchCount) throw new ItemCacheIncompleteException(completed.Count, batchCount, failures.Timeout, failures.Transport, failures.HttpStatus, failures.InvalidResponse);

        var artifact = publisher.CreateGeneration(output, EnumerateShards(completed, roots, cancellationToken), roots.Length, cancellationToken, repairExisting: true);
        var generatedAtUtc = publisher.GetGeneratedAtUtc();
        var manifest = publisher.CreateManifestBytes(artifact, artifact.ItemCount, generatedAtUtc);
        var publishingState = state with
        {
            Status = "publishing",
            GeneratedAtUtc = generatedAtUtc,
            CsvFileName = artifact.CsvFileName,
            CsvSha256 = artifact.CsvSha256,
            PublishedNamedCount = artifact.ItemCount,
            ManifestSha256 = Hash(manifest)
        };
        WriteState(stage, publishingState);
        publisher.PublishManifestBytes(output, manifest, cancellationToken);
        return new ItemRefreshSummary(artifact.ItemCount, client.AttemptCount, artifact.CsvFileName, artifact.CsvSha256) { SourceItemCount = roots.Length };
    }

    public async Task<ItemRefreshSummary> RefreshTestAsync(string outputDirectory, CancellationToken cancellationToken)
    {
        var rootIds = await client.GetRootIdsAsync(cancellationToken).ConfigureAwait(false);
        ValidateRootIds(rootIds);
        var selectedIds = rootIds.Order().Take(200).ToArray();
        if (selectedIds.Length != 200) throw new ItemCatalogDownloadException();
        var items = (await client.GetDefinitionsAsync(selectedIds, cancellationToken).ConfigureAwait(false)).OrderBy(item => item.Id).ToArray();
        ValidateItems(items, selectedIds);
        cancellationToken.ThrowIfCancellationRequested();
        var artifact = publisher.Publish(outputDirectory, items.Where(item => !string.IsNullOrWhiteSpace(item.Name)).ToArray(), cancellationToken);
        return new ItemRefreshSummary(artifact.ItemCount, client.AttemptCount, artifact.CsvFileName, artifact.CsvSha256, IsTestCache: true) { SourceItemCount = items.Length };
    }

    private static void ValidateRootIds(IReadOnlyList<long> rootIds)
    {
        if (rootIds.Count == 0 || rootIds.Any(id => id <= 0) || rootIds.Distinct().Count() != rootIds.Count) throw new ItemCatalogDownloadException();
    }

    public static void PrepareFreshOutput(string outputDirectory)
    {
        var stage = Path.Combine(Path.GetFullPath(outputDirectory), ".items-staging");
        if (File.Exists(stage)) throw new ItemStagingException(true);
        if (!Directory.Exists(stage)) return;
        CleanupStage(stage, freshPreflight: true);
    }

    private ItemRefreshSummary RecoverPublication(string output, string stage, ResumeState state, CancellationToken cancellationToken)
    {
        ValidateState(state);
        var roots = Array.Empty<long>();
        var batchCount = (state.RootCount + BatchSize - 1) / BatchSize;
        var completed = ValidateShardsForPublishing(stage, state, batchCount, out roots);
        var artifact = publisher.CreateGeneration(output, EnumerateShards(completed, roots, cancellationToken), state.RootCount, cancellationToken, repairExisting: true);
        var manifest = publisher.CreateManifestBytes(artifact, state.PublishedNamedCount!.Value, state.GeneratedAtUtc!);
        if (artifact.ItemCount != state.PublishedNamedCount || !string.Equals(artifact.CsvFileName, state.CsvFileName, StringComparison.Ordinal) || !string.Equals(artifact.CsvSha256, state.CsvSha256, StringComparison.Ordinal) || !string.Equals(Hash(manifest), state.ManifestSha256, StringComparison.Ordinal)) throw new ItemStagingException(false);
        var manifestPath = Path.Combine(output, "items.manifest.json");
        if (!File.Exists(manifestPath) || !File.ReadAllBytes(manifestPath).AsSpan().SequenceEqual(manifest)) publisher.PublishManifestBytes(output, manifest, cancellationToken);
        return new ItemRefreshSummary(artifact.ItemCount, client.AttemptCount, artifact.CsvFileName, artifact.CsvSha256) { SourceItemCount = state.RootCount };
    }

    private async Task<FailureCounts> DownloadMissingAsync(string stage, ResumeState state, long[] roots, int batchCount, Dictionary<int, string> completed, CancellationToken cancellationToken)
    {
        var active = new Dictionary<Task<IReadOnlyList<ItemCatalogItem>>, int>();
        var failures = new Dictionary<int, ItemCatalogDownloadFailureKind>();
        var next = 0;
        Exception? terminal = null;
        void Schedule()
        {
            while (!cancellationToken.IsCancellationRequested && terminal is null && active.Count < 4 && next < batchCount)
            {
                var index = next++;
                if (completed.ContainsKey(index)) continue;
                try { active.Add(client.GetDefinitionsAsync(GetSlice(roots, index), cancellationToken), index); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (ItemCatalogDownloadException exception) { failures[index] = exception.Kind; }
                catch (Exception exception) { terminal = exception; }
            }
        }
        Schedule();
        while (active.Count != 0)
        {
            var task = await Task.WhenAny(active.Keys).ConfigureAwait(false);
            var index = active[task];
            active.Remove(task);
            IReadOnlyList<ItemCatalogItem> items;
            try
            {
                items = await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { continue; }
            catch (ItemCatalogDownloadException exception) { failures[index] = exception.Kind; Schedule(); continue; }
            catch (Exception exception) { terminal ??= exception; continue; }
            try { completed[index] = WriteShard(stage, state, index, GetSlice(roots, index), items); }
            catch (Exception exception) { terminal ??= exception; }
            Schedule();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (terminal is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(terminal).Throw();
        return FailureCounts.From(failures.Values);
    }

    private static long[] GetSlice(long[] roots, int batchIndex)
    {
        var start = batchIndex * BatchSize;
        var count = Math.Min(BatchSize, roots.Length - start);
        var slice = new long[count];
        Array.Copy(roots, start, slice, 0, count);
        return slice;
    }

    private IEnumerable<ItemCatalogItem> EnumerateShards(Dictionary<int, string> completed, long[] roots, CancellationToken cancellationToken)
    {
        foreach (var pair in completed.OrderBy(pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in ReadShard(pair.Value, pair.Key, roots.Skip(pair.Key * BatchSize).Take(Math.Min(BatchSize, roots.Length - pair.Key * BatchSize)).ToArray(), expectedRootHash: null)) yield return item;
        }
    }

    private static string HashRootIds(long[] roots) => Hash(Utf8.GetBytes(string.Join('\n', roots) + "\n"));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string StatePath(string stage) => Path.Combine(stage, "items.resume-state.json");

    private static ResumeState ReadState(string stage, bool freshPreflight)
    {
        ValidateStageLeaves(stage, freshPreflight);
        try { return DeserializeState(File.ReadAllBytes(StatePath(stage))); }
        catch { throw new ItemStagingException(freshPreflight); }
    }

    private static ResumeState DeserializeState(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var properties = PropertyNames(document.RootElement);
        var state = JsonSerializer.Deserialize<ResumeState>(bytes, JsonOptions) ?? throw new JsonException();
        var expected = state.Status == "publishing"
            ? new[] { "formatVersion", "gw2SchemaVersion", "language", "batchSize", "rootCount", "rootSha256", "status", "generatedAtUtc", "csvFileName", "csvSha256", "publishedNamedCount", "manifestSha256" }
            : new[] { "formatVersion", "gw2SchemaVersion", "language", "batchSize", "rootCount", "rootSha256", "status" };
        if (properties is null || !properties.SetEquals(expected)) throw new JsonException();
        ValidateState(state);
        return state;
    }

    private static void ValidateState(ResumeState state)
    {
        if (state.FormatVersion != StateVersion || state.Gw2SchemaVersion != ItemCatalogDownloadClient.SchemaVersion || state.Language != "en" || state.BatchSize != BatchSize || state.RootCount <= 0 || !IsHash(state.RootSha256) || state.Status is not ("downloading" or "publishing")) throw new ItemStagingException(false);
        if (state.Status == "publishing" && (string.IsNullOrWhiteSpace(state.GeneratedAtUtc) || string.IsNullOrWhiteSpace(state.CsvFileName) || !IsHash(state.CsvSha256) || !IsHash(state.ManifestSha256) || state.PublishedNamedCount is not > 0 || state.PublishedNamedCount > state.RootCount || state.GeneratedAtUtc != DateTimeOffset.Parse(state.GeneratedAtUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind).UtcDateTime.ToString("O") || state.CsvFileName != $"items.{state.CsvSha256}.csv")) throw new ItemStagingException(false);
    }

    private static Dictionary<int, string> ValidatePartialShards(string stage, ResumeState state)
    {
        ValidateStageLeaves(stage, freshPreflight: false);
        var completed = new Dictionary<int, string>();
        var batchCount = (state.RootCount + BatchSize - 1) / BatchSize;
        foreach (var path in Directory.EnumerateFiles(stage, "items.batch.*.json"))
        {
            var match = BatchName.Match(Path.GetFileName(path));
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var index) || index < 0 || index >= batchCount || !completed.TryAdd(index, path)) throw new ItemStagingException(false);
            ReadShard(path, index, expectedIds: null, state.RootSha256);
        }
        return completed;
    }

    private static Dictionary<int, string> ValidateShards(string stage, ResumeState state, long[] roots, int batchCount, Dictionary<int, string>? completed = null)
    {
        completed ??= ValidatePartialShards(stage, state);
        if (completed.Keys.Any(index => index >= batchCount)) throw new ItemStagingException(false);
        foreach (var pair in completed) ReadShard(pair.Value, pair.Key, GetSlice(roots, pair.Key), state.RootSha256);
        return completed;
    }

    private static Dictionary<int, string> ValidateShardsForPublishing(string stage, ResumeState state, int batchCount, out long[] roots)
    {
        ValidateStageLeaves(stage, freshPreflight: false);
        var completed = new Dictionary<int, string>();
        var items = new List<long>(state.RootCount);
        foreach (var path in Directory.EnumerateFiles(stage, "items.batch.*.json"))
        {
            var match = BatchName.Match(Path.GetFileName(path));
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var index) || index < 0 || index >= batchCount || !completed.TryAdd(index, path)) throw new ItemStagingException(false);
            var shard = ReadShard(path, index, expectedIds: null, state.RootSha256);
            items.AddRange(shard.Select(item => item.Id));
        }
        roots = items.Order().ToArray();
        if (completed.Count != batchCount || roots.Length != state.RootCount || roots.Distinct().Count() != roots.Length || HashRootIds(roots) != state.RootSha256) throw new ItemStagingException(false);
        foreach (var pair in completed) ReadShard(pair.Value, pair.Key, GetSlice(roots, pair.Key), state.RootSha256);
        return completed;
    }

    private static string WriteShard(string stage, ResumeState state, int index, long[] expected, IReadOnlyList<ItemCatalogItem> items)
    {
        ValidateItems(items, expected);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ResumeShard(StateVersion, index, state.RootSha256, items.OrderBy(item => item.Id).ToArray()), JsonOptions);
        var hash = Hash(bytes);
        var destination = Path.Combine(stage, $"items.batch.{index}.{hash}.json");
        if (File.Exists(destination)) { ReadShard(destination, index, expected, state.RootSha256); return destination; }
        AtomicWrite(Path.Combine(stage, $"items.batch.{Guid.NewGuid():N}.tmp"), destination, bytes);
        return destination;
    }

    private static ItemCatalogItem[] ReadShard(string path, int index, long[]? expectedIds, string? expectedRootHash)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var match = BatchName.Match(Path.GetFileName(path));
            if (!match.Success || Hash(bytes) != match.Groups[2].Value) throw new ItemStagingException(false);
            using var document = JsonDocument.Parse(bytes);
            if (PropertyNames(document.RootElement) is not { } shardProperties || !shardProperties.SetEquals(["formatVersion", "batchIndex", "rootSha256", "items"])) throw new ItemStagingException(false);
            var shard = JsonSerializer.Deserialize<ResumeShard>(bytes, JsonOptions) ?? throw new ItemStagingException(false);
            if (shard.FormatVersion != StateVersion || shard.BatchIndex != index || !IsHash(shard.RootSha256) || (expectedRootHash is not null && shard.RootSha256 != expectedRootHash)) throw new ItemStagingException(false);
            if (shard.Items is null || shard.Items.Any(item => item is null) || document.RootElement.GetProperty("items").EnumerateArray().Any(item => PropertyNames(item) is not { } itemProperties || !itemProperties.SetEquals(["id", "name", "type", "rarity", "level"]))) throw new ItemStagingException(false);
            if (shard.Items.Length is < 1 or > BatchSize || shard.Items.Any(item => !IsValidSourceItem(item)) || !IsStrictlyAscending(shard.Items.Select(item => item.Id))) throw new ItemStagingException(false);
            if (expectedIds is not null && (shard.Items.Length != expectedIds.Length || !shard.Items.Select(item => item.Id).SequenceEqual(expectedIds))) throw new ItemStagingException(false);
            return shard.Items;
        }
        catch (ItemStagingException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException) { throw new ItemStagingException(false); }
    }

    private static void ValidateItems(IEnumerable<ItemCatalogItem> items, long[] expected)
    {
        var actual = items.OrderBy(item => item.Id).ToArray();
        if (actual.Length != expected.Length || actual.Any(item => !IsValidSourceItem(item)) || !actual.Select(item => item.Id).SequenceEqual(expected)) throw new ItemCatalogDownloadException();
    }
    private static bool IsValidItem(ItemCatalogItem item) => item.Id > 0 && !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Type) && !string.IsNullOrWhiteSpace(item.Rarity) && item.Level >= 0;
    private static bool IsValidSourceItem(ItemCatalogItem item) => item.Id > 0 && item.Name is not null && !string.IsNullOrWhiteSpace(item.Type) && !string.IsNullOrWhiteSpace(item.Rarity) && item.Level >= 0;
    private static bool IsStrictlyAscending(IEnumerable<long> ids)
    {
        var previous = 0L;
        foreach (var id in ids) { if (id <= previous) return false; previous = id; }
        return true;
    }
    private static bool IsHash(string? value) => value is not null && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static HashSet<string>? PropertyNames(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!names.Add(property.Name)) return null;
        return names;
    }

    private static void WriteState(string stage, ResumeState state) => AtomicWrite(Path.Combine(stage, $"items.resume-state.{Guid.NewGuid():N}.tmp"), StatePath(stage), JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions));
    private static void AtomicWrite(string temporary, string destination, byte[] bytes)
    {
        WriteAllBytesFlushed(temporary, bytes);
        if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
    }
    private static void WriteAllBytesFlushed(string path, byte[] bytes) { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough); stream.Write(bytes); stream.Flush(flushToDisk: true); }

    private static void ValidateStageLeaves(string stage, bool freshPreflight)
    {
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(stage))
            {
                var name = Path.GetFileName(path);
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || (name != "items.resume-state.json" && !StateTempName.IsMatch(name) && !BatchName.IsMatch(name) && !BatchTempName.IsMatch(name))) throw new ItemStagingException(freshPreflight);
            }
        }
        catch (ItemStagingException) { throw; }
        catch { throw new ItemStagingException(freshPreflight); }
    }

    private static void CleanupStage(string stage, bool freshPreflight)
    {
        ValidateStageLeaves(stage, freshPreflight);
        try
        {
            foreach (var path in Directory.EnumerateFiles(stage)) File.Delete(path);
            Directory.Delete(stage);
        }
        catch { throw new ItemStagingException(freshPreflight); }
    }

    private sealed record ResumeState(int FormatVersion, string Gw2SchemaVersion, string Language, int BatchSize, int RootCount, string RootSha256, string Status, string? GeneratedAtUtc = null, string? CsvFileName = null, string? CsvSha256 = null, int? PublishedNamedCount = null, string? ManifestSha256 = null);
    private sealed record ResumeShard(int FormatVersion, int BatchIndex, string RootSha256, ItemCatalogItem[] Items);
    private readonly record struct FailureCounts(int Timeout, int Transport, int HttpStatus, int InvalidResponse)
    {
        public static FailureCounts From(IEnumerable<ItemCatalogDownloadFailureKind> kinds) => new(kinds.Count(kind => kind == ItemCatalogDownloadFailureKind.Timeout), kinds.Count(kind => kind == ItemCatalogDownloadFailureKind.Transport), kinds.Count(kind => kind == ItemCatalogDownloadFailureKind.HttpStatus), kinds.Count(kind => kind == ItemCatalogDownloadFailureKind.InvalidResponse));
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}

public sealed class ItemCachePublishException : Exception { public ItemCachePublishException() : base("The item cache could not be published.") { } }
public sealed record ItemCacheArtifact(string CsvFileName, string CsvSha256, int ItemCount = 0);

public sealed class ItemCachePublisher(TimeProvider timeProvider, Action? beforeManifestReplacement = null, Action? afterManifestReplacement = null)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex GenerationName = new("^items\\.([0-9a-f]{64})\\.csv$", RegexOptions.CultureInvariant);
    private static readonly Regex CsvTempName = new("^items\\.[0-9a-f]{32}\\.tmp$", RegexOptions.CultureInvariant);
    private static readonly Regex ManifestTempName = new("^items\\.manifest\\.[0-9a-f]{32}\\.tmp$", RegexOptions.CultureInvariant);
    public ItemCacheArtifact CreateArtifact(IReadOnlyCollection<ItemCatalogItem> items)
    {
        var bytes = CreateCsv(items);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new ItemCacheArtifact($"items.{hash}.csv", hash, items.Count);
    }

    public ItemCacheArtifact Publish(string outputDirectory, IReadOnlyCollection<ItemCatalogItem> items, CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(outputDirectory, "items.manifest.json");
        var temporaryFiles = new List<string>();
        try
        {
            Directory.CreateDirectory(outputDirectory);
            Cleanup(outputDirectory, manifestPath);
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = CreateCsv(items);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var artifact = new ItemCacheArtifact($"items.{hash}.csv", hash, items.Count);
            var generationPath = Path.Combine(outputDirectory, artifact.CsvFileName);
            if (File.Exists(generationPath))
            {
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(generationPath)), Convert.FromHexString(hash))) throw new ItemCachePublishException();
            }
            else
            {
                var csvTemp = TempPath(outputDirectory, "items"); temporaryFiles.Add(csvTemp);
                WriteAllBytesFlushed(csvTemp, bytes);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(csvTemp, generationPath);
                temporaryFiles.Remove(csvTemp);
            }

            var generatedAtUtc = timeProvider.GetUtcNow().UtcDateTime.ToString("O");
            var manifest = $"{{\"formatVersion\":1,\"generatedAtUtc\":\"{generatedAtUtc}\",\"gw2SchemaVersion\":\"{ItemCatalogDownloadClient.SchemaVersion}\",\"language\":\"en\",\"rowCount\":{items.Count.ToString(CultureInfo.InvariantCulture)},\"csvFileName\":\"{artifact.CsvFileName}\",\"csvSha256\":\"{hash}\"}}";
            var manifestTemp = TempPath(outputDirectory, "items.manifest"); temporaryFiles.Add(manifestTemp);
            WriteAllBytesFlushed(manifestTemp, Utf8.GetBytes(manifest));
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(manifestPath)) File.Replace(manifestTemp, manifestPath, null); else File.Move(manifestTemp, manifestPath);
            temporaryFiles.Remove(manifestTemp);
            return artifact;
        }
        catch (OperationCanceledException) { throw; }
        catch (ItemCachePublishException) { throw; }
        catch (Exception) { throw new ItemCachePublishException(); }
        finally
        {
            foreach (var file in temporaryFiles) { try { File.Delete(file); } catch { } }
        }
    }

    public string GetGeneratedAtUtc() => timeProvider.GetUtcNow().UtcDateTime.ToString("O");

    public ItemCacheArtifact CreateGeneration(string outputDirectory, IEnumerable<ItemCatalogItem> orderedItems, int expectedCount, CancellationToken cancellationToken, bool repairExisting = false)
    {
        var temporaryFiles = new List<string>();
        try
        {
            Directory.CreateDirectory(outputDirectory);
            Cleanup(outputDirectory, Path.Combine(outputDirectory, "items.manifest.json"));
            var temp = TempPath(outputDirectory, "items");
            temporaryFiles.Add(temp);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var namedCount = 0;
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                Write(stream, hash, "id,name,type,rarity,level\r\n");
                long previous = 0;
                var sourceCount = 0;
                foreach (var item in orderedItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.Id <= previous || item.Id <= 0 || item.Name is null || string.IsNullOrWhiteSpace(item.Type) || string.IsNullOrWhiteSpace(item.Rarity) || item.Level < 0) throw new ItemCachePublishException();
                    if (!string.IsNullOrWhiteSpace(item.Name)) { Write(stream, hash, string.Concat(item.Id.ToString(CultureInfo.InvariantCulture), ",", Escape(item.Name), ",", Escape(item.Type), ",", Escape(item.Rarity), ",", item.Level.ToString(CultureInfo.InvariantCulture), "\r\n")); namedCount++; }
                    previous = item.Id;
                    sourceCount++;
                }
                if (sourceCount != expectedCount || namedCount == 0) throw new ItemCachePublishException();
                stream.Flush(flushToDisk: true);
            }
            var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            var artifact = new ItemCacheArtifact($"items.{sha}.csv", sha, namedCount);
            var generation = Path.Combine(outputDirectory, artifact.CsvFileName);
            if (File.Exists(generation))
            {
                if (CryptographicOperations.FixedTimeEquals(HashFile(generation), Convert.FromHexString(sha))) File.Delete(temp);
                else if (repairExisting) File.Replace(temp, generation, null);
                else throw new ItemCachePublishException();
            }
            else File.Move(temp, generation);
            temporaryFiles.Remove(temp);
            return artifact;
        }
        catch (OperationCanceledException) { throw; }
        catch (ItemCachePublishException) { throw; }
        catch (Exception) { throw new ItemCachePublishException(); }
        finally { foreach (var temporary in temporaryFiles) { try { File.Delete(temporary); } catch { } } }
    }

    public byte[] CreateManifestBytes(ItemCacheArtifact artifact, int itemCount, string generatedAtUtc) => Utf8.GetBytes($"{{\"formatVersion\":1,\"generatedAtUtc\":\"{generatedAtUtc}\",\"gw2SchemaVersion\":\"{ItemCatalogDownloadClient.SchemaVersion}\",\"language\":\"en\",\"rowCount\":{itemCount.ToString(CultureInfo.InvariantCulture)},\"csvFileName\":\"{artifact.CsvFileName}\",\"csvSha256\":\"{artifact.CsvSha256}\"}}");

    public void PublishManifestBytes(string outputDirectory, byte[] manifest, CancellationToken cancellationToken)
    {
        var temporaryFiles = new List<string>();
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var manifestPath = Path.Combine(outputDirectory, "items.manifest.json");
            var temporary = TempPath(outputDirectory, "items.manifest");
            temporaryFiles.Add(temporary);
            WriteAllBytesFlushed(temporary, manifest);
            cancellationToken.ThrowIfCancellationRequested();
            beforeManifestReplacement?.Invoke();
            if (File.Exists(manifestPath)) File.Replace(temporary, manifestPath, null); else File.Move(temporary, manifestPath);
            afterManifestReplacement?.Invoke();
            temporaryFiles.Remove(temporary);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new ItemCachePublishException(); }
        finally { foreach (var temporary in temporaryFiles) { try { File.Delete(temporary); } catch { } } }
    }

    private static byte[] CreateCsv(IReadOnlyCollection<ItemCatalogItem> items)
    {
        if (items.Count == 0 || items.Any(item => item.Id <= 0 || string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Type) || string.IsNullOrWhiteSpace(item.Rarity) || item.Level < 0) || items.Select(item => item.Id).Distinct().Count() != items.Count) throw new ItemCachePublishException();
        var csv = new StringBuilder("id,name,type,rarity,level\r\n");
        foreach (var item in items.OrderBy(item => item.Id)) csv.Append(item.Id.ToString(CultureInfo.InvariantCulture)).Append(',').Append(Escape(item.Name)).Append(',').Append(Escape(item.Type)).Append(',').Append(Escape(item.Rarity)).Append(',').Append(item.Level.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        return Utf8.GetBytes(csv.ToString());
    }

    private static string Escape(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    private static void Write(Stream stream, IncrementalHash hash, string value) { var bytes = Utf8.GetBytes(value); stream.Write(bytes); hash.AppendData(bytes); }
    private static string TempPath(string directory, string prefix) => Path.Combine(directory, $"{prefix}.{Guid.NewGuid():N}.tmp");
    private static void WriteAllBytesFlushed(string path, byte[] bytes) { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough); stream.Write(bytes); stream.Flush(flushToDisk: true); }
    private static byte[] HashFile(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return SHA256.HashData(stream); }

    private static void Cleanup(string directory, string manifestPath)
    {
        string? referenced = null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("csvFileName", out var csv) && csv.ValueKind == JsonValueKind.String && IsGenerationName(csv.GetString()!)) referenced = csv.GetString();
        }
        catch (Exception) { }
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (CsvTempName.IsMatch(name) || ManifestTempName.IsMatch(name)) { TryDelete(file); continue; }
            if (referenced is not null && IsGenerationName(name) && !string.Equals(name, referenced, StringComparison.Ordinal)) TryDelete(file);
        }
    }

    private static bool IsGenerationName(string name) => GenerationName.IsMatch(name);
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}

public interface IUpdaterLeaseFactory { IDisposable Acquire(); }
public sealed class UpdaterLeaseFactory(string lockPath) : IUpdaterLeaseFactory { public IDisposable Acquire() => UpdaterLease.Acquire(lockPath); }
public sealed class UpdaterLease : IDisposable
{
    private readonly FileStream stream;
    private UpdaterLease(FileStream stream) => this.stream = stream;
    public static UpdaterLease Acquire(string lockPath)
    {
        try
        {
            var path = Path.GetFullPath(lockPath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new UpdaterLease(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Cannot acquire the updater lease. Stop MCP before refreshing the item cache.", exception);
        }
    }
    public void Dispose() => stream.Dispose();
}

public sealed class ItemRefreshCommand
{
    private readonly Func<IItemCacheRefreshService> serviceFactory;
    private readonly IUpdaterLeaseFactory leaseFactory;
    private readonly Action<ItemRefreshSummary>? report;
    private readonly Action<string>? error;
    public ItemRefreshCommand(IItemCacheRefreshService service, IUpdaterLeaseFactory leaseFactory) : this(() => service, leaseFactory) { }
    public ItemRefreshCommand(IItemCacheRefreshService service, IUpdaterLeaseFactory leaseFactory, Action<ItemRefreshSummary>? report, Action<string>? error) : this(() => service, leaseFactory, report, error) { }
    public ItemRefreshCommand(Func<IItemCacheRefreshService> serviceFactory, IUpdaterLeaseFactory leaseFactory, Action<ItemRefreshSummary>? report = null, Action<string>? error = null) { this.serviceFactory = serviceFactory; this.leaseFactory = leaseFactory; this.report = report; this.error = error; }
    public static string FormatSuccess(ItemRefreshSummary summary) => summary.IsTestCache
        ? $"Test item cache published. Named {summary.NamedItemCount.ToString(CultureInfo.InvariantCulture)} of {summary.SourceItemCount.ToString(CultureInfo.InvariantCulture)} items; excluded blank names {summary.ExcludedBlankNameCount.ToString(CultureInfo.InvariantCulture)}; attempts {summary.HttpAttemptCount.ToString(CultureInfo.InvariantCulture)}; generation {summary.CsvFileName}."
        : $"Production item cache published. Named {summary.NamedItemCount.ToString(CultureInfo.InvariantCulture)} of {summary.SourceItemCount.ToString(CultureInfo.InvariantCulture)} items; excluded blank names {summary.ExcludedBlankNameCount.ToString(CultureInfo.InvariantCulture)}; generation {summary.CsvFileName}.";
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryParseArguments(args, out var isTestCache, out var fresh, out var outputDirectory))
        {
            error?.Invoke("Invalid refresh arguments.");
            return 2;
        }
        try
        {
            IDisposable lease;
            try
            {
                lease = leaseFactory.Acquire();
            }
            catch (Exception)
            {
                error?.Invoke("Refresh lease unavailable.");
                return 1;
            }

            using (lease)
            {
                if (fresh) ItemCacheRefreshService.PrepareFreshOutput(outputDirectory);
                var service = serviceFactory();
                var summary = isTestCache
                    ? await service.RefreshTestAsync(outputDirectory, cancellationToken).ConfigureAwait(false)
                    : await service.RefreshAsync(outputDirectory, cancellationToken).ConfigureAwait(false);
                report?.Invoke(summary);
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { error?.Invoke("Item cache refresh cancelled."); return 1; }
        catch (ItemCachePublishException) { error?.Invoke("Item cache publication failed."); return 1; }
        catch (ItemCacheIncompleteException exception) { error?.Invoke($"Item cache download incomplete. Staged {exception.CompletedBatches.ToString(CultureInfo.InvariantCulture)} of {exception.TotalBatches.ToString(CultureInfo.InvariantCulture)} batches; rerun to resume."); error?.Invoke($"Unresolved batches by cause: timeout {exception.TimeoutCount.ToString(CultureInfo.InvariantCulture)}, transport {exception.TransportCount.ToString(CultureInfo.InvariantCulture)}, HTTP {exception.HttpStatusCount.ToString(CultureInfo.InvariantCulture)}, invalid response {exception.InvalidResponseCount.ToString(CultureInfo.InvariantCulture)}."); return 1; }
        catch (ItemStagingException exception) when (exception.FreshPreflight) { error?.Invoke("Item cache download failed."); error?.Invoke("No files were deleted. Resolve unrecognized staging entries before retrying."); return 1; }
        catch (ItemStagingException) { error?.Invoke("Item cache download failed."); error?.Invoke("Staged item data is incompatible. Rerun with --fresh."); return 1; }
        catch (ItemCatalogDownloadException) { error?.Invoke("Item cache download failed."); return 1; }
        catch (OperationCanceledException) { error?.Invoke("Item cache download failed."); return 1; }
        catch (Exception) { error?.Invoke("Item cache download failed."); return 1; }
    }

    private static bool TryParseArguments(string[] args, out bool isTestCache, out bool fresh, out string outputDirectory)
    {
        isTestCache = false;
        fresh = false;
        outputDirectory = string.Empty;
        if (args.Length is not (3 or 4) || args[1] != "--output" || string.IsNullOrWhiteSpace(args[2]) || args[2].StartsWith("-", StringComparison.Ordinal)) return false;
        if (args[0] == "items")
        {
            if (args.Length == 4 && args[3] != "--fresh") return false;
            try { outputDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[2])); fresh = args.Length == 4; return true; }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        }
        if (args[0] != "items-test" || args.Length != 3) return false;
        isTestCache = true;
        try
        {
            outputDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[2]));
            var root = Path.GetPathRoot(outputDirectory);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.IsNullOrEmpty(root) || string.Equals(outputDirectory, Path.TrimEndingDirectorySeparator(root), comparison) || !Path.GetFileName(outputDirectory).EndsWith("-test", comparison)) return false;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
