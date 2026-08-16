using GW2AccountMCP.Prices;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class PriceSnapshotProviderTests
{
    [Fact]
    public async Task Reloads_when_every_call_observes_a_new_manifest_fingerprint()
    {
        var reader = new MutableReader(Snapshot("a"));
        var provider = new PriceSnapshotProvider(reader, new Lifetime());

        var first = await provider.GetSnapshotAsync(CancellationToken.None);
        reader.Snapshot = Snapshot("b");
        reader.Fingerprint = reader.Snapshot.Fingerprint;
        var second = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal("a", first.Snapshot.Fingerprint.ManifestSha256[..1]);
        Assert.Equal("b", second.Snapshot.Fingerprint.ManifestSha256[..1]);
        Assert.Equal(2, reader.LoadCalls);
    }

    [Fact]
    public async Task Retains_old_snapshot_with_warning_when_new_adoption_fails()
    {
        var reader = new MutableReader(Snapshot("a"));
        var provider = new PriceSnapshotProvider(reader, new Lifetime());
        var initial = await provider.GetSnapshotAsync(CancellationToken.None);
        reader.Fingerprint = Fingerprint("b");
        reader.FailLoad = true;

        var result = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Same(initial.Snapshot, result.Snapshot);
        Assert.NotNull(result.AdoptionWarning);
    }

    [Fact]
    public async Task Adopts_timestamp_only_manifest_change_for_the_same_csv_generation()
    {
        var original = Snapshot("a");
        var reader = new MutableReader(original);
        var provider = new PriceSnapshotProvider(reader, new Lifetime());
        await provider.GetSnapshotAsync(CancellationToken.None);
        var timestampOnly = original with { Fingerprint = original.Fingerprint with { ManifestSha256 = "b".PadRight(64, 'b') }, SourceCompletedAtUtc = original.SourceCompletedAtUtc.AddMinutes(1) };
        reader.Snapshot = timestampOnly;
        reader.Fingerprint = timestampOnly.Fingerprint;

        var adopted = await provider.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(original.Fingerprint.CsvFileName, adopted.Snapshot.Fingerprint.CsvFileName);
        Assert.Equal(original.Prices, adopted.Snapshot.Prices);
        Assert.Equal(timestampOnly.SourceCompletedAtUtc, adopted.Snapshot.SourceCompletedAtUtc);
        Assert.Equal(2, reader.LoadCalls);
    }

    [Fact]
    public async Task Shares_one_reload_and_caller_cancellation_does_not_cancel_it()
    {
        var reader = new MutableReader(Snapshot("a"));
        var provider = new PriceSnapshotProvider(reader, new Lifetime());
        await provider.GetSnapshotAsync(CancellationToken.None);
        reader.Snapshot = Snapshot("b");
        reader.Fingerprint = reader.Snapshot.Fingerprint;
        reader.BlockLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelled = new CancellationTokenSource();
        var cancelledCall = provider.GetSnapshotAsync(cancelled.Token);
        var survivingCall = provider.GetSnapshotAsync(CancellationToken.None);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledCall);
        reader.BlockLoad.SetResult();

        Assert.Equal("b", (await survivingCall).Snapshot.Fingerprint.ManifestSha256[..1]);
        Assert.Equal(2, reader.LoadCalls);
    }

    [Fact]
    public async Task Initial_cache_unavailability_is_not_represented_as_a_snapshot()
    {
        var reader = new MutableReader(Snapshot("a")) { FailFingerprint = true };
        var provider = new PriceSnapshotProvider(reader, new Lifetime());

        await Assert.ThrowsAsync<PriceCacheException>(() => provider.GetSnapshotAsync(CancellationToken.None));
    }

    private static PriceCacheSnapshot Snapshot(string marker) => new([new CachedPrice(marker == "a" ? 1 : 2, true, 1, 2, 3, 4)], Fingerprint(marker), DateTime.UnixEpoch, DateTime.UnixEpoch, DateTime.UnixEpoch);
    private static PriceCacheFingerprint Fingerprint(string marker) => new(marker.PadRight(64, marker[0]), "prices." + marker.PadRight(64, marker[0]) + ".csv", DateTime.UnixEpoch, 1, DateTime.UnixEpoch, 1);
    private sealed class MutableReader : IPriceCacheReader
    {
        public MutableReader(PriceCacheSnapshot snapshot) { Snapshot = snapshot; Fingerprint = snapshot.Fingerprint; }
        public PriceCacheSnapshot Snapshot { get; set; }
        public PriceCacheFingerprint Fingerprint { get; set; }
        public bool FailLoad { get; set; }
        public bool FailFingerprint { get; set; }
        public TaskCompletionSource? BlockLoad { get; set; }
        public int LoadCalls { get; private set; }
        public PriceCacheFingerprint GetCurrentFingerprint() { if (FailFingerprint) throw new PriceCacheException("The price cache is unavailable."); return Fingerprint; }
        public PriceCacheSnapshot Load(CancellationToken cancellationToken) { LoadCalls++; BlockLoad?.Task.Wait(cancellationToken); if (FailLoad) throw new PriceCacheException("The price cache is unavailable."); Fingerprint = Snapshot.Fingerprint; return Snapshot; }
    }
    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
