using GW2AccountMCP.Items;
using Microsoft.Extensions.Hosting;

namespace GW2AccountMCP.Prices;

public sealed record PriceSnapshotResult(PriceCacheSnapshot Snapshot, string? AdoptionWarning);
public interface IPriceSnapshotProvider { Task<PriceSnapshotResult> GetSnapshotAsync(CancellationToken cancellationToken); }

public sealed class PriceSnapshotProvider(IPriceCacheReader reader, IHostApplicationLifetime applicationLifetime) : IPriceSnapshotProvider
{
    private const string AdoptionWarning = "A newer local price cache could not be adopted; this response uses the prior validated manual-cached snapshot.";
    private readonly object stateLock = new();
    private PriceCacheSnapshot? current;
    private Task<PriceCacheSnapshot>? loadTask;

    public async Task<PriceSnapshotResult> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PriceCacheSnapshot? existing;
        lock (stateLock) existing = current;
        PriceCacheFingerprint fingerprint;
        try { fingerprint = reader.GetCurrentFingerprint(); }
        catch (PriceCacheException) when (existing is not null) { return new PriceSnapshotResult(existing, AdoptionWarning); }
        if (existing is not null && existing.Fingerprint == fingerprint) return new PriceSnapshotResult(existing, null);
        var task = GetOrStartLoad(existing);
        try { return new PriceSnapshotResult(await task.WaitAsync(cancellationToken).ConfigureAwait(false), null); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PriceCacheException) when (existing is not null) { return new PriceSnapshotResult(existing, AdoptionWarning); }
    }

    private Task<PriceCacheSnapshot> GetOrStartLoad(PriceCacheSnapshot? observed)
    {
        lock (stateLock)
        {
            if (current is not null && !ReferenceEquals(current, observed)) return Task.FromResult(current);
            if (loadTask is null || loadTask.IsCompleted) loadTask = Task.Run(() => LoadAndPublish(applicationLifetime.ApplicationStopping), applicationLifetime.ApplicationStopping);
            return loadTask;
        }
    }

    private PriceCacheSnapshot LoadAndPublish(CancellationToken stoppingToken)
    {
        var loaded = reader.Load(stoppingToken);
        lock (stateLock) current = loaded;
        return loaded;
    }
}

public interface IItemNameLookup { Task<IReadOnlyDictionary<long, string>> GetNamesAsync(CancellationToken cancellationToken); }

public sealed class ItemNameLookup(IItemCacheReader reader, IHostApplicationLifetime applicationLifetime) : IItemNameLookup
{
    private readonly object stateLock = new();
    private IReadOnlyDictionary<long, string>? names;
    private Task<IReadOnlyDictionary<long, string>>? loadTask;
    public Task<IReadOnlyDictionary<long, string>> GetNamesAsync(CancellationToken cancellationToken)
    {
        Task<IReadOnlyDictionary<long, string>> task;
        lock (stateLock)
        {
            if (names is not null) return Task.FromResult(names);
            if (loadTask is null || loadTask.IsCompleted) loadTask = Task.Run(() => Load(applicationLifetime.ApplicationStopping), applicationLifetime.ApplicationStopping);
            task = loadTask;
        }
        return task.WaitAsync(cancellationToken);
    }
    private IReadOnlyDictionary<long, string> Load(CancellationToken cancellationToken)
    {
        var result = reader.Load(cancellationToken).Items.ToDictionary(item => item.Id, item => item.Name);
        lock (stateLock) names = result;
        return result;
    }
}
