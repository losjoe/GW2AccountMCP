using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GW2AccountMCP.DataRefresh;

public sealed record PriceCatalogPrice(long Id, bool Whitelisted, long BuyQuantity, long BuyUnitPrice, long SellQuantity, long SellUnitPrice);
public interface IPriceCatalogDownloadClient
{
    int AttemptCount { get; }
    Task<IReadOnlyList<long>> GetRootIdsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<PriceCatalogPrice>> GetPricesAsync(IReadOnlyCollection<long> requestedIds, CancellationToken cancellationToken);
}

public enum PriceCatalogDownloadFailureKind { Timeout, Transport, HttpStatus, InvalidResponse }
public sealed class PriceCatalogDownloadException(PriceCatalogDownloadFailureKind kind = PriceCatalogDownloadFailureKind.InvalidResponse) : Exception("The Guild Wars 2 price catalog response is invalid.") { public PriceCatalogDownloadFailureKind Kind { get; } = kind; }

public sealed class PriceCatalogDownloadClient(HttpClient httpClient, TimeProvider? timeProvider = null, IApiStartGate? startGate = null, TimeSpan? attemptTimeout = null) : IPriceCatalogDownloadClient
{
    public static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromSeconds(15);
    private const int MaximumRootResponseBytes = 2 * 1024 * 1024;
    private const int MaximumBatchResponseBytes = 2 * 1024 * 1024;
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IApiStartGate startGate = startGate ?? new ApiStartGate(timeProvider ?? TimeProvider.System);
    private readonly TimeSpan attemptTimeout = attemptTimeout ?? DefaultAttemptTimeout;
    private int attemptCount;
    public int AttemptCount => Volatile.Read(ref attemptCount);

    public async Task<IReadOnlyList<long>> GetRootIdsAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync("/v2/commerce/prices", MaximumRootResponseBytes, allowPartialContent: false, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() is < 1 or > 100_000) throw new PriceCatalogDownloadException();
        var ids = new List<long>();
        var unique = new HashSet<long>();
        foreach (var value in document.RootElement.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var id) || id <= 0 || !unique.Add(id)) throw new PriceCatalogDownloadException();
            ids.Add(id);
        }
        return ids;
    }

    public async Task<IReadOnlyList<PriceCatalogPrice>> GetPricesAsync(IReadOnlyCollection<long> requestedIds, CancellationToken cancellationToken)
    {
        if (requestedIds.Count is < 1 or > 200 || requestedIds.Any(id => id <= 0) || requestedIds.Distinct().Count() != requestedIds.Count) throw new ArgumentException("Price batches must contain 1 through 200 unique positive IDs.", nameof(requestedIds));
        var ids = requestedIds.Order().ToArray();
        using var document = await GetJsonAsync("/v2/commerce/prices?ids=" + string.Join(',', ids), MaximumBatchResponseBytes, allowPartialContent: true, cancellationToken).ConfigureAwait(false);
        return ReadPrices(document.RootElement);
    }

    private async Task<JsonDocument> GetJsonAsync(string uri, int limit, bool allowPartialContent, CancellationToken cancellationToken)
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
                using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, attemptCancellation.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK || allowPartialContent && response.StatusCode == HttpStatusCode.PartialContent)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(attemptCancellation.Token).ConfigureAwait(false);
                    try { return JsonDocument.Parse(await ReadBoundedAsync(stream, limit, attemptCancellation.Token).ConfigureAwait(false)); }
                    catch (JsonException) { throw new PriceCatalogDownloadException(); }
                }
                if (retry == 0 && IsTransient(response.StatusCode))
                {
                    if (GetRetryAfter(response.Headers.RetryAfter) is { } delay) await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw new PriceCatalogDownloadException(PriceCatalogDownloadFailureKind.HttpStatus);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (attemptCancellation.IsCancellationRequested && retry == 0) { }
            catch (OperationCanceledException) when (attemptCancellation.IsCancellationRequested) { throw new PriceCatalogDownloadException(PriceCatalogDownloadFailureKind.Timeout); }
            catch (HttpRequestException) when (retry == 0) { }
            catch (HttpRequestException) { throw new PriceCatalogDownloadException(PriceCatalogDownloadFailureKind.Transport); }
            catch (PriceCatalogDownloadException) { throw; }
            catch (Exception) { throw new PriceCatalogDownloadException(); }
        }
        throw new PriceCatalogDownloadException(PriceCatalogDownloadFailureKind.Timeout);
    }

    private static IReadOnlyList<PriceCatalogPrice> ReadPrices(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new PriceCatalogDownloadException();
        var prices = new List<PriceCatalogPrice>();
        foreach (var value in root.EnumerateArray())
        {
            if (!TryReadPrice(value, out var price)) throw new PriceCatalogDownloadException();
            prices.Add(price);
        }
        return prices;
    }

    internal static bool IsValid(PriceCatalogPrice price) => price.Id > 0 && price.BuyQuantity >= 0 && price.BuyUnitPrice >= 0 && price.SellQuantity >= 0 && price.SellUnitPrice >= 0 &&
        ((price.BuyQuantity == 0) == (price.BuyUnitPrice == 0)) && ((price.SellQuantity == 0) == (price.SellUnitPrice == 0));

    internal static bool TryReadPrice(JsonElement value, out PriceCatalogPrice price)
    {
        price = default!;
        if (!UniqueProperties(value) || !value.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var idValue) || idValue <= 0 ||
            !value.TryGetProperty("whitelisted", out var white) || white.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !TrySide(value, "buys", out var buyQuantity, out var buyUnitPrice) || !TrySide(value, "sells", out var sellQuantity, out var sellUnitPrice)) return false;
        price = new PriceCatalogPrice(idValue, white.GetBoolean(), buyQuantity, buyUnitPrice, sellQuantity, sellUnitPrice);
        return IsValid(price);
    }

    private static bool TrySide(JsonElement parent, string name, out long quantity, out long unitPrice)
    {
        quantity = unitPrice = 0;
        return parent.TryGetProperty(name, out var side) && UniqueProperties(side) && side.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number && q.TryGetInt64(out quantity) &&
            side.TryGetProperty("unit_price", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out unitPrice);
    }
    private TimeSpan? GetRetryAfter(RetryConditionHeaderValue? retryAfter) => retryAfter?.Delta is { } delta && delta >= TimeSpan.Zero ? delta : retryAfter?.Date is { } date && date >= timeProvider.GetUtcNow() ? date - timeProvider.GetUtcNow() : null;
    private static bool IsTransient(HttpStatusCode status) => status is (HttpStatusCode)429 or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        await using var result = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return result.ToArray();
            if (result.Length > maximumBytes - read) throw new PriceCatalogDownloadException();
            await result.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }
    private static bool UniqueProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!names.Add(property.Name)) return false;
        return true;
    }
}

public sealed record PriceRefreshSummary(int PriceCount, int HttpAttemptCount, string CsvFileName, string CsvSha256, bool IsTestCache = false);
public sealed class PriceStagingException(bool freshPreflight) : Exception { public bool FreshPreflight { get; } = freshPreflight; }
public sealed class PriceStagingCleanupException : Exception { }
public sealed class PriceClockException : Exception { }
public sealed class PriceCacheIncompleteException(int completedBatches, int totalBatches) : Exception { public int CompletedBatches { get; } = completedBatches; public int TotalBatches { get; } = totalBatches; }
public sealed class PriceCachePublishException : Exception { public PriceCachePublishException() : base("The price cache could not be published.") { } }
public interface IPriceCacheRefreshService { Task<PriceRefreshSummary> RefreshAsync(string outputDirectory, bool fresh, CancellationToken cancellationToken); Task<PriceRefreshSummary> RefreshTestAsync(string outputDirectory, CancellationToken cancellationToken); }

public sealed class PriceCacheRefreshService(IPriceCatalogDownloadClient client, PriceCachePublisher publisher, TimeProvider? timeProvider = null) : IPriceCacheRefreshService
{
    private const int BatchSize = 200, MaxRows = 100_000, MaxBatches = 500, StateVersion = 1, RootLimit = 2 * 1024 * 1024, StateLimit = 1024 * 1024, ShardLimit = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex RootName = new("^prices\\.root\\.([0-9a-f]{64})\\.json$", RegexOptions.CultureInvariant), ShardName = new("^prices\\.batch\\.([0-9]+)\\.([0-9a-f]{64})\\.json$", RegexOptions.CultureInvariant), TempName = new("^prices\\.(?:root|state|batch)\\.[0-9a-f]{32}\\.tmp$", RegexOptions.CultureInvariant);
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<PriceRefreshSummary> RefreshAsync(string outputDirectory, bool fresh, CancellationToken cancellationToken)
    {
        var output = Path.GetFullPath(outputDirectory);
        ValidateExistingPathComponents(output);
        if (fresh) PrepareFreshOutput(output);
        var stage = Path.Combine(output, ".prices-staging");
        if (File.Exists(stage)) throw new PriceStagingException(false);
        if (Directory.Exists(stage))
        {
            var state = ReadState(stage);
            var roots = ReadRoots(stage, state.RootSha256, state.RootCount);
            var shards = ValidateShards(stage, state, roots);
            if (state.Status == "publishing") return RecoverPublication(output, stage, state, roots, shards, cancellationToken);
            return await ContinueAsync(output, stage, state, roots, shards, cancellationToken).ConfigureAwait(false);
        }

        var sourceStarted = UtcNow();
        var rootsNew = (await client.GetRootIdsAsync(cancellationToken).ConfigureAwait(false)).Order().ToArray();
        ValidateRootIds(rootsNew);
        var rootHash = HashRoot(rootsNew);
        Directory.CreateDirectory(stage);
        WriteRoots(stage, rootHash, rootsNew);
        var newState = new ResumeState(StateVersion, BatchSize, rootsNew.Length, rootHash, sourceStarted, "downloading");
        WriteState(stage, newState);
        return await ContinueAsync(output, stage, newState, rootsNew, [], cancellationToken).ConfigureAwait(false);
    }

    public async Task<PriceRefreshSummary> RefreshTestAsync(string outputDirectory, CancellationToken cancellationToken)
    {
        var output = ValidateTestOutput(outputDirectory);
        var sourceStarted = UtcNow();
        var roots = (await client.GetRootIdsAsync(cancellationToken).ConfigureAwait(false)).Order().ToArray();
        ValidateRootIds(roots);
        if (roots.Length < BatchSize) throw new PriceCatalogDownloadException();
        var selected = roots.Take(BatchSize).ToArray();
        var prices = (await client.GetPricesAsync(selected, cancellationToken).ConfigureAwait(false)).OrderBy(price => price.Id).ToArray();
        ValidateExact(prices, selected);
        var sourceCompleted = UtcNow();
        cancellationToken.ThrowIfCancellationRequested();
        var artifact = publisher.CreateGeneration(output, prices, cancellationToken);
        var generated = UtcNow();
        ValidateTimestampOrder(sourceStarted, sourceCompleted, generated);
        publisher.PublishManifest(output, publisher.CreateManifestBytes(artifact, sourceStarted, sourceCompleted, generated, "test", false), cancellationToken);
        return new PriceRefreshSummary(artifact.RowCount, client.AttemptCount, artifact.CsvFileName, artifact.CsvSha256, true);
    }

    public static void PrepareFreshOutput(string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        ValidateExistingPathComponents(output);
        var stage = Path.Combine(output, ".prices-staging");
        ValidateExistingPathComponents(stage);
        if (File.Exists(stage)) throw new PriceStagingException(true);
        if (!Directory.Exists(stage)) return;
        ValidateStageLeaves(stage, true);
        try { foreach (var file in Directory.EnumerateFiles(stage)) File.Delete(file); Directory.Delete(stage); }
        catch { throw new PriceStagingCleanupException(); }
    }

    public static string ValidateTestOutput(string outputDirectory)
    {
        try
        {
            var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
            var root = Path.GetPathRoot(output);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.IsNullOrEmpty(root) || string.Equals(output, Path.TrimEndingDirectorySeparator(root), comparison) || !Path.GetFileName(output).EndsWith("-test", comparison)) throw new ArgumentException();
            ValidateExistingPathComponents(output);
            EnsureTestPublicationIsolated(output);
            return output;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { throw new ArgumentException("Invalid test output.", nameof(outputDirectory)); }
    }

    private static void EnsureTestPublicationIsolated(string output)
    {
        var manifest = Path.Combine(output, "prices.manifest.json");
        if (Directory.Exists(manifest) || File.Exists(manifest) && (File.GetAttributes(manifest) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new PriceCachePublishException();
        if (!File.Exists(manifest)) return;
        try
        {
            if ((File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0) throw new IOException();
            using var document = JsonDocument.Parse(ReadFile(manifest, 64 * 1024));
            if (Properties(document.RootElement, "formatVersion", "scope", "isComplete", "sourceStartedAtUtc", "sourceCompletedAtUtc", "cacheGeneratedAtUtc", "rowCount", "csvByteLength", "csvFileName", "csvSha256") &&
                document.RootElement.GetProperty("formatVersion").GetInt32() == StateVersion && document.RootElement.GetProperty("scope").GetString() == "test" && !document.RootElement.GetProperty("isComplete").GetBoolean() &&
                CanonicalTime(document.RootElement.GetProperty("sourceStartedAtUtc").GetString()) && CanonicalTime(document.RootElement.GetProperty("sourceCompletedAtUtc").GetString()) && CanonicalTime(document.RootElement.GetProperty("cacheGeneratedAtUtc").GetString()) &&
                document.RootElement.GetProperty("rowCount").GetInt32() is > 0 and <= BatchSize && document.RootElement.GetProperty("csvByteLength").GetInt64() is > 0 and <= 16 * 1024 * 1024 &&
                HashLike(document.RootElement.GetProperty("csvSha256").GetString()) && document.RootElement.GetProperty("csvFileName").GetString() == $"prices.{document.RootElement.GetProperty("csvSha256").GetString()}.csv") return;
        }
        catch { }
        throw new PriceCachePublishException();
    }

    private async Task<PriceRefreshSummary> ContinueAsync(string output, string stage, ResumeState state, long[] roots, Dictionary<int, string> completed, CancellationToken cancellationToken)
    {
        var batchCount = BatchCount(roots.Length);
        if (state.Status == "downloading") _ = await DownloadMissingAsync(stage, state, roots, completed, cancellationToken).ConfigureAwait(false);
        completed = ValidateShards(stage, state, roots);
        cancellationToken.ThrowIfCancellationRequested();
        if (completed.Count != batchCount) throw new PriceCacheIncompleteException(completed.Count, batchCount);
        if (state.Status == "downloading")
        {
            var sourceCompleted = UtcNow();
            ValidateTimestampOrder(state.SourceStartedAtUtc, sourceCompleted, null);
            state = state with { Status = "collected", SourceCompletedAtUtc = sourceCompleted };
            WriteState(stage, state);
        }
        if (state.Status != "collected") throw new PriceStagingException(false);
        var artifact = publisher.CreateGeneration(output, EnumerateShards(completed, roots), cancellationToken);
        var generated = UtcNow();
        ValidateTimestampOrder(state.SourceStartedAtUtc, state.SourceCompletedAtUtc!, generated);
        var manifest = publisher.CreateManifestBytes(artifact, state.SourceStartedAtUtc, state.SourceCompletedAtUtc!, generated, "production", true);
        var publishing = state with { Status = "publishing", CacheGeneratedAtUtc = generated, CsvFileName = artifact.CsvFileName, CsvSha256 = artifact.CsvSha256, PublishedRowCount = artifact.RowCount, CsvByteLength = artifact.CsvByteLength, ManifestSha256 = Hash(manifest) };
        WriteState(stage, publishing);
        publisher.PublishManifest(output, manifest, cancellationToken);
        return new PriceRefreshSummary(artifact.RowCount, client.AttemptCount, artifact.CsvFileName, artifact.CsvSha256);
    }

    private PriceRefreshSummary RecoverPublication(string output, string stage, ResumeState state, long[] roots, Dictionary<int, string> completed, CancellationToken cancellationToken)
    {
        if (completed.Count != BatchCount(roots.Length)) throw new PriceStagingException(false);
        var artifact = publisher.CreateGeneration(output, EnumerateShards(completed, roots), cancellationToken);
        var manifest = publisher.CreateManifestBytes(artifact, state.SourceStartedAtUtc, state.SourceCompletedAtUtc!, state.CacheGeneratedAtUtc!, "production", true);
        if (artifact.RowCount != state.PublishedRowCount || artifact.CsvByteLength != state.CsvByteLength || artifact.CsvFileName != state.CsvFileName || artifact.CsvSha256 != state.CsvSha256 || Hash(manifest) != state.ManifestSha256) throw new PriceStagingException(false);
        var manifestPath = Path.Combine(output, "prices.manifest.json");
        var matches = false;
        try { matches = File.Exists(manifestPath) && ReadFile(manifestPath, 64 * 1024).AsSpan().SequenceEqual(manifest); } catch { }
        if (!matches) publisher.PublishManifest(output, manifest, cancellationToken);
        return new PriceRefreshSummary(artifact.RowCount, client.AttemptCount, artifact.CsvFileName, artifact.CsvSha256);
    }

    private async Task<HashSet<int>> DownloadMissingAsync(string stage, ResumeState state, long[] roots, Dictionary<int, string> completed, CancellationToken cancellationToken)
    {
        var active = new Dictionary<Task<IReadOnlyList<PriceCatalogPrice>>, int>();
        var failures = new HashSet<int>();
        var next = 0; Exception? terminal = null;
        void Schedule()
        {
            while (!cancellationToken.IsCancellationRequested && terminal is null && active.Count < 4 && next < BatchCount(roots.Length))
            {
                var index = next++; if (completed.ContainsKey(index)) continue;
                try { active.Add(client.GetPricesAsync(Slice(roots, index), cancellationToken), index); }
                catch (PriceCatalogDownloadException) { failures.Add(index); }
                catch (Exception ex) { terminal = ex; }
            }
        }
        Schedule();
        while (active.Count > 0)
        {
            var task = await Task.WhenAny(active.Keys).ConfigureAwait(false); var index = active[task]; active.Remove(task);
            try { completed[index] = WriteShard(stage, state, index, Slice(roots, index), await task.ConfigureAwait(false)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (PriceCatalogDownloadException) { failures.Add(index); }
            catch (Exception ex) { terminal ??= ex; }
            Schedule();
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (terminal is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(terminal).Throw();
        return failures;
    }

    private static Dictionary<int, string> ValidateShards(string stage, ResumeState state, long[] roots)
    {
        ValidateStageLeaves(stage, false);
        var result = new Dictionary<int, string>(); var count = BatchCount(roots.Length);
        foreach (var path in Directory.EnumerateFiles(stage, "prices.batch.*.json"))
        {
            var match = ShardName.Match(Path.GetFileName(path));
            if (!match.Success || !int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= count || !result.TryAdd(index, path)) throw new PriceStagingException(false);
            ReadShard(path, index, Slice(roots, index), state.RootSha256);
        }
        return result;
    }
    private static IEnumerable<PriceCatalogPrice> EnumerateShards(Dictionary<int, string> completed, long[] roots)
    {
        foreach (var pair in completed.OrderBy(pair => pair.Key)) foreach (var price in ReadShard(pair.Value, pair.Key, Slice(roots, pair.Key), null)) yield return price;
    }
    private static string WriteShard(string stage, ResumeState state, int index, long[] expected, IReadOnlyList<PriceCatalogPrice> prices)
    {
        ValidateExact(prices.OrderBy(price => price.Id).ToArray(), expected);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ResumeShard(StateVersion, index, state.RootSha256, prices.OrderBy(price => price.Id).ToArray()), JsonOptions);
        if (bytes.Length > ShardLimit) throw new PriceCatalogDownloadException();
        var hash = Hash(bytes); var destination = Path.Combine(stage, $"prices.batch.{index}.{hash}.json");
        if (File.Exists(destination)) { ReadShard(destination, index, expected, state.RootSha256); return destination; }
        AtomicWrite(Path.Combine(stage, $"prices.batch.{Guid.NewGuid():N}.tmp"), destination, bytes); return destination;
    }
    private static PriceCatalogPrice[] ReadShard(string path, int index, long[] expected, string? rootHash)
    {
        try
        {
            var bytes = ReadFile(path, ShardLimit); var match = ShardName.Match(Path.GetFileName(path));
            if (!match.Success || Hash(bytes) != match.Groups[2].Value) throw new PriceStagingException(false);
            using var document = JsonDocument.Parse(bytes);
            if (!Properties(document.RootElement, "formatVersion", "batchIndex", "rootSha256", "prices")) throw new PriceStagingException(false);
            var shard = JsonSerializer.Deserialize<ResumeShard>(bytes, JsonOptions) ?? throw new PriceStagingException(false);
            if (shard.FormatVersion != StateVersion || shard.BatchIndex != index || !HashLike(shard.RootSha256) || rootHash is not null && shard.RootSha256 != rootHash || shard.Prices is null ||
                document.RootElement.GetProperty("prices").EnumerateArray().Any(price => !Properties(price, "id", "whitelisted", "buyQuantity", "buyUnitPrice", "sellQuantity", "sellUnitPrice")) || !StrictAscending(shard.Prices.Select(price => price.Id))) throw new PriceStagingException(false);
            ValidateExact(shard.Prices, expected); return shard.Prices;
        }
        catch (PriceStagingException) { throw; }
        catch { throw new PriceStagingException(false); }
    }
    private static ResumeState ReadState(string stage)
    {
        ValidateStageLeaves(stage, false);
        try
        {
            var bytes = ReadFile(Path.Combine(stage, "prices.resume-state.json"), StateLimit); using var document = JsonDocument.Parse(bytes);
            var state = JsonSerializer.Deserialize<ResumeState>(bytes, JsonOptions) ?? throw new JsonException();
            var expected = state.Status switch
            {
                "downloading" => new[] { "formatVersion", "batchSize", "rootCount", "rootSha256", "sourceStartedAtUtc", "status" },
                "collected" => new[] { "formatVersion", "batchSize", "rootCount", "rootSha256", "sourceStartedAtUtc", "status", "sourceCompletedAtUtc" },
                "publishing" => new[] { "formatVersion", "batchSize", "rootCount", "rootSha256", "sourceStartedAtUtc", "status", "sourceCompletedAtUtc", "cacheGeneratedAtUtc", "csvFileName", "csvSha256", "publishedRowCount", "csvByteLength", "manifestSha256" },
                _ => []
            };
            if (!Properties(document.RootElement, expected)) throw new JsonException(); ValidateState(state); return state;
        }
        catch (PriceStagingException) { throw; } catch { throw new PriceStagingException(false); }
    }
    private static long[] ReadRoots(string stage, string rootHash, int rootCount)
    {
        try
        {
            var path = Path.Combine(stage, $"prices.root.{rootHash}.json"); var bytes = ReadFile(path, RootLimit); using var document = JsonDocument.Parse(bytes);
            if (!Properties(document.RootElement, "formatVersion", "ids")) throw new JsonException();
            var root = JsonSerializer.Deserialize<ResumeRoot>(bytes, JsonOptions) ?? throw new JsonException();
            if (root.FormatVersion != StateVersion || root.Ids is null || root.Ids.Length != rootCount || !StrictAscending(root.Ids) || HashRoot(root.Ids) != rootHash) throw new JsonException(); return root.Ids;
        }
        catch { throw new PriceStagingException(false); }
    }
    private static void WriteRoots(string stage, string hash, long[] roots)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ResumeRoot(StateVersion, roots), JsonOptions); if (bytes.Length > RootLimit) throw new PriceCatalogDownloadException();
        AtomicWrite(Path.Combine(stage, $"prices.root.{Guid.NewGuid():N}.tmp"), Path.Combine(stage, $"prices.root.{hash}.json"), bytes);
    }
    private static void WriteState(string stage, ResumeState state) => AtomicWrite(Path.Combine(stage, $"prices.state.{Guid.NewGuid():N}.tmp"), Path.Combine(stage, "prices.resume-state.json"), JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions));
    private static void ValidateState(ResumeState state)
    {
        if (state.FormatVersion != StateVersion || state.BatchSize != BatchSize || state.RootCount is < 1 or > MaxRows || BatchCount(state.RootCount) > MaxBatches || !HashLike(state.RootSha256) || !CanonicalTime(state.SourceStartedAtUtc) || state.Status is not ("downloading" or "collected" or "publishing")) throw new PriceStagingException(false);
        if ((state.Status is "collected" or "publishing") && (!CanonicalTime(state.SourceCompletedAtUtc) || DateTimeOffset.Parse(state.SourceStartedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) > DateTimeOffset.Parse(state.SourceCompletedAtUtc!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))) throw new PriceStagingException(false);
        if (state.Status == "publishing" && (!CanonicalTime(state.CacheGeneratedAtUtc) || DateTimeOffset.Parse(state.SourceCompletedAtUtc!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) > DateTimeOffset.Parse(state.CacheGeneratedAtUtc!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) || !HashLike(state.CsvSha256) || !HashLike(state.ManifestSha256) || state.CsvFileName != $"prices.{state.CsvSha256}.csv" || state.PublishedRowCount != state.RootCount || state.CsvByteLength is <= 0 or > 16 * 1024 * 1024)) throw new PriceStagingException(false);
    }
    private static void ValidateExact(IReadOnlyList<PriceCatalogPrice> prices, long[] expected)
    {
        if (expected.Length is < 1 or > MaxRows || BatchCount(expected.Length) > MaxBatches || prices.Count != expected.Length || prices.Any(price => !PriceCatalogDownloadClient.IsValid(price)) || !prices.Select(price => price.Id).Order().SequenceEqual(expected)) throw new PriceCatalogDownloadException();
        if (prices.Select(price => price.Id).Distinct().Count() != prices.Count) throw new PriceCatalogDownloadException();
    }
    private static void ValidateRootIds(long[] roots)
    {
        if (roots.Length is < 1 or > MaxRows || !StrictAscending(roots) || BatchCount(roots.Length) > MaxBatches) throw new PriceCatalogDownloadException();
    }
    private static int BatchCount(int length) => (length + BatchSize - 1) / BatchSize;
    private static long[] Slice(long[] roots, int index) => roots.Skip(index * BatchSize).Take(Math.Min(BatchSize, roots.Length - index * BatchSize)).ToArray();
    private static bool StrictAscending(IEnumerable<long> ids) { var prior = 0L; foreach (var id in ids) { if (id <= prior) return false; prior = id; } return true; }
    private static string HashRoot(long[] ids) => Hash(Utf8.GetBytes(string.Join('\n', ids) + "\n"));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool HashLike(string? value) => value is not null && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static bool CanonicalTime(string? value) { try { return value is not null && value == DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).UtcDateTime.ToString("O"); } catch { return false; } }
    private string UtcNow() => timeProvider.GetUtcNow().UtcDateTime.ToString("O");
    private static bool Properties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) return false; var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!names.Add(property.Name)) return false;
        return names.SetEquals(expected);
    }
    private static void ValidateExistingPathComponents(string fullPath)
    {
        try
        {
            var root = Path.GetPathRoot(fullPath) ?? throw new IOException();
            var current = Path.TrimEndingDirectorySeparator(root);
            if (current.Length == 0) current = root;
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new PriceStagingException(true);
            var relative = Path.GetRelativePath(root, fullPath);
            if (relative == ".") return;
            var components = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                current = Path.Combine(current, component);
                if (!File.Exists(current) && !Directory.Exists(current)) break;
                if (!Directory.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new PriceStagingException(true);
            }
        }
        catch (PriceStagingException) { throw; }
        catch { throw new PriceStagingException(true); }
    }
    private static void ValidateStageLeaves(string stage, bool fresh)
    {
        try
        {
            if ((File.GetAttributes(stage) & FileAttributes.ReparsePoint) != 0) throw new PriceStagingException(fresh);
            foreach (var path in Directory.EnumerateFileSystemEntries(stage))
            {
                var attributes = File.GetAttributes(path); var name = Path.GetFileName(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || (name != "prices.resume-state.json" && !RootName.IsMatch(name) && !ShardName.IsMatch(name) && !TempName.IsMatch(name))) throw new PriceStagingException(fresh);
            }
        }
        catch (PriceStagingException) { throw; } catch { throw new PriceStagingException(fresh); }
    }
    private static byte[] ReadFile(string path, int maximum) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); if (stream.Length > maximum) throw new IOException(); return PriceCatalogDownloadClient.ReadBoundedAsync(stream, maximum, CancellationToken.None).GetAwaiter().GetResult(); }
    private static void ValidateTimestampOrder(string started, string completed, string? generated)
    {
        if (!CanonicalTime(started) || !CanonicalTime(completed)) throw new PriceClockException();
        var start = DateTimeOffset.Parse(started, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var finish = DateTimeOffset.Parse(completed, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (start > finish) throw new PriceClockException();
        if (generated is not null)
        {
            if (!CanonicalTime(generated) || finish > DateTimeOffset.Parse(generated, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)) throw new PriceClockException();
        }
    }
    private static void AtomicWrite(string temp, string destination, byte[] bytes) { WriteFlushed(temp, bytes); if (File.Exists(destination)) File.Replace(temp, destination, null); else File.Move(temp, destination); }
    internal static void WriteFlushed(string path, byte[] bytes) { using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough); stream.Write(bytes); stream.Flush(true); }
    private sealed record ResumeState(int FormatVersion, int BatchSize, int RootCount, string RootSha256, string SourceStartedAtUtc, string Status, string? SourceCompletedAtUtc = null, string? CacheGeneratedAtUtc = null, string? CsvFileName = null, string? CsvSha256 = null, int? PublishedRowCount = null, long? CsvByteLength = null, string? ManifestSha256 = null);
    private sealed record ResumeRoot(int FormatVersion, long[] Ids);
    private sealed record ResumeShard(int FormatVersion, int BatchIndex, string RootSha256, PriceCatalogPrice[] Prices);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
}

public sealed record PriceCacheArtifact(string CsvFileName, string CsvSha256, int RowCount, long CsvByteLength);
public sealed class PriceCachePublisher
{
    private const int CsvLimit = 16 * 1024 * 1024, ManifestLimit = 64 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Regex Generation = new("^prices\\.([0-9a-f]{64})\\.csv$", RegexOptions.CultureInvariant), Temp = new("^prices(?:\\.manifest)?\\.[0-9a-f]{32}\\.tmp$", RegexOptions.CultureInvariant);
    private readonly Action? beforeManifestReplacement;
    private readonly Action? beforeGeneration;
    public PriceCachePublisher(TimeProvider timeProvider, Action? beforeManifestReplacement = null, Action? beforeGeneration = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.beforeManifestReplacement = beforeManifestReplacement;
        this.beforeGeneration = beforeGeneration;
    }
    public PriceCacheArtifact CreateGeneration(string outputDirectory, IEnumerable<PriceCatalogPrice> prices, CancellationToken cancellationToken)
    {
        var temporary = string.Empty;
        try
        {
            beforeGeneration?.Invoke();
            Directory.CreateDirectory(outputDirectory); temporary = Path.Combine(outputDirectory, $"prices.{Guid.NewGuid():N}.tmp");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var count = 0; long prior = 0;
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                Write(stream, hash, "id,whitelisted,buyQuantity,buyUnitPrice,sellQuantity,sellUnitPrice\r\n");
                foreach (var price in prices)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!PriceCatalogDownloadClient.IsValid(price) || price.Id <= prior || ++count > 100_000) throw new PriceCachePublishException(); prior = price.Id;
                    Write(stream, hash, string.Concat(price.Id.ToString(CultureInfo.InvariantCulture), ",", price.Whitelisted ? "true" : "false", ",", price.BuyQuantity.ToString(CultureInfo.InvariantCulture), ",", price.BuyUnitPrice.ToString(CultureInfo.InvariantCulture), ",", price.SellQuantity.ToString(CultureInfo.InvariantCulture), ",", price.SellUnitPrice.ToString(CultureInfo.InvariantCulture), "\r\n"));
                    if (stream.Length > CsvLimit) throw new PriceCachePublishException();
                }
                if (count == 0) throw new PriceCachePublishException(); stream.Flush(true);
            }
            var length = new FileInfo(temporary).Length;
            if (length is <= 0 or > CsvLimit) throw new PriceCachePublishException();
            var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(); var artifact = new PriceCacheArtifact($"prices.{sha}.csv", sha, count, length); var generation = Path.Combine(outputDirectory, artifact.CsvFileName);
            if (File.Exists(generation))
            {
                if (IsReparsePoint(generation)) throw new PriceCachePublishException();
                var matchingGeneration = false;
                try { var existing = ReadBoundedFile(generation, CsvLimit); matchingGeneration = existing.Length == length && Convert.ToHexString(SHA256.HashData(existing)).ToLowerInvariant() == sha; } catch (IOException) { }
                if (matchingGeneration) File.Delete(temporary); else File.Replace(temporary, generation, null);
            }
            else File.Move(temporary, generation);
            temporary = string.Empty; return artifact;
        }
        catch (OperationCanceledException) { throw; } catch (PriceCachePublishException) { throw; } catch { throw new PriceCachePublishException(); }
        finally { if (temporary.Length != 0) try { File.Delete(temporary); } catch { } }
    }
    public byte[] CreateManifestBytes(PriceCacheArtifact artifact, string started, string completed, string generated, string scope, bool complete) => Utf8.GetBytes($"{{\"formatVersion\":1,\"scope\":\"{scope}\",\"isComplete\":{(complete ? "true" : "false")},\"sourceStartedAtUtc\":\"{started}\",\"sourceCompletedAtUtc\":\"{completed}\",\"cacheGeneratedAtUtc\":\"{generated}\",\"rowCount\":{artifact.RowCount.ToString(CultureInfo.InvariantCulture)},\"csvByteLength\":{artifact.CsvByteLength.ToString(CultureInfo.InvariantCulture)},\"csvFileName\":\"{artifact.CsvFileName}\",\"csvSha256\":\"{artifact.CsvSha256}\"}}");
    public void PublishManifest(string outputDirectory, byte[] manifest, CancellationToken cancellationToken)
    {
        var temporary = string.Empty;
        try
        {
            if (manifest.Length > ManifestLimit) throw new PriceCachePublishException(); Directory.CreateDirectory(outputDirectory);
            temporary = Path.Combine(outputDirectory, $"prices.manifest.{Guid.NewGuid():N}.tmp"); WriteFlushed(temporary, manifest); cancellationToken.ThrowIfCancellationRequested(); beforeManifestReplacement?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(outputDirectory, "prices.manifest.json"); if (File.Exists(destination) && IsReparsePoint(destination)) throw new PriceCachePublishException(); cancellationToken.ThrowIfCancellationRequested(); if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination); temporary = string.Empty; Cleanup(outputDirectory);
        }
        catch (OperationCanceledException) { throw; } catch (PriceCachePublishException) { throw; } catch { throw new PriceCachePublishException(); }
        finally { if (temporary.Length != 0) try { File.Delete(temporary); } catch { } }
    }
    private static void Write(Stream stream, IncrementalHash hash, string text) { var bytes = Utf8.GetBytes(text); stream.Write(bytes); hash.AppendData(bytes); }
    private static void WriteFlushed(string path, byte[] bytes) => PriceCacheRefreshService.WriteFlushed(path, bytes);
    private static void Cleanup(string directory)
    {
        string? referenced = null;
        try { using var document = JsonDocument.Parse(ReadBoundedFile(Path.Combine(directory, "prices.manifest.json"), ManifestLimit)); if (document.RootElement.TryGetProperty("csvFileName", out var name) && name.ValueKind == JsonValueKind.String && Generation.IsMatch(name.GetString()!)) referenced = name.GetString(); } catch { return; }
        foreach (var file in Directory.EnumerateFiles(directory)) { var name = Path.GetFileName(file); if (!IsReparsePoint(file) && (Temp.IsMatch(name) || Generation.IsMatch(name) && name != referenced)) try { File.Delete(file); } catch { } }
    }
    private static byte[] ReadBoundedFile(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new IOException();
        return PriceCatalogDownloadClient.ReadBoundedAsync(stream, maximum, CancellationToken.None).GetAwaiter().GetResult();
    }
    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}

public sealed class PriceRefreshCommand
{
    private readonly Func<IPriceCacheRefreshService> factory; private readonly IUpdaterLeaseFactory leaseFactory; private readonly Action<PriceRefreshSummary>? report; private readonly Action<string>? error;
    public PriceRefreshCommand(Func<IPriceCacheRefreshService> factory, IUpdaterLeaseFactory leaseFactory, Action<PriceRefreshSummary>? report = null, Action<string>? error = null) { this.factory = factory; this.leaseFactory = leaseFactory; this.report = report; this.error = error; }
    public static string FormatSuccess(PriceRefreshSummary summary) => summary.IsTestCache ? $"Test price cache published. Rows {summary.PriceCount.ToString(CultureInfo.InvariantCulture)}; attempts {summary.HttpAttemptCount.ToString(CultureInfo.InvariantCulture)}; generation {summary.CsvFileName}." : $"Production price cache published. Rows {summary.PriceCount.ToString(CultureInfo.InvariantCulture)}; generation {summary.CsvFileName}.";
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryParse(args, out var test, out var fresh, out var output)) { error?.Invoke("Invalid refresh arguments."); return 2; }
        try
        {
            IDisposable lease; try { lease = leaseFactory.Acquire(); } catch { error?.Invoke("Refresh lease unavailable."); return 1; }
            using (lease) { var service = factory(); var result = test ? await service.RefreshTestAsync(output, cancellationToken).ConfigureAwait(false) : await service.RefreshAsync(output, fresh, cancellationToken).ConfigureAwait(false); report?.Invoke(result); }
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { error?.Invoke("Price cache refresh cancelled."); return 1; }
        catch (PriceCachePublishException) { error?.Invoke("Price cache publication failed."); return 1; }
        catch (PriceClockException) { error?.Invoke("Price cache refresh failed because the system clock moved backwards. Correct the system clock and rerun the same command without --fresh."); return 1; }
        catch (PriceCacheIncompleteException ex) { error?.Invoke($"Price cache download incomplete. Staged {ex.CompletedBatches.ToString(CultureInfo.InvariantCulture)} of {ex.TotalBatches.ToString(CultureInfo.InvariantCulture)} batches; rerun to resume."); return 1; }
        catch (PriceStagingCleanupException) { error?.Invoke("Price cache fresh cleanup failed; staging may be partially deleted. Published generation was not deleted."); return 1; }
        catch (PriceStagingException ex) when (ex.FreshPreflight) { error?.Invoke("Price cache download failed."); error?.Invoke("No files were deleted. Resolve unrecognized staging entries before retrying."); return 1; }
        catch (PriceStagingException) { error?.Invoke("Price cache download failed."); error?.Invoke("Staged price data is incompatible. Rerun with --fresh."); return 1; }
        catch { error?.Invoke("Price cache download failed."); return 1; }
    }
    private static bool TryParse(string[] args, out bool test, out bool fresh, out string output)
    {
        test = fresh = false; output = string.Empty;
        if (args.Length is not (3 or 4) || args[1] != "--output" || string.IsNullOrWhiteSpace(args[2]) || args[2].StartsWith("-", StringComparison.Ordinal)) return false;
        try { output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[2])); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        if (args[0] == "tp") { if (args.Length == 4 && args[3] != "--fresh") return false; fresh = args.Length == 4; return true; }
        if (args[0] != "tp-test" || args.Length != 3) return false;
        try { output = PriceCacheRefreshService.ValidateTestOutput(output); test = true; return true; } catch { return false; }
    }
}
