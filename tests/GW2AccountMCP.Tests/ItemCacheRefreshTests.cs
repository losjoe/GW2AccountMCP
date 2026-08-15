using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using GW2AccountMCP.DataRefresh;
using GW2AccountMCP.Items;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class ItemCacheRefreshTests
{
    [Fact]
    public async Task Download_client_validates_root_and_sends_no_authentication()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, "[2,1]"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://fake.test") };
        var client = new ItemCatalogDownloadClient(http, TimeProvider.System, new ImmediateStartGate());

        var ids = await client.GetRootIdsAsync(CancellationToken.None);

        Assert.Equal([1L, 2L], ids.Order());
        Assert.All(handler.Requests, request => Assert.Null(request.Headers.Authorization));
        Assert.Equal(1, client.AttemptCount);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[1,\"2\"]")]
    [InlineData("[0]")]
    public async Task Download_client_rejects_malformed_root_without_response_content(string body)
    {
        using var http = new HttpClient(new ScriptedHandler(_ => Json(HttpStatusCode.OK, body))) { BaseAddress = new Uri("https://fake.test") };
        var error = await Assert.ThrowsAsync<ItemCatalogDownloadException>(() => new ItemCatalogDownloadClient(http).GetRootIdsAsync(CancellationToken.None));

        Assert.Equal("The Guild Wars 2 item catalog response is invalid.", error.Message);
        Assert.Equal(ItemCatalogDownloadFailureKind.InvalidResponse, error.Kind);
        Assert.DoesNotContain(body, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[1,1]")]
    public async Task Download_client_rejects_empty_or_duplicate_root_without_response_content(string body)
    {
        using var http = new HttpClient(new ScriptedHandler(_ => Json(HttpStatusCode.OK, body))) { BaseAddress = new Uri("https://fake.test") };

        var error = await Assert.ThrowsAsync<ItemCatalogDownloadException>(() => new ItemCatalogDownloadClient(http).GetRootIdsAsync(CancellationToken.None));

        Assert.Equal("The Guild Wars 2 item catalog response is invalid.", error.Message);
        Assert.Equal(ItemCatalogDownloadFailureKind.InvalidResponse, error.Kind);
        Assert.DoesNotContain(body, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(206)]
    [InlineData(500)]
    public async Task Download_client_rejects_non_ok_root_without_response_content(int statusCode)
    {
        const string body = "root response secret";
        using var http = new HttpClient(new ScriptedHandler(_ => Json((HttpStatusCode)statusCode, body))) { BaseAddress = new Uri("https://fake.test") };

        var error = await Assert.ThrowsAsync<ItemCatalogDownloadException>(() => new ItemCatalogDownloadClient(http).GetRootIdsAsync(CancellationToken.None));

        Assert.Equal("The Guild Wars 2 item catalog response is invalid.", error.Message);
        Assert.Equal(ItemCatalogDownloadFailureKind.HttpStatus, error.Kind);
        Assert.DoesNotContain(body, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Download_client_requires_an_exact_valid_batch_set()
    {
        var handler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, "[{\"id\":2,\"name\":\"B\",\"type\":\"Armor\",\"rarity\":\"Rare\",\"level\":0},{\"id\":1,\"name\":\"A\",\"type\":\"Weapon\",\"rarity\":\"Exotic\",\"level\":80}]"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://fake.test") };
        var client = new ItemCatalogDownloadClient(http, TimeProvider.System, new ImmediateStartGate());

        var items = await client.GetDefinitionsAsync([1, 2], CancellationToken.None);

        Assert.Equal([1L, 2L], items.Select(item => item.Id).Order());
        Assert.Equal("/v2/items?ids=1,2&lang=en&v=2025-08-29T01%3A00%3A00.000Z", handler.Requests.Single().RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task Download_client_accepts_blank_name_source_rows_but_rejects_non_string_names()
    {
        using var validHttp = new HttpClient(new ScriptedHandler(_ => Json(HttpStatusCode.OK, "[{\"id\":1,\"name\":\" \",\"type\":\"Weapon\",\"rarity\":\"Rare\",\"level\":0}]"))) { BaseAddress = new Uri("https://fake.test") };
        var valid = await new ItemCatalogDownloadClient(validHttp, startGate: new ImmediateStartGate()).GetDefinitionsAsync([1], CancellationToken.None);
        Assert.Equal(" ", valid.Single().Name);

        using var invalidHttp = new HttpClient(new ScriptedHandler(_ => Json(HttpStatusCode.OK, "[{\"id\":1,\"name\":null,\"type\":\"Weapon\",\"rarity\":\"Rare\",\"level\":0}]"))) { BaseAddress = new Uri("https://fake.test") };
        var error = await Assert.ThrowsAsync<ItemCatalogDownloadException>(() => new ItemCatalogDownloadClient(invalidHttp, startGate: new ImmediateStartGate()).GetDefinitionsAsync([1], CancellationToken.None));
        Assert.Equal(ItemCatalogDownloadFailureKind.InvalidResponse, error.Kind);
    }

    [Theory]
    [InlineData("[{\"id\":1,\"name\":\"A\",\"type\":\"Weapon\",\"rarity\":\"Rare\",\"level\":0}]")]
    [InlineData("[{\"id\":1,\"name\":\"A\",\"type\":\"Weapon\",\"rarity\":\"Rare\",\"level\":0},{\"id\":1,\"name\":\"B\",\"type\":\"Armor\",\"rarity\":\"Rare\",\"level\":0}]")]
    [InlineData("[{\"id\":1,\"name\":\" \",\"type\":\"\",\"rarity\":\"Rare\",\"level\":0},{\"id\":2,\"name\":\"B\",\"type\":\"Armor\",\"rarity\":\"Rare\",\"level\":0}]")]
    public async Task Download_client_rejects_non_exact_or_invalid_batches(string body)
    {
        using var http = new HttpClient(new ScriptedHandler(_ => Json(HttpStatusCode.OK, body))) { BaseAddress = new Uri("https://fake.test") };

        await Assert.ThrowsAsync<ItemCatalogDownloadException>(() => new ItemCatalogDownloadClient(http).GetDefinitionsAsync([1, 2], CancellationToken.None));
    }

    [Fact]
    public async Task Download_client_rejects_partial_content_batches()
    {
        using var http = new HttpClient(new ScriptedHandler(_ => Json(HttpStatusCode.PartialContent, "[]"))) { BaseAddress = new Uri("https://fake.test") };

        await Assert.ThrowsAsync<ItemCatalogDownloadException>(() => new ItemCatalogDownloadClient(http).GetDefinitionsAsync([1], CancellationToken.None));
    }

    [Fact]
    public async Task Transient_retry_honors_retry_after_and_gates_the_second_start()
    {
        var time = new ManualTimeProvider();
        var gate = new RecordingGate(time);
        var calls = 0;
        using var http = new HttpClient(new ScriptedHandler(_ => ++calls == 1
            ? new HttpResponseMessage((HttpStatusCode)429) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1)) } }
            : Json(HttpStatusCode.OK, "[1]"))) { BaseAddress = new Uri("https://fake.test") };
        var client = new ItemCatalogDownloadClient(http, time, gate);
        var download = client.GetRootIdsAsync(CancellationToken.None);
        await Task.Yield();

        Assert.Single(gate.Starts);
        time.Advance(TimeSpan.FromSeconds(1));
        await download;
        Assert.Equal(2, gate.Starts.Count);
        Assert.Equal(2, client.AttemptCount);
    }

    [Fact]
    public async Task Start_gate_limits_starts_to_four_per_second()
    {
        var time = new ManualTimeProvider();
        var gate = new ApiStartGate(time);
        await gate.WaitAsync(CancellationToken.None);
        var second = gate.WaitAsync(CancellationToken.None);
        await Task.Yield();
        Assert.False(second.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(250));
        await second;
    }

    [Fact]
    public async Task Refresh_fetches_once_partitions_and_publishes_deterministic_snapshot()
    {
        using var fixture = DirectoryFixture.Create();
        var client = new FakeCatalogClient([3, 1, 2]);
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 34, 56, TimeSpan.Zero));

        var summary = await new ItemCacheRefreshService(client, new ItemCachePublisher(time)).RefreshAsync(fixture.Path, CancellationToken.None);

        Assert.Equal(3, summary.ItemCount);
        Assert.Equal(1, client.RootCalls);
        Assert.Equal("id,name,type,rarity,level\r\n1,Name 1,Weapon,Rare,80\r\n2,Name 2,Weapon,Rare,80\r\n3,Name 3,Weapon,Rare,80\r\n", File.ReadAllText(Path.Combine(fixture.Path, summary.CsvFileName), new UTF8Encoding(false)));
    }

    [Fact]
    public async Task Production_stages_blank_source_names_but_publishes_only_named_rows()
    {
        using var fixture = DirectoryFixture.Create();
        var items = new[] { new ItemCatalogItem(1, " ", "Weapon", "Rare", 80), new ItemCatalogItem(2, "Named", "Armor", "Exotic", 0) };
        var refresh = new ItemCacheRefreshService(new StaticItemCatalogClient(items), new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch)));

        var summary = await refresh.RefreshAsync(fixture.Path, CancellationToken.None);

        Assert.Equal(2, summary.SourceItemCount);
        Assert.Equal(1, summary.NamedItemCount);
        Assert.Equal(1, summary.ExcludedBlankNameCount);
        Assert.Equal("id,name,type,rarity,level\r\n2,Named,Armor,Exotic,0\r\n", File.ReadAllText(Path.Combine(fixture.Path, summary.CsvFileName), new UTF8Encoding(false)));
        Assert.Single(new ItemCacheReader(new ItemCacheOptions(fixture.Path)).Load(CancellationToken.None).Items);
        Assert.Equal("Production item cache published. Named 1 of 2 items; excluded blank names 1; generation " + summary.CsvFileName + ".", ItemRefreshCommand.FormatSuccess(summary));
    }

    [Fact]
    public async Task Refresh_limits_large_definition_batches_to_four_concurrent_operations()
    {
        using var fixture = DirectoryFixture.Create();
        var client = new BoundedCatalogClient(Enumerable.Range(1, 1_000).Select(id => (long)id));
        var refresh = new ItemCacheRefreshService(client, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch)));

        var run = refresh.RefreshAsync(fixture.Path, CancellationToken.None);
        await client.FirstWaveStarted;
        Assert.Equal(4, client.MaximumActiveCalls);
        client.ReleaseFirstWave();
        await run;

        Assert.Equal(1, client.RootCalls);
        Assert.All(client.Batches, batch => Assert.InRange(batch.Length, 1, 200));
        Assert.Equal(Enumerable.Range(1, 1_000).Select(id => (long)id), client.Batches.SelectMany(batch => batch).Order());
        Assert.True(client.MaximumActiveCalls <= 4);
    }

    [Fact]
    public async Task Production_refresh_resumes_committed_validated_shards_after_a_batch_failure()
    {
        using var fixture = DirectoryFixture.Create();
        var first = new ResumeCatalogClient(Enumerable.Range(1, 400).Select(id => (long)id), failingBatchIndex: 1);
        var refresh = new ItemCacheRefreshService(first, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch)));

        var incomplete = await Assert.ThrowsAsync<ItemCacheIncompleteException>(() => refresh.RefreshAsync(fixture.Path, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(fixture.Path, ".items-staging", "items.resume-state.json")));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Path, ".items-staging"), "items.batch.*.json"));
        Assert.Equal(1, incomplete.CompletedBatches);
        Assert.Equal(2, incomplete.TotalBatches);

        var resumed = new ResumeCatalogClient(Enumerable.Range(1, 400).Select(id => (long)id));
        var result = await new ItemCacheRefreshService(resumed, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None);

        Assert.Equal(400, result.ItemCount);
        Assert.Single(resumed.Batches);
        Assert.Equal(Enumerable.Range(201, 200).Select(id => (long)id), resumed.Batches.Single());
        Assert.True(Directory.Exists(Path.Combine(fixture.Path, ".items-staging")));
    }

    [Fact]
    public async Task Multiple_batch_failures_report_one_aggregate_without_durable_failure_markers()
    {
        using var fixture = DirectoryFixture.Create();
        var client = new MultiFailureCatalogClient(Enumerable.Range(1, 1_000).Select(id => (long)id), new HashSet<int> { 1, 3 });

        var incomplete = await Assert.ThrowsAsync<ItemCacheIncompleteException>(() => new ItemCacheRefreshService(client, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None));

        Assert.Equal(3, incomplete.CompletedBatches);
        Assert.Equal(5, incomplete.TotalBatches);
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Path, ".items-staging")).Select(Path.GetFileName), name => name!.Contains("failure", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "items.manifest.json")));
    }

    [Fact]
    public async Task Mixed_catalog_failure_kinds_are_aggregated_without_durable_details()
    {
        using var fixture = DirectoryFixture.Create();
        var client = new MixedFailureCatalogClient(Enumerable.Range(1, 800).Select(id => (long)id), new Dictionary<int, ItemCatalogDownloadFailureKind>
        {
            [0] = ItemCatalogDownloadFailureKind.Timeout,
            [1] = ItemCatalogDownloadFailureKind.Transport,
            [2] = ItemCatalogDownloadFailureKind.HttpStatus
        });

        var incomplete = await Assert.ThrowsAsync<ItemCacheIncompleteException>(() => new ItemCacheRefreshService(client, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None));

        Assert.Equal(1, incomplete.CompletedBatches);
        Assert.Equal(4, incomplete.TotalBatches);
        Assert.Equal(1, incomplete.TimeoutCount);
        Assert.Equal(1, incomplete.TransportCount);
        Assert.Equal(1, incomplete.HttpStatusCount);
        Assert.Equal(0, incomplete.InvalidResponseCount);
        Assert.Equal(incomplete.TotalBatches - incomplete.CompletedBatches, incomplete.TimeoutCount + incomplete.TransportCount + incomplete.HttpStatusCount + incomplete.InvalidResponseCount);
    }

    [Fact]
    public async Task Incomplete_command_reports_only_aggregate_batch_counts()
    {
        using var fixture = DirectoryFixture.Create();
        var errors = new List<string>();
        var client = new ResumeCatalogClient(Enumerable.Range(1, 400).Select(id => (long)id), failingBatchIndex: 1);
        var command = new ItemRefreshCommand(() => new ItemCacheRefreshService(client, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))), new CountingLeaseFactory(), report: null, error: errors.Add);

        Assert.Equal(1, await command.RunAsync(["items", "--output", fixture.Path], CancellationToken.None));

        Assert.Equal(["Item cache download incomplete. Staged 1 of 2 batches; rerun to resume.", "Unresolved batches by cause: timeout 0, transport 0, HTTP 0, invalid response 1."], errors);
    }

    [Fact]
    public async Task Successful_production_refresh_retains_snapshot_and_later_run_uses_zero_network()
    {
        using var fixture = DirectoryFixture.Create();
        var roots = Enumerable.Range(1, 400).Select(id => (long)id);
        await new ItemCacheRefreshService(new ResumeCatalogClient(roots), new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None);
        var later = new ResumeCatalogClient(roots);

        await new ItemCacheRefreshService(later, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(fixture.Path, ".items-staging")));
        Assert.Equal(0, later.RootCalls);
        Assert.Empty(later.Batches);
    }

    [Fact]
    public async Task Retained_publishing_state_repairs_a_corrupt_generation_without_network_or_manifest_churn()
    {
        using var fixture = DirectoryFixture.Create();
        var items = new[] { new ItemCatalogItem(1, "One", "Weapon", "Rare", 80), new ItemCatalogItem(2, "Two", "Armor", "Exotic", 0) };
        var first = await new ItemCacheRefreshService(new StaticItemCatalogClient(items), new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None);
        var manifest = File.ReadAllBytes(Path.Combine(fixture.Path, "items.manifest.json"));
        File.WriteAllText(Path.Combine(fixture.Path, first.CsvFileName), "corrupt");
        var repairClient = new StaticItemCatalogClient(items);

        var repaired = await new ItemCacheRefreshService(repairClient, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None);

        Assert.Equal(0, repairClient.RootCalls);
        Assert.Equal(first.CsvSha256, repaired.CsvSha256);
        Assert.Equal(first.CsvSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Path, first.CsvFileName)))).ToLowerInvariant());
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(fixture.Path, "items.manifest.json")));
        Assert.True(Directory.Exists(Path.Combine(fixture.Path, ".items-staging")));
    }

    [Fact]
    public async Task Definition_failures_continue_scheduling_and_leave_an_aggregate_incomplete_stage()
    {
        using var fixture = DirectoryFixture.Create();
        var client = new DrainingFailureCatalogClient(Enumerable.Range(1, 1_000).Select(id => (long)id));
        var run = new ItemCacheRefreshService(client, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None);
        await client.FirstWaveStarted;
        client.ReleaseStartedBatches();

        var incomplete = await Assert.ThrowsAsync<ItemCacheIncompleteException>(() => run);

        Assert.Equal(5, client.Batches.Count);
        Assert.True(client.MaximumActiveCalls <= 4);
        Assert.Equal(0, client.ActiveCalls);
        Assert.Equal(4, Directory.EnumerateFiles(Path.Combine(fixture.Path, ".items-staging"), "items.batch.*.json").Count());
        Assert.False(File.Exists(Path.Combine(fixture.Path, "items.manifest.json")));
        Assert.Equal(4, incomplete.CompletedBatches);
        Assert.Equal(5, incomplete.TotalBatches);
    }

    [Fact]
    public async Task Unexpected_batch_failure_stops_scheduling_but_drains_started_successes_without_aggregate()
    {
        using var fixture = DirectoryFixture.Create();
        var client = new DrainingFailureCatalogClient(Enumerable.Range(1, 1_000).Select(id => (long)id), unexpected: true);
        var run = new ItemCacheRefreshService(client, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None);
        await client.FirstWaveStarted;
        client.ReleaseStartedBatches();

        await Assert.ThrowsAsync<InvalidOperationException>(() => run);

        Assert.Equal(4, client.Batches.Count);
        Assert.True(client.MaximumActiveCalls <= 4);
        Assert.Equal(0, client.ActiveCalls);
        Assert.Equal(3, Directory.EnumerateFiles(Path.Combine(fixture.Path, ".items-staging"), "items.batch.*.json").Count());
        Assert.False(File.Exists(Path.Combine(fixture.Path, "items.manifest.json")));
    }

    [Fact]
    public async Task Caller_cancellation_takes_priority_over_a_terminal_batch_failure_during_drain()
    {
        using var fixture = DirectoryFixture.Create();
        using var cancellation = new CancellationTokenSource();
        var client = new DrainingFailureCatalogClient(Enumerable.Range(1, 1_000).Select(id => (long)id), unexpected: true);
        var run = new ItemCacheRefreshService(client, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, cancellation.Token);
        await client.FirstWaveStarted;
        cancellation.Cancel();
        client.ReleaseStartedBatches();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(4, client.Batches.Count);
        Assert.Equal(0, client.ActiveCalls);
        Assert.False(File.Exists(Path.Combine(fixture.Path, "items.manifest.json")));
    }

    [Theory]
    [InlineData(401, 0)]
    [InlineData(400, 1)]
    public async Task Production_refresh_refuses_staging_when_the_root_identity_changes(int count, int offset)
    {
        using var fixture = DirectoryFixture.Create();
        var failed = new ResumeCatalogClient(Enumerable.Range(1, 400).Select(id => (long)id), failingBatchIndex: 1);
        await Assert.ThrowsAsync<ItemCacheIncompleteException>(() => new ItemCacheRefreshService(failed, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None));

        var changed = new ResumeCatalogClient(Enumerable.Range(1 + offset, count).Select(id => (long)id));
        await Assert.ThrowsAsync<ItemStagingException>(() => new ItemCacheRefreshService(changed, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(fixture.Path, ".items-staging", "items.resume-state.json")));
        Assert.Equal(1, changed.RootCalls);
        Assert.Empty(changed.Batches);
    }

    [Fact]
    public async Task Production_refresh_refuses_a_corrupt_committed_shard_without_reusing_it()
    {
        using var fixture = DirectoryFixture.Create();
        var failed = new ResumeCatalogClient(Enumerable.Range(1, 400).Select(id => (long)id), failingBatchIndex: 1);
        await Assert.ThrowsAsync<ItemCacheIncompleteException>(() => new ItemCacheRefreshService(failed, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None));
        var shard = Directory.EnumerateFiles(Path.Combine(fixture.Path, ".items-staging"), "items.batch.*.json").Single();
        File.WriteAllText(shard, "corrupt");
        var retry = new ResumeCatalogClient(Enumerable.Range(1, 400).Select(id => (long)id));

        await Assert.ThrowsAsync<ItemStagingException>(() => new ItemCacheRefreshService(retry, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None));

        Assert.Equal(0, retry.RootCalls);
        Assert.Empty(retry.Batches);
    }

    [Fact]
    public async Task Existing_invalid_or_ambiguous_staging_fails_before_the_root_request()
    {
        using var fixture = DirectoryFixture.Create();
        var stage = Path.Combine(fixture.Path, ".items-staging");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "items.resume-state.json"), "{\"formatVersion\":1,\"formatVersion\":1,\"gw2SchemaVersion\":\"2025-08-29T01:00:00.000Z\",\"language\":\"en\",\"batchSize\":200,\"rootCount\":200,\"rootSha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"status\":\"downloading\"}");
        var client = new ResumeCatalogClient(Enumerable.Range(1, 200).Select(id => (long)id));

        await Assert.ThrowsAsync<ItemStagingException>(() => new ItemCacheRefreshService(client, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None));

        Assert.Equal(0, client.RootCalls);
        Assert.Empty(client.Batches);
    }

    [Fact]
    public async Task Unknown_staging_leaf_fails_before_the_root_request()
    {
        using var fixture = DirectoryFixture.Create();
        var stage = Path.Combine(fixture.Path, ".items-staging");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "unrecognized.txt"), "keep");
        var client = new ResumeCatalogClient(Enumerable.Range(1, 200).Select(id => (long)id));

        await Assert.ThrowsAsync<ItemStagingException>(() => new ItemCacheRefreshService(client, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None));

        Assert.Equal(0, client.RootCalls);
        Assert.Empty(client.Batches);
    }

    [Theory]
    [InlineData("\"batchIndex\":0,", "\"batchIndex\":0,\"batchIndex\":0,")]
    [InlineData("\"id\":1,", "\"id\":1,\"id\":1,")]
    public async Task Duplicate_shard_or_item_properties_are_rejected_before_network(string original, string duplicate)
    {
        using var fixture = DirectoryFixture.Create();
        var roots = Enumerable.Range(1, 400).Select(id => (long)id);
        await Assert.ThrowsAsync<ItemCacheIncompleteException>(() => new ItemCacheRefreshService(new ResumeCatalogClient(roots, failingBatchIndex: 1), new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None));
        var stage = Path.Combine(fixture.Path, ".items-staging");
        var shard = Directory.EnumerateFiles(stage, "items.batch.*.json").Single();
        var bytes = Encoding.UTF8.GetBytes(File.ReadAllText(shard).Replace(original, duplicate, StringComparison.Ordinal));
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var renamed = Path.Combine(stage, $"items.batch.0.{hash}.json");
        File.Delete(shard);
        File.WriteAllBytes(renamed, bytes);
        var client = new ResumeCatalogClient(roots);

        await Assert.ThrowsAsync<ItemStagingException>(() => new ItemCacheRefreshService(client, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None));

        Assert.Equal(0, client.RootCalls);
        Assert.Empty(client.Batches);
    }

    [Fact]
    public async Task Hash_linked_unsorted_shard_is_incompatible_before_definitions_and_at_command_boundary()
    {
        using var fixture = DirectoryFixture.Create();
        var roots = Enumerable.Range(1, 400).Select(id => (long)id);
        await Assert.ThrowsAsync<ItemCacheIncompleteException>(() => new ItemCacheRefreshService(new ResumeCatalogClient(roots, failingBatchIndex: 1), new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None));
        RewriteShard(Path.Combine(fixture.Path, ".items-staging"), bytes => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\"id\":1,", "\"id\":9999,", StringComparison.Ordinal).Replace("\"id\":2,", "\"id\":1,", StringComparison.Ordinal).Replace("\"id\":9999,", "\"id\":2,", StringComparison.Ordinal)));
        var client = new ResumeCatalogClient(roots);
        var errors = new List<string>();
        var command = new ItemRefreshCommand(() => new ItemCacheRefreshService(client, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))), new CountingLeaseFactory(), report: null, error: errors.Add);

        Assert.Equal(1, await command.RunAsync(["items", "--output", fixture.Path], CancellationToken.None));

        Assert.Equal(0, client.RootCalls);
        Assert.Empty(client.Batches);
        Assert.Equal(["Item cache download failed.", "Staged item data is incompatible. Rerun with --fresh."], errors);
    }

    [Fact]
    public async Task Hash_consistent_malformed_shard_json_is_incompatible_before_definitions()
    {
        using var fixture = DirectoryFixture.Create();
        var roots = Enumerable.Range(1, 400).Select(id => (long)id);
        await Assert.ThrowsAsync<ItemCacheIncompleteException>(() => new ItemCacheRefreshService(new ResumeCatalogClient(roots, failingBatchIndex: 1), new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None));
        RewriteShard(Path.Combine(fixture.Path, ".items-staging"), _ => Encoding.UTF8.GetBytes("{not-valid-json"));
        var client = new ResumeCatalogClient(roots);

        await Assert.ThrowsAsync<ItemStagingException>(() => new ItemCacheRefreshService(client, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None));

        Assert.Equal(0, client.RootCalls);
        Assert.Empty(client.Batches);
    }

    [Fact]
    public async Task Production_cancellation_preserves_committed_shards_without_publication()
    {
        using var fixture = DirectoryFixture.Create();
        using var cancellation = new CancellationTokenSource();
        var client = new CancellationCatalogClient(Enumerable.Range(1, 400).Select(id => (long)id));
        var run = new ItemCacheRefreshService(client, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, cancellation.Token);
        await client.BlockingBatchStarted;
        for (var attempt = 0; attempt < 50 && !Directory.EnumerateFiles(Path.Combine(fixture.Path, ".items-staging"), "items.batch.*.json").Any(); attempt++) await Task.Delay(5);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Path, ".items-staging"), "items.batch.*.json"));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "items.manifest.json")));
    }

    [Fact]
    public void Fresh_preflight_is_all_or_nothing_and_preserves_unrelated_output()
    {
        using var fixture = DirectoryFixture.Create();
        var stage = Path.Combine(fixture.Path, ".items-staging");
        Directory.CreateDirectory(stage);
        var owned = Path.Combine(stage, "items.resume-state.json");
        var unknown = Path.Combine(stage, "keep.txt");
        var output = Path.Combine(fixture.Path, "items-notes.tmp");
        File.WriteAllText(owned, "state");
        File.WriteAllText(unknown, "keep");
        File.WriteAllText(output, "keep");

        var error = Assert.Throws<ItemStagingException>(() => ItemCacheRefreshService.PrepareFreshOutput(fixture.Path));

        Assert.True(error.FreshPreflight);
        Assert.True(File.Exists(owned));
        Assert.True(File.Exists(unknown));
        Assert.True(File.Exists(output));
        File.Delete(unknown);
        ItemCacheRefreshService.PrepareFreshOutput(fixture.Path);
        Assert.False(Directory.Exists(stage));
    }

    [Fact]
    public async Task Fresh_command_refuses_unknown_staging_before_service_or_network()
    {
        using var fixture = DirectoryFixture.Create();
        var stage = Path.Combine(fixture.Path, ".items-staging");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "items.resume-state.json"), "state");
        File.WriteAllText(Path.Combine(stage, "unrecognized.txt"), "keep");
        var lease = new CountingLeaseFactory();
        var serviceFactoryCalls = 0;
        var errors = new List<string>();
        var command = new ItemRefreshCommand(() => { serviceFactoryCalls++; return new RecordingRefreshService(); }, lease, report: null, error: errors.Add);

        Assert.Equal(1, await command.RunAsync(["items", "--output", fixture.Path, "--fresh"], CancellationToken.None));

        Assert.Equal(1, lease.AcquireCalls);
        Assert.Equal(0, serviceFactoryCalls);
        Assert.Equal(["Item cache download failed.", "No files were deleted. Resolve unrecognized staging entries before retrying."], errors);
        Assert.True(File.Exists(Path.Combine(stage, "items.resume-state.json")));
    }

    [Fact]
    public async Task Publishing_recovery_finishes_or_recognizes_the_exact_manifest_without_network()
    {
        using var fixture = DirectoryFixture.Create();
        var roots = Enumerable.Range(1, 200).Select(id => (long)id);
        var interrupted = new ResumeCatalogClient(roots);
        var crashingPublisher = new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch), beforeManifestReplacement: () => throw new InvalidOperationException());

        await Assert.ThrowsAsync<ItemCachePublishException>(() => new ItemCacheRefreshService(interrupted, crashingPublisher).RefreshAsync(fixture.Path, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(fixture.Path, ".items-staging", "items.resume-state.json")));

        var recovery = new ResumeCatalogClient(roots);
        var result = await new ItemCacheRefreshService(recovery, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None);

        Assert.Equal(200, result.ItemCount);
        Assert.Equal(0, recovery.RootCalls);
        Assert.Empty(recovery.Batches);
        Assert.True(Directory.Exists(Path.Combine(fixture.Path, ".items-staging")));
        Assert.True(File.Exists(Path.Combine(fixture.Path, "items.manifest.json")));
    }

    [Fact]
    public async Task Publishing_recovery_replaces_a_different_manifest_after_the_swap_crash()
    {
        using var fixture = DirectoryFixture.Create();
        var roots = Enumerable.Range(1, 200).Select(id => (long)id);
        var crashingPublisher = new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch), afterManifestReplacement: () => throw new InvalidOperationException());
        await Assert.ThrowsAsync<ItemCachePublishException>(() => new ItemCacheRefreshService(new ResumeCatalogClient(roots), crashingPublisher).RefreshAsync(fixture.Path, CancellationToken.None));
        File.WriteAllText(Path.Combine(fixture.Path, "items.manifest.json"), "{\"formatVersion\":1,\"generatedAtUtc\":\"2000-01-01T00:00:00.0000000Z\"}");

        var recovery = new ResumeCatalogClient(roots);
        await new ItemCacheRefreshService(recovery, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None);

        Assert.Equal(0, recovery.RootCalls);
        Assert.Empty(recovery.Batches);
        Assert.Contains("\"rowCount\":200", File.ReadAllText(Path.Combine(fixture.Path, "items.manifest.json")));
        Assert.True(Directory.Exists(Path.Combine(fixture.Path, ".items-staging")));
    }

    [Fact]
    public async Task Publishing_recovery_after_the_exact_swap_preserves_manifest_without_network()
    {
        using var fixture = DirectoryFixture.Create();
        var roots = Enumerable.Range(1, 200).Select(id => (long)id);
        var crashing = new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch), afterManifestReplacement: () => throw new InvalidOperationException());
        await Assert.ThrowsAsync<ItemCachePublishException>(() => new ItemCacheRefreshService(new ResumeCatalogClient(roots), crashing).RefreshAsync(fixture.Path, CancellationToken.None));
        var expected = File.ReadAllBytes(Path.Combine(fixture.Path, "items.manifest.json"));
        var recovery = new ResumeCatalogClient(roots);

        await new ItemCacheRefreshService(recovery, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None);

        Assert.Equal(0, recovery.RootCalls);
        Assert.Empty(recovery.Batches);
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(fixture.Path, "items.manifest.json")));
    }

    [Fact]
    public async Task Publishing_recovery_reconstructs_the_named_row_count_from_blank_source_shards()
    {
        using var fixture = DirectoryFixture.Create();
        var items = new[] { new ItemCatalogItem(1, " ", "Weapon", "Rare", 80), new ItemCatalogItem(2, "Named", "Armor", "Exotic", 0) };
        var crashing = new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch), beforeManifestReplacement: () => throw new InvalidOperationException());
        await Assert.ThrowsAsync<ItemCachePublishException>(() => new ItemCacheRefreshService(new StaticItemCatalogClient(items), crashing).RefreshAsync(fixture.Path, CancellationToken.None));
        var recovery = new StaticItemCatalogClient(items);

        var summary = await new ItemCacheRefreshService(recovery, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None);

        Assert.Equal(0, recovery.RootCalls);
        Assert.Equal(1, summary.NamedItemCount);
        Assert.Equal(2, summary.SourceItemCount);
    }

    [Fact]
    public async Task Publishing_recovery_replaces_a_valid_older_manifest_for_the_same_generation_without_network()
    {
        using var fixture = DirectoryFixture.Create();
        var roots = Enumerable.Range(1, 200).Select(id => (long)id);
        var items = roots.Select(id => new ItemCatalogItem(id, $"Name {id}", "Weapon", "Rare", 80)).ToArray();
        var publishingTime = new FixedTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var crashing = new ItemCachePublisher(publishingTime, beforeManifestReplacement: () => throw new InvalidOperationException());
        await Assert.ThrowsAsync<ItemCachePublishException>(() => new ItemCacheRefreshService(new ResumeCatalogClient(roots), crashing).RefreshAsync(fixture.Path, CancellationToken.None));
        new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch)).Publish(fixture.Path, items);
        var oldManifest = File.ReadAllBytes(Path.Combine(fixture.Path, "items.manifest.json"));
        var recovery = new ResumeCatalogClient(roots);

        await new ItemCacheRefreshService(recovery, new ItemCachePublisher(publishingTime)).RefreshAsync(fixture.Path, CancellationToken.None);

        Assert.Equal(0, recovery.RootCalls);
        Assert.Empty(recovery.Batches);
        Assert.NotEqual(oldManifest, File.ReadAllBytes(Path.Combine(fixture.Path, "items.manifest.json")));
        Assert.Contains("2026-01-02T03:04:05.0000000Z", File.ReadAllText(Path.Combine(fixture.Path, "items.manifest.json")));
    }

    [Fact]
    public async Task Staged_streamed_csv_matches_collection_publisher_and_reader_for_escaped_unicode_fields()
    {
        using var staged = DirectoryFixture.Create();
        using var collection = DirectoryFixture.Create();
        var items = new[]
        {
            new ItemCatalogItem(1, "Comma, \"quote\"\r\nΩ", "Weapon", "Rare", 80),
            new ItemCatalogItem(2, "Plain", "Armor", "Exotic", 0)
        };
        var time = new FixedTimeProvider(DateTimeOffset.UnixEpoch);
        var stagedResult = await new ItemCacheRefreshService(new StaticItemCatalogClient(items), new ItemCachePublisher(time)).RefreshAsync(staged.Path, CancellationToken.None);
        var collectionResult = new ItemCachePublisher(time).Publish(collection.Path, items);

        Assert.Equal(collectionResult.CsvSha256, stagedResult.CsvSha256);
        Assert.Equal(File.ReadAllBytes(Path.Combine(collection.Path, collectionResult.CsvFileName)), File.ReadAllBytes(Path.Combine(staged.Path, stagedResult.CsvFileName)));
        var snapshot = new ItemCacheReader(new ItemCacheOptions(staged.Path)).Load(CancellationToken.None);
        Assert.Equal(items, snapshot.Items.Select(item => new ItemCatalogItem(item.Id, item.Name, item.Type, item.Rarity, item.Level)));
    }

    [Fact]
    public async Task Test_refresh_selects_only_the_first_200_sorted_ids_and_publishes_a_reader_compatible_cache()
    {
        using var fixture = DirectoryFixture.Create("cache-test");
        var client = new FakeCatalogClient(Enumerable.Range(1, 500).Reverse().Select(id => (long)id));
        var refresh = new ItemCacheRefreshService(client, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch)));

        var summary = await refresh.RefreshTestAsync(fixture.Path, CancellationToken.None);
        var snapshot = new ItemCacheReader(new ItemCacheOptions(fixture.Path)).Load(CancellationToken.None);

        Assert.Equal(200, summary.ItemCount);
        Assert.Equal(1, client.RootCalls);
        Assert.Equal([Enumerable.Range(1, 200).Select(id => (long)id).ToArray()], client.Batches);
        Assert.Equal(200, snapshot.Items.Count);
        Assert.Equal(Enumerable.Range(1, 200).Select(id => (long)id), snapshot.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task Test_refresh_filters_blank_names_and_never_creates_staging()
    {
        using var fixture = DirectoryFixture.Create("cache-test");
        var items = Enumerable.Range(1, 200).Select(id => new ItemCatalogItem(id, id == 1 ? "\t" : $"Name {id}", "Weapon", "Rare", 80)).ToArray();

        var summary = await new ItemCacheRefreshService(new StaticItemCatalogClient(items), new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshTestAsync(fixture.Path, CancellationToken.None);

        Assert.Equal(200, summary.SourceItemCount);
        Assert.Equal(199, summary.NamedItemCount);
        Assert.Equal(1, summary.ExcludedBlankNameCount);
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, ".items-staging")));
        Assert.Equal(199, new ItemCacheReader(new ItemCacheOptions(fixture.Path)).Load(CancellationToken.None).Items.Count);
        Assert.Equal("Test item cache published. Named 199 of 200 items; excluded blank names 1; attempts 1; generation " + summary.CsvFileName + ".", ItemRefreshCommand.FormatSuccess(summary));
    }

    [Fact]
    public async Task Test_command_normalizes_safe_test_output_before_service_or_lease_work()
    {
        using var fixture = DirectoryFixture.Create();
        var safeOutput = Path.Combine(fixture.Path, "nested", "..", "cache-test") + Path.DirectorySeparatorChar;
        var lease = new CountingLeaseFactory();
        var service = new RecordingRefreshService();
        var serviceFactoryCalls = 0;
        var command = new ItemRefreshCommand(() => { serviceFactoryCalls++; return service; }, lease);

        Assert.Equal(0, await command.RunAsync(["items-test", "--output", safeOutput], CancellationToken.None));

        Assert.Equal(1, lease.AcquireCalls);
        Assert.Equal(1, serviceFactoryCalls);
        Assert.Equal([Path.TrimEndingDirectorySeparator(Path.GetFullPath(safeOutput))], service.TestOutputs);
    }

    [Theory]
    [InlineData("not-a-test-cache")]
    [InlineData("\0")]
    public async Task Test_command_rejects_unsafe_outputs_before_service_or_lease_work(string output)
    {
        var lease = new CountingLeaseFactory();
        var service = new RecordingRefreshService();
        var errors = new List<string>();
        var serviceFactoryCalls = 0;
        var command = new ItemRefreshCommand(() => { serviceFactoryCalls++; return service; }, lease, report: null, error: errors.Add);

        Assert.Equal(2, await command.RunAsync(["items-test", "--output", output], CancellationToken.None));

        Assert.Equal(0, lease.AcquireCalls);
        Assert.Equal(0, serviceFactoryCalls);
        Assert.Empty(service.TestOutputs);
        Assert.Equal(["Invalid refresh arguments."], errors);
    }

    [Fact]
    public async Task Test_command_rejects_root_output_before_service_or_lease_work()
    {
        var lease = new CountingLeaseFactory();
        var service = new RecordingRefreshService();
        var serviceFactoryCalls = 0;
        var command = new ItemRefreshCommand(() => { serviceFactoryCalls++; return service; }, lease);

        Assert.Equal(2, await command.RunAsync(["items-test", "--output", Path.GetPathRoot(Directory.GetCurrentDirectory())!], CancellationToken.None));
        Assert.Equal(0, lease.AcquireCalls);
        Assert.Equal(0, serviceFactoryCalls);
        Assert.Empty(service.TestOutputs);
    }

    [Fact]
    public async Task Internal_body_read_timeout_retries_once_through_the_start_gate()
    {
        var gate = new CountingStartGate();
        var calls = 0;
        using var http = new HttpClient(new ScriptedHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new BlockingContent() }
            : Json(HttpStatusCode.OK, "[1]"))) { BaseAddress = new Uri("https://fake.test"), Timeout = Timeout.InfiniteTimeSpan };
        var client = new ItemCatalogDownloadClient(http, startGate: gate, attemptTimeout: TimeSpan.FromMilliseconds(25));

        var ids = await client.GetRootIdsAsync(CancellationToken.None);

        Assert.Equal([1L], ids);
        Assert.Equal(2, client.AttemptCount);
        Assert.Equal(2, gate.Calls);
    }

    [Fact]
    public async Task Transport_retry_reenters_the_start_gate_and_exhausted_timeout_is_a_download_failure()
    {
        var transportGate = new CountingStartGate();
        var transportCalls = 0;
        using var transportHttp = new HttpClient(new ScriptedHandler(_ => ++transportCalls == 1 ? throw new HttpRequestException("secret") : Json(HttpStatusCode.OK, "[1]"))) { BaseAddress = new Uri("https://fake.test"), Timeout = Timeout.InfiniteTimeSpan };
        var transport = new ItemCatalogDownloadClient(transportHttp, startGate: transportGate, attemptTimeout: TimeSpan.FromSeconds(1));

        await transport.GetRootIdsAsync(CancellationToken.None);
        Assert.Equal(2, transport.AttemptCount);
        Assert.Equal(2, transportGate.Calls);

        var timeoutGate = new CountingStartGate();
        using var timeoutHttp = new HttpClient(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BlockingContent() })) { BaseAddress = new Uri("https://fake.test"), Timeout = Timeout.InfiniteTimeSpan };
        var timeout = new ItemCatalogDownloadClient(timeoutHttp, startGate: timeoutGate, attemptTimeout: TimeSpan.FromMilliseconds(25));

        var timeoutError = await Assert.ThrowsAsync<ItemCatalogDownloadException>(() => timeout.GetRootIdsAsync(CancellationToken.None));
        Assert.Equal(2, timeout.AttemptCount);
        Assert.Equal(2, timeoutGate.Calls);
        Assert.Equal(ItemCatalogDownloadFailureKind.Timeout, timeoutError.Kind);
    }

    [Fact]
    public async Task Download_client_classifies_retry_exhaustion_without_exposing_transport_details()
    {
        var gate = new CountingStartGate();
        using var transportHttp = new HttpClient(new ScriptedHandler(_ => throw new HttpRequestException("secret"))) { BaseAddress = new Uri("https://fake.test"), Timeout = Timeout.InfiniteTimeSpan };
        var transport = new ItemCatalogDownloadClient(transportHttp, startGate: gate);
        var transportError = await Assert.ThrowsAsync<ItemCatalogDownloadException>(() => transport.GetRootIdsAsync(CancellationToken.None));
        Assert.Equal(ItemCatalogDownloadFailureKind.Transport, transportError.Kind);
        Assert.Equal(2, transport.AttemptCount);
        Assert.DoesNotContain("secret", transportError.Message, StringComparison.OrdinalIgnoreCase);

        using var statusHttp = new HttpClient(new ScriptedHandler(_ => Json(HttpStatusCode.ServiceUnavailable, "secret"))) { BaseAddress = new Uri("https://fake.test"), Timeout = Timeout.InfiniteTimeSpan };
        var status = new ItemCatalogDownloadClient(statusHttp, startGate: new ImmediateStartGate());
        var statusError = await Assert.ThrowsAsync<ItemCatalogDownloadException>(() => status.GetRootIdsAsync(CancellationToken.None));
        Assert.Equal(ItemCatalogDownloadFailureKind.HttpStatus, statusError.Kind);
        Assert.Equal(2, status.AttemptCount);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_retried()
    {
        var gate = new CountingStartGate();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var http = new HttpClient(new ScriptedHandler(_ => Json(HttpStatusCode.OK, "[1]"))) { BaseAddress = new Uri("https://fake.test"), Timeout = Timeout.InfiniteTimeSpan };
        var client = new ItemCatalogDownloadClient(http, startGate: gate, attemptTimeout: TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetRootIdsAsync(cancellation.Token));
        Assert.Equal(1, gate.Calls);
        Assert.Equal(0, client.AttemptCount);
    }

    [Fact]
    public void Publisher_uses_rfc4180_bytes_hash_and_exact_manifest()
    {
        using var fixture = DirectoryFixture.Create();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 34, 56, 7, TimeSpan.Zero));
        var result = new ItemCachePublisher(time).Publish(fixture.Path, [new ItemCatalogItem(2, "Line\r\nΩ, \"quoted\"", "Weapon", "Rare", 80)]);
        var bytes = File.ReadAllBytes(Path.Combine(fixture.Path, result.CsvFileName));
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        Assert.Equal("id,name,type,rarity,level\r\n2,\"Line\r\nΩ, \"\"quoted\"\"\",Weapon,Rare,80\r\n", new UTF8Encoding(false, true).GetString(bytes));
        Assert.Equal(hash, result.CsvSha256);
        Assert.Equal($"{{\"formatVersion\":1,\"generatedAtUtc\":\"2026-08-14T12:34:56.0070000Z\",\"gw2SchemaVersion\":\"2025-08-29T01:00:00.000Z\",\"language\":\"en\",\"rowCount\":1,\"csvFileName\":\"items.{hash}.csv\",\"csvSha256\":\"{hash}\"}}", File.ReadAllText(Path.Combine(fixture.Path, "items.manifest.json"), new UTF8Encoding(false)));
    }

    [Fact]
    public void Publisher_reuses_matching_generation_and_cleans_only_unreferenced_owned_files()
    {
        using var fixture = DirectoryFixture.Create();
        var publisher = new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        var first = publisher.Publish(fixture.Path, [new ItemCatalogItem(1, "A", "Weapon", "Rare", 0)]);
        var stale = Path.Combine(fixture.Path, "items." + new string('a', 64) + ".csv");
        File.WriteAllText(stale, "stale");
        File.WriteAllText(Path.Combine(fixture.Path, "other.txt"), "keep");
        var second = publisher.Publish(fixture.Path, [new ItemCatalogItem(1, "A", "Weapon", "Rare", 0)]);

        Assert.Equal(first.CsvFileName, second.CsvFileName);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(Path.Combine(fixture.Path, first.CsvFileName)));
        Assert.True(File.Exists(Path.Combine(fixture.Path, "other.txt")));
    }

    [Fact]
    public void Publisher_cleanup_preserves_unrelated_item_temporary_files()
    {
        using var fixture = DirectoryFixture.Create();
        var owned = Path.Combine(fixture.Path, $"items.{Guid.NewGuid():N}.tmp");
        var unrelated = Path.Combine(fixture.Path, "items-notes.tmp");
        File.WriteAllText(owned, "delete");
        File.WriteAllText(unrelated, "keep");

        new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch)).Publish(fixture.Path, [new ItemCatalogItem(1, "A", "Weapon", "Rare", 0)]);

        Assert.False(File.Exists(owned));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void Staged_generation_cleanup_keeps_current_and_unrelated_files()
    {
        using var fixture = DirectoryFixture.Create();
        var publisher = new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        var current = publisher.Publish(fixture.Path, [new ItemCatalogItem(1, "Current", "Weapon", "Rare", 0)]);
        var stale = Path.Combine(fixture.Path, "items." + new string('c', 64) + ".csv");
        var temp = Path.Combine(fixture.Path, $"items.{Guid.NewGuid():N}.tmp");
        var notes = Path.Combine(fixture.Path, "items-notes.tmp");
        File.WriteAllText(stale, "stale");
        File.WriteAllText(temp, "temp");
        File.WriteAllText(notes, "keep");

        publisher.CreateGeneration(fixture.Path, [new ItemCatalogItem(2, "Next", "Armor", "Exotic", 80)], 1, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(fixture.Path, current.CsvFileName)));
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(temp));
        Assert.True(File.Exists(notes));
    }

    [Fact]
    public void Csv_bytes_are_invariant_under_a_non_invariant_current_culture()
    {
        using var fixture = DirectoryFixture.Create();
        using var culture = new CurrentCultureScope(new CultureInfo("ar-SA"));

        var result = new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch)).Publish(fixture.Path, [new ItemCatalogItem(1234, "Name", "Weapon", "Rare", 80)]);

        Assert.Equal("id,name,type,rarity,level\r\n1234,Name,Weapon,Rare,80\r\n", File.ReadAllText(Path.Combine(fixture.Path, result.CsvFileName), new UTF8Encoding(false)));
    }

    [Fact]
    public void Publisher_does_not_publish_when_existing_generation_hash_mismatches()
    {
        using var fixture = DirectoryFixture.Create();
        var publisher = new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        var expected = publisher.CreateArtifact([new ItemCatalogItem(1, "A", "Weapon", "Rare", 0)]);
        File.WriteAllText(Path.Combine(fixture.Path, expected.CsvFileName), "wrong");

        Assert.Throws<ItemCachePublishException>(() => publisher.Publish(fixture.Path, [new ItemCatalogItem(1, "A", "Weapon", "Rare", 0)]));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "items.manifest.json")));
    }

    [Fact]
    public void Publisher_keeps_the_previous_pair_when_manifest_replacement_fails()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = DirectoryFixture.Create();
        var publisher = new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        var first = publisher.Publish(fixture.Path, [new ItemCatalogItem(1, "A", "Weapon", "Rare", 0)]);
        var manifestPath = Path.Combine(fixture.Path, "items.manifest.json");
        var previousManifest = File.ReadAllBytes(manifestPath);
        using var lockHandle = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Throws<ItemCachePublishException>(() => publisher.Publish(fixture.Path, [new ItemCatalogItem(2, "B", "Armor", "Exotic", 80)]));
        Assert.Equal(previousManifest, File.ReadAllBytes(manifestPath));
        Assert.True(File.Exists(Path.Combine(fixture.Path, first.CsvFileName)));
    }

    [Fact]
    public void Publisher_cancellation_does_not_publish_a_manifest()
    {
        using var fixture = DirectoryFixture.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch)).Publish(fixture.Path, [new ItemCatalogItem(1, "A", "Weapon", "Rare", 0)], cancellation.Token));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "items.manifest.json")));
    }

    [Fact]
    public async Task Publisher_wraps_output_directory_creation_failure_and_command_reports_publication_failure()
    {
        using var fixture = DirectoryFixture.Create();
        var outputFile = Path.Combine(fixture.Path, "output-test");
        File.WriteAllText(outputFile, "not a directory");
        var publisher = new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch));

        Assert.Throws<ItemCachePublishException>(() => publisher.Publish(outputFile, [new ItemCatalogItem(1, "A", "Weapon", "Rare", 0)]));

        var errors = new List<string>();
        var client = new FakeCatalogClient(Enumerable.Range(1, 200).Select(id => (long)id));
        var command = new ItemRefreshCommand(new ItemCacheRefreshService(client, publisher), new CountingLeaseFactory(), report: null, error: errors.Add);
        Assert.Equal(1, await command.RunAsync(["items-test", "--output", outputFile], CancellationToken.None));
        Assert.Equal(["Item cache publication failed."], errors);
    }

    [Fact]
    public void Publisher_does_not_delete_generations_when_the_existing_manifest_is_invalid()
    {
        using var fixture = DirectoryFixture.Create();
        var stale = Path.Combine(fixture.Path, "items." + new string('b', 64) + ".csv");
        File.WriteAllText(stale, "keep");
        File.WriteAllText(Path.Combine(fixture.Path, "items.manifest.json"), "not json");

        new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch)).Publish(fixture.Path, [new ItemCatalogItem(1, "A", "Weapon", "Rare", 0)]);
        Assert.True(File.Exists(stale));
    }

    [Fact]
    public async Task Refresh_failure_before_validation_does_not_publish()
    {
        using var fixture = DirectoryFixture.Create();
        var client = new InvalidCatalogClient();

        await Assert.ThrowsAsync<ItemCatalogDownloadException>(() => new ItemCacheRefreshService(client, new ItemCachePublisher(new FixedTimeProvider(DateTimeOffset.UnixEpoch))).RefreshAsync(fixture.Path, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "items.manifest.json")));
    }

    [Fact]
    public void Lease_is_exclusive_and_persists_after_release()
    {
        using var fixture = DirectoryFixture.Create();
        var path = Path.Combine(fixture.Path, "nested", "gw2-api-budget.lock");
        using (UpdaterLease.Acquire(path))
        {
            Assert.True(File.Exists(path));
            var error = Assert.Throws<InvalidOperationException>(() => UpdaterLease.Acquire(path));
            Assert.Contains("Stop MCP", error.Message, StringComparison.Ordinal);
        }
        using var reacquired = UpdaterLease.Acquire(path);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Command_validates_before_lease_or_network_and_lease_blocks_network_then_releases()
    {
        var lease = new CountingLeaseFactory();
        var service = new RecordingRefreshService();
        var command = new ItemRefreshCommand(service, lease);

        Assert.NotEqual(0, await command.RunAsync(["items"], CancellationToken.None));
        Assert.Equal(0, lease.AcquireCalls);
        Assert.Equal(0, service.Calls);
        Assert.NotEqual(0, await command.RunAsync(["items", "--output", "--unknown"], CancellationToken.None));
        Assert.Equal(0, lease.AcquireCalls);
        Assert.Equal(0, service.Calls);
        Assert.NotEqual(0, await command.RunAsync(["items-test", "--output", "out-test", "--fresh"], CancellationToken.None));
        Assert.Equal(0, lease.AcquireCalls);
        Assert.Equal(0, service.Calls);

        lease.ThrowOnAcquire = true;
        Assert.NotEqual(0, await command.RunAsync(["items", "--output", "out"], CancellationToken.None));
        Assert.Equal(0, service.Calls);
        lease.ThrowOnAcquire = false;
        Assert.Equal(0, await command.RunAsync(["items", "--output", "out"], CancellationToken.None));
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task Command_reports_distinct_redacted_role_specific_errors_and_test_success()
    {
        var errors = new List<string>();
        var lease = new CountingLeaseFactory { ThrowOnAcquire = true };
        var network = new ThrowingRefreshService("response body and C:\\secret");
        var command = new ItemRefreshCommand(network, lease, report: null, error: errors.Add);

        Assert.Equal(1, await command.RunAsync(["items", "--output", "out"], CancellationToken.None));
        Assert.Equal(["Refresh lease unavailable."], errors);
        Assert.Equal(0, network.Calls);

        lease.ThrowOnAcquire = false;
        Assert.Equal(1, await command.RunAsync(["items", "--output", "out"], CancellationToken.None));
        Assert.Equal("Item cache download failed.", errors[^1]);
        Assert.DoesNotContain("secret", errors[^1], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("response", errors[^1], StringComparison.OrdinalIgnoreCase);

        var invalidErrors = new List<string>();
        Assert.Equal(2, await new ItemRefreshCommand(network, lease, report: null, error: invalidErrors.Add).RunAsync(["items"], CancellationToken.None));
        Assert.Equal(["Invalid refresh arguments."], invalidErrors);

        var cancellationErrors = new List<string>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(1, await new ItemRefreshCommand(new CancelledRefreshService(), lease, report: null, error: cancellationErrors.Add).RunAsync(["items", "--output", "out"], cancellation.Token));
        Assert.Equal(["Item cache refresh cancelled."], cancellationErrors);

        var publicationErrors = new List<string>();
        Assert.Equal(1, await new ItemRefreshCommand(new PublicationRefreshService(), lease, report: null, error: publicationErrors.Add).RunAsync(["items", "--output", "out"], CancellationToken.None));
        Assert.Equal(["Item cache publication failed."], publicationErrors);

        var reports = new List<ItemRefreshSummary>();
        var successful = new RecordingRefreshService();
        var successCommand = new ItemRefreshCommand(successful, lease, reports.Add, _ => throw new Xunit.Sdk.XunitException("Unexpected error"));
        Assert.Equal(0, await successCommand.RunAsync(["items", "--output", "out"], CancellationToken.None));
        Assert.Equal(0, await successCommand.RunAsync(["items-test", "--output", "out-test"], CancellationToken.None));
        Assert.False(reports[0].IsTestCache);
        Assert.True(reports[1].IsTestCache);
        Assert.Equal(200, reports[1].ItemCount);
        Assert.Equal(2, reports[1].HttpAttemptCount);
        Assert.Equal("Production item cache published. Named 1 of 1 items; excluded blank names 0; generation x.", ItemRefreshCommand.FormatSuccess(reports[0]));
        Assert.Equal("Test item cache published. Named 200 of 200 items; excluded blank names 0; attempts 2; generation x.", ItemRefreshCommand.FormatSuccess(reports[1]));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static void RewriteShard(string stage, Func<byte[], byte[]> transform)
    {
        var shard = Directory.EnumerateFiles(stage, "items.batch.*.json").Single();
        var bytes = transform(File.ReadAllBytes(shard));
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var renamed = Path.Combine(stage, $"items.batch.0.{hash}.json");
        File.Delete(shard);
        File.WriteAllBytes(renamed, bytes);
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class ImmediateStartGate : IApiStartGate { public Task WaitAsync(CancellationToken cancellationToken) => Task.CompletedTask; }
    private sealed class CountingStartGate : IApiStartGate { public int Calls { get; private set; } public Task WaitAsync(CancellationToken cancellationToken) { Calls++; cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; } }
    private sealed class RecordingGate(TimeProvider time) : IApiStartGate { public List<DateTimeOffset> Starts { get; } = []; public Task WaitAsync(CancellationToken cancellationToken) { Starts.Add(time.GetUtcNow()); return Task.CompletedTask; } }
    private sealed class FakeCatalogClient(IEnumerable<long> ids) : IItemCatalogDownloadClient
    {
        private readonly long[] ids = ids.ToArray(); public int RootCalls { get; private set; } public List<long[]> Batches { get; } = []; public int AttemptCount => 1;
        public Task<IReadOnlyList<long>> GetRootIdsAsync(CancellationToken cancellationToken) { RootCalls++; return Task.FromResult<IReadOnlyList<long>>(ids); }
        public Task<IReadOnlyList<ItemCatalogItem>> GetDefinitionsAsync(IReadOnlyCollection<long> requestedIds, CancellationToken cancellationToken) { var batch = requestedIds.Order().ToArray(); Batches.Add(batch); return Task.FromResult<IReadOnlyList<ItemCatalogItem>>(batch.Select(id => new ItemCatalogItem(id, $"Name {id}", "Weapon", "Rare", 80)).ToArray()); }
    }
    private sealed class InvalidCatalogClient : IItemCatalogDownloadClient
    {
        public int AttemptCount => 1;
        public Task<IReadOnlyList<long>> GetRootIdsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<long>>([1]);
        public Task<IReadOnlyList<ItemCatalogItem>> GetDefinitionsAsync(IReadOnlyCollection<long> requestedIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ItemCatalogItem>>([new ItemCatalogItem(2, "Wrong", "Weapon", "Rare", 0)]);
    }
    private sealed class BoundedCatalogClient(IEnumerable<long> ids) : IItemCatalogDownloadClient
    {
        private readonly long[] ids = ids.ToArray();
        private readonly TaskCompletionSource firstWaveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int activeCalls;
        private int maximumActiveCalls;
        public int RootCalls { get; private set; }
        public int MaximumActiveCalls => Volatile.Read(ref maximumActiveCalls);
        public List<long[]> Batches { get; } = [];
        public Task FirstWaveStarted => firstWaveStarted.Task;
        public int AttemptCount => 0;
        public Task<IReadOnlyList<long>> GetRootIdsAsync(CancellationToken cancellationToken) { RootCalls++; return Task.FromResult<IReadOnlyList<long>>(ids); }
        public async Task<IReadOnlyList<ItemCatalogItem>> GetDefinitionsAsync(IReadOnlyCollection<long> requestedIds, CancellationToken cancellationToken)
        {
            var batch = requestedIds.Order().ToArray();
            lock (Batches) Batches.Add(batch);
            var active = Interlocked.Increment(ref activeCalls);
            UpdateMaximum(active);
            if (active == 4) firstWaveStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref activeCalls);
            return batch.Select(id => new ItemCatalogItem(id, $"Name {id}", "Weapon", "Rare", 80)).ToArray();
        }
        public void ReleaseFirstWave() => release.TrySetResult();
        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var current = MaximumActiveCalls;
                if (active <= current || Interlocked.CompareExchange(ref maximumActiveCalls, active, current) == current) return;
            }
        }
    }
    private sealed class DrainingFailureCatalogClient(IEnumerable<long> ids, bool unexpected = false) : IItemCatalogDownloadClient
    {
        private readonly long[] ids = ids.ToArray();
        private readonly TaskCompletionSource firstWaveStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int activeCalls;
        private int maximumActiveCalls;
        public List<long[]> Batches { get; } = [];
        public Task FirstWaveStarted => firstWaveStarted.Task;
        public int ActiveCalls => Volatile.Read(ref activeCalls);
        public int MaximumActiveCalls => Volatile.Read(ref maximumActiveCalls);
        public int AttemptCount => 1;
        public Task<IReadOnlyList<long>> GetRootIdsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<long>>(ids);
        public Task<IReadOnlyList<ItemCatalogItem>> GetDefinitionsAsync(IReadOnlyCollection<long> requestedIds, CancellationToken cancellationToken)
        {
            var batch = requestedIds.Order().ToArray();
            lock (Batches)
            {
                Batches.Add(batch);
                if (Batches.Count == 4) firstWaveStarted.TrySetResult();
            }
            if (batch[0] == 1) return Task.FromException<IReadOnlyList<ItemCatalogItem>>(unexpected ? new InvalidOperationException() : new ItemCatalogDownloadException());
            return CompleteWhenReleased(batch, cancellationToken);
        }
        public void ReleaseStartedBatches() => release.TrySetResult();
        private async Task<IReadOnlyList<ItemCatalogItem>> CompleteWhenReleased(long[] batch, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref activeCalls);
            while (true)
            {
                var current = MaximumActiveCalls;
                if (active <= current || Interlocked.CompareExchange(ref maximumActiveCalls, active, current) == current) break;
            }
            try { await release.Task.WaitAsync(cancellationToken); return batch.Select(id => new ItemCatalogItem(id, $"Name {id}", "Weapon", "Rare", 80)).ToArray(); }
            finally { Interlocked.Decrement(ref activeCalls); }
        }
    }
    private sealed class CancellationCatalogClient(IEnumerable<long> ids) : IItemCatalogDownloadClient
    {
        private readonly long[] ids = ids.ToArray();
        private readonly TaskCompletionSource blockingBatchStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task BlockingBatchStarted => blockingBatchStarted.Task;
        public int AttemptCount => 1;
        public Task<IReadOnlyList<long>> GetRootIdsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<long>>(ids);
        public Task<IReadOnlyList<ItemCatalogItem>> GetDefinitionsAsync(IReadOnlyCollection<long> requestedIds, CancellationToken cancellationToken)
        {
            var batch = requestedIds.Order().ToArray();
            if (batch[0] == 1) return Task.FromResult<IReadOnlyList<ItemCatalogItem>>(batch.Select(id => new ItemCatalogItem(id, $"Name {id}", "Weapon", "Rare", 80)).ToArray());
            blockingBatchStarted.TrySetResult();
            return WaitForCancellation(cancellationToken);
        }
        private static async Task<IReadOnlyList<ItemCatalogItem>> WaitForCancellation(CancellationToken cancellationToken) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return []; }
    }
    private sealed class StaticItemCatalogClient(IEnumerable<ItemCatalogItem> items) : IItemCatalogDownloadClient
    {
        private readonly ItemCatalogItem[] items = items.ToArray();
        public int RootCalls { get; private set; }
        public int AttemptCount => 1;
        public Task<IReadOnlyList<long>> GetRootIdsAsync(CancellationToken cancellationToken) { RootCalls++; return Task.FromResult<IReadOnlyList<long>>(items.Select(item => item.Id).Reverse().ToArray()); }
        public Task<IReadOnlyList<ItemCatalogItem>> GetDefinitionsAsync(IReadOnlyCollection<long> requestedIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ItemCatalogItem>>(items.Where(item => requestedIds.Contains(item.Id)).Reverse().ToArray());
    }
    private sealed class MultiFailureCatalogClient(IEnumerable<long> ids, IReadOnlySet<int> failedBatchIndexes) : IItemCatalogDownloadClient
    {
        private readonly long[] ids = ids.ToArray();
        public int AttemptCount => 1;
        public Task<IReadOnlyList<long>> GetRootIdsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<long>>(ids);
        public Task<IReadOnlyList<ItemCatalogItem>> GetDefinitionsAsync(IReadOnlyCollection<long> requestedIds, CancellationToken cancellationToken)
        {
            var batch = requestedIds.Order().ToArray();
            if (failedBatchIndexes.Contains((int)((batch[0] - 1) / 200))) throw new ItemCatalogDownloadException();
            return Task.FromResult<IReadOnlyList<ItemCatalogItem>>(batch.Select(id => new ItemCatalogItem(id, $"Name {id}", "Weapon", "Rare", 80)).ToArray());
        }
    }
    private sealed class MixedFailureCatalogClient(IEnumerable<long> ids, IReadOnlyDictionary<int, ItemCatalogDownloadFailureKind> failures) : IItemCatalogDownloadClient
    {
        private readonly long[] ids = ids.ToArray();
        public int AttemptCount => 1;
        public Task<IReadOnlyList<long>> GetRootIdsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<long>>(ids);
        public Task<IReadOnlyList<ItemCatalogItem>> GetDefinitionsAsync(IReadOnlyCollection<long> requestedIds, CancellationToken cancellationToken)
        {
            var batch = requestedIds.Order().ToArray();
            if (failures.TryGetValue((int)((batch[0] - 1) / 200), out var kind)) throw new ItemCatalogDownloadException(kind);
            return Task.FromResult<IReadOnlyList<ItemCatalogItem>>(batch.Select(id => new ItemCatalogItem(id, $"Name {id}", "Weapon", "Rare", 80)).ToArray());
        }
    }
    private sealed class ResumeCatalogClient(IEnumerable<long> ids, int? failingBatchIndex = null) : IItemCatalogDownloadClient
    {
        private readonly long[] ids = ids.ToArray();
        public List<long[]> Batches { get; } = [];
        public int RootCalls { get; private set; }
        public int AttemptCount => 1;
        public Task<IReadOnlyList<long>> GetRootIdsAsync(CancellationToken cancellationToken) { RootCalls++; return Task.FromResult<IReadOnlyList<long>>(ids); }
        public Task<IReadOnlyList<ItemCatalogItem>> GetDefinitionsAsync(IReadOnlyCollection<long> requestedIds, CancellationToken cancellationToken)
        {
            var batch = requestedIds.Order().ToArray();
            Batches.Add(batch);
            if (failingBatchIndex == (batch[0] - 1) / 200) throw new ItemCatalogDownloadException();
            return Task.FromResult<IReadOnlyList<ItemCatalogItem>>(batch.Select(id => new ItemCatalogItem(id, $"Name {id}", "Weapon", "Rare", 80)).ToArray());
        }
    }
    private sealed class DirectoryFixture : IDisposable { private DirectoryFixture(string path) => Path = path; public string Path { get; } public static DirectoryFixture Create(string? name = null) { var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GW2AccountMCP.Tests", Guid.NewGuid().ToString("N"), name ?? "cache"); Directory.CreateDirectory(path); return new(path); } public void Dispose() { var root = System.IO.Path.GetDirectoryName(Path)!; if (Directory.Exists(root)) Directory.Delete(root, true); } }
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class CurrentCultureScope : IDisposable { private readonly CultureInfo previous = CultureInfo.CurrentCulture; public CurrentCultureScope(CultureInfo culture) => CultureInfo.CurrentCulture = culture; public void Dispose() => CultureInfo.CurrentCulture = previous; }
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<(TimerCallback Callback, object? State, DateTimeOffset Due)> timers = []; private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) { timers.Add((callback, state, now + dueTime)); return new NoopTimer(); }
        public void Advance(TimeSpan elapsed) { now += elapsed; foreach (var timer in timers.Where(timer => timer.Due <= now).ToArray()) { timers.Remove(timer); timer.Callback(timer.State); } }
        private sealed class NoopTimer : ITimer { public bool Change(TimeSpan dueTime, TimeSpan period) => true; public void Dispose() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
    private sealed class CountingLeaseFactory : IUpdaterLeaseFactory { public int AcquireCalls { get; private set; } public bool ThrowOnAcquire { get; set; } public IDisposable Acquire() { AcquireCalls++; if (ThrowOnAcquire) throw new InvalidOperationException("Stop MCP before refreshing."); return new MemoryStream(); } }
    private sealed class RecordingRefreshService : IItemCacheRefreshService { public int Calls { get; private set; } public List<string> TestOutputs { get; } = []; public Task<ItemRefreshSummary> RefreshAsync(string outputDirectory, CancellationToken cancellationToken) { Calls++; return Task.FromResult(new ItemRefreshSummary(1, 1, "x", "y")); } public Task<ItemRefreshSummary> RefreshTestAsync(string outputDirectory, CancellationToken cancellationToken) { TestOutputs.Add(outputDirectory); return Task.FromResult(new ItemRefreshSummary(200, 2, "x", "y", true)); } }
    private sealed class ThrowingRefreshService(string message) : IItemCacheRefreshService { public int Calls { get; private set; } public Task<ItemRefreshSummary> RefreshAsync(string outputDirectory, CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException(message); } public Task<ItemRefreshSummary> RefreshTestAsync(string outputDirectory, CancellationToken cancellationToken) => RefreshAsync(outputDirectory, cancellationToken); }
    private sealed class CancelledRefreshService : IItemCacheRefreshService { public Task<ItemRefreshSummary> RefreshAsync(string outputDirectory, CancellationToken cancellationToken) => throw new OperationCanceledException(); public Task<ItemRefreshSummary> RefreshTestAsync(string outputDirectory, CancellationToken cancellationToken) => RefreshAsync(outputDirectory, cancellationToken); }
    private sealed class PublicationRefreshService : IItemCacheRefreshService { public Task<ItemRefreshSummary> RefreshAsync(string outputDirectory, CancellationToken cancellationToken) => throw new ItemCachePublishException(); public Task<ItemRefreshSummary> RefreshTestAsync(string outputDirectory, CancellationToken cancellationToken) => RefreshAsync(outputDirectory, cancellationToken); }
    private sealed class BlockingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new BlockingStream());
        private sealed class BlockingStream : Stream
        {
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => 0; public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { } public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask; public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
