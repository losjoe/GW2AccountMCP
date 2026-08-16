using System.Security.Cryptography;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GW2AccountMCP.DataRefresh;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class PriceCacheRefreshTests
{
    [Fact]
    public async Task Download_client_reads_canonical_id_root_and_uses_sorted_exact_details_query()
    {
        var requests = new List<string>();
        const string details = "[{\"id\":1,\"whitelisted\":true,\"buys\":{\"quantity\":1,\"unit_price\":1},\"sells\":{\"quantity\":1,\"unit_price\":1}},{\"id\":3,\"whitelisted\":false,\"buys\":{\"quantity\":0,\"unit_price\":0},\"sells\":{\"quantity\":2,\"unit_price\":5}}]";
        using var http = new HttpClient(new ScriptedHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            return requests.Count == 1 ? Json(HttpStatusCode.OK, "[3,1]") : Json(HttpStatusCode.OK, details);
        })) { BaseAddress = new Uri("https://fake.test") };
        var client = new PriceCatalogDownloadClient(http, startGate: new ImmediateStartGate());

        var root = await client.GetRootIdsAsync(CancellationToken.None);
        var prices = await client.GetPricesAsync(root.Order().ToArray(), CancellationToken.None);

        Assert.Equal([3L, 1L], root);
        Assert.Equal([1L, 3L], prices.Select(price => price.Id).Order());
        Assert.Equal(["/v2/commerce/prices", "/v2/commerce/prices?ids=1,3"], requests);
    }

    [Fact]
    public async Task Fresh_refresh_publishes_a_deterministic_complete_price_snapshot()
    {
        using var fixture = DirectoryFixture.Create();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 15, 12, 34, 56, TimeSpan.Zero));
        var client = new FakePriceClient([Price(3), Price(1, buyQuantity: 0, buyUnitPrice: 0), Price(2)]);

        var result = await new PriceCacheRefreshService(client, new PriceCachePublisher(time), time).RefreshAsync(fixture.Path, fresh: false, CancellationToken.None);

        var bytes = File.ReadAllBytes(Path.Combine(fixture.Path, result.CsvFileName));
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal("id,whitelisted,buyQuantity,buyUnitPrice,sellQuantity,sellUnitPrice\r\n1,true,0,0,2,3\r\n2,true,2,3,2,3\r\n3,true,2,3,2,3\r\n", new UTF8Encoding(false, true).GetString(bytes));
        Assert.Equal(hash, result.CsvSha256);
        Assert.Equal($"prices.{hash}.csv", result.CsvFileName);
        Assert.Equal(1, client.RootCalls);
        Assert.Equal([[1L, 2L, 3L]], client.Batches);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.Path, "prices.manifest.json")));
        Assert.Equal("production", manifest.RootElement.GetProperty("scope").GetString());
        Assert.True(manifest.RootElement.GetProperty("isComplete").GetBoolean());
        Assert.Equal(3, manifest.RootElement.GetProperty("rowCount").GetInt32());
        Assert.Equal(bytes.Length, manifest.RootElement.GetProperty("csvByteLength").GetInt64());
        Assert.Equal(hash, manifest.RootElement.GetProperty("csvSha256").GetString());
        Assert.True(Directory.Exists(Path.Combine(fixture.Path, ".prices-staging")));
    }

    [Fact]
    public async Task Incomplete_refresh_resumes_persisted_root_without_rerooting_and_completed_staging_repairs_without_network()
    {
        using var fixture = DirectoryFixture.Create();
        var root = Enumerable.Range(1, 400).Select(id => Price(id)).ToArray();
        var failed = new FakePriceClient(root, failingBatchIndex: 1);
        await Assert.ThrowsAsync<PriceCacheIncompleteException>(() => new PriceCacheRefreshService(failed, new PriceCachePublisher(TimeProvider.System)).RefreshAsync(fixture.Path, false, CancellationToken.None));
        Assert.Equal(1, failed.RootCalls);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Path, ".prices-staging"), "prices.batch.*.json"));

        var resumed = new FakePriceClient(root);
        var first = await new PriceCacheRefreshService(resumed, new PriceCachePublisher(TimeProvider.System)).RefreshAsync(fixture.Path, false, CancellationToken.None);
        Assert.Equal(0, resumed.RootCalls);
        Assert.Equal([Enumerable.Range(201, 200).Select(id => (long)id).ToArray()], resumed.Batches);
        var manifest = File.ReadAllBytes(Path.Combine(fixture.Path, "prices.manifest.json"));
        File.Delete(Path.Combine(fixture.Path, "prices.manifest.json"));

        var repair = new FakePriceClient(root);
        var second = await new PriceCacheRefreshService(repair, new PriceCachePublisher(TimeProvider.System)).RefreshAsync(fixture.Path, false, CancellationToken.None);
        Assert.Equal(0, repair.RootCalls);
        Assert.Empty(repair.Batches);
        Assert.Equal(first.CsvSha256, second.CsvSha256);
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(fixture.Path, "prices.manifest.json")));
    }

    [Fact]
    public async Task Fresh_byte_identical_collection_replaces_source_timestamps_but_retains_generation_hash()
    {
        using var fixture = DirectoryFixture.Create();
        var prices = new[] { Price(1), Price(2) };
        var first = await new PriceCacheRefreshService(new FakePriceClient(prices), new PriceCachePublisher(TimeProvider.System), new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))).RefreshAsync(fixture.Path, false, CancellationToken.None);
        var before = File.ReadAllText(Path.Combine(fixture.Path, "prices.manifest.json"));
        var second = await new PriceCacheRefreshService(new FakePriceClient(prices), new PriceCachePublisher(TimeProvider.System), new FixedTimeProvider(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero))).RefreshAsync(fixture.Path, true, CancellationToken.None);

        Assert.Equal(first.CsvSha256, second.CsvSha256);
        Assert.NotEqual(before, File.ReadAllText(Path.Combine(fixture.Path, "prices.manifest.json")));
        Assert.True(Directory.Exists(Path.Combine(fixture.Path, ".prices-staging")));
    }

    [Fact]
    public async Task Test_refresh_is_isolated_incomplete_and_does_not_create_staging()
    {
        using var fixture = DirectoryFixture.Create("prices-test");
        var client = new FakePriceClient(Enumerable.Range(1, 500).Reverse().Select(id => Price(id)));

        var summary = await new PriceCacheRefreshService(client, new PriceCachePublisher(TimeProvider.System)).RefreshTestAsync(fixture.Path, CancellationToken.None);

        Assert.True(summary.IsTestCache);
        Assert.Equal(200, summary.PriceCount);
        Assert.Equal([Enumerable.Range(1, 200).Select(id => (long)id).ToArray()], client.Batches);
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, ".prices-staging")));
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.Path, "prices.manifest.json")));
        Assert.Equal("test", manifest.RootElement.GetProperty("scope").GetString());
        Assert.False(manifest.RootElement.GetProperty("isComplete").GetBoolean());
    }

    [Fact]
    public async Task Test_refresh_refuses_to_replace_a_production_manifest_before_network()
    {
        using var fixture = DirectoryFixture.Create("prices-test");
        File.WriteAllText(Path.Combine(fixture.Path, "prices.manifest.json"), "{\"scope\":\"production\"}");
        var client = new FakePriceClient([Price(1)]);

        await Assert.ThrowsAsync<PriceCachePublishException>(() => new PriceCacheRefreshService(client, new PriceCachePublisher(TimeProvider.System)).RefreshTestAsync(fixture.Path, CancellationToken.None));

        Assert.Equal(0, client.RootCalls);
        Assert.Equal("{\"scope\":\"production\"}", File.ReadAllText(Path.Combine(fixture.Path, "prices.manifest.json")));
    }

    [Fact]
    public async Task Test_command_rejects_existing_production_manifest_before_lease_or_service()
    {
        using var fixture = DirectoryFixture.Create("prices-test");
        File.WriteAllText(Path.Combine(fixture.Path, "prices.manifest.json"), "{\"scope\":\"production\"}");
        var lease = new CountingLeaseFactory();
        var service = new RecordingService();

        Assert.Equal(2, await new PriceRefreshCommand(() => service, lease).RunAsync(["tp-test", "--output", fixture.Path], CancellationToken.None));

        Assert.Equal(0, lease.Calls);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task Collected_state_survives_generation_failure_and_resumes_without_network()
    {
        using var fixture = DirectoryFixture.Create();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 15, 1, 2, 3, TimeSpan.Zero));
        var firstClient = new FakePriceClient([Price(2), Price(1)]);
        await Assert.ThrowsAsync<PriceCachePublishException>(() => new PriceCacheRefreshService(firstClient, new PriceCachePublisher(time, beforeGeneration: () => throw new InvalidOperationException()), time).RefreshAsync(fixture.Path, false, CancellationToken.None));
        var state = File.ReadAllText(Path.Combine(fixture.Path, ".prices-staging", "prices.resume-state.json"));
        Assert.Contains("\"status\":\"collected\"", state, StringComparison.Ordinal);

        var resumedClient = new FakePriceClient([Price(1), Price(2)]);
        var result = await new PriceCacheRefreshService(resumedClient, new PriceCachePublisher(time), time).RefreshAsync(fixture.Path, false, CancellationToken.None);

        Assert.Equal(0, resumedClient.RootCalls);
        Assert.Empty(resumedClient.Batches);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.Path, "prices.manifest.json")));
        Assert.Equal("2026-08-15T01:02:03.0000000Z", manifest.RootElement.GetProperty("sourceStartedAtUtc").GetString());
        Assert.Equal("2026-08-15T01:02:03.0000000Z", manifest.RootElement.GetProperty("sourceCompletedAtUtc").GetString());
        Assert.Equal(result.CsvSha256, manifest.RootElement.GetProperty("csvSha256").GetString());
    }

    [Fact]
    public async Task Cancellation_at_manifest_boundary_preserves_the_existing_publication()
    {
        using var fixture = DirectoryFixture.Create();
        var initial = await new PriceCacheRefreshService(new FakePriceClient([Price(1)]), new PriceCachePublisher(TimeProvider.System)).RefreshAsync(fixture.Path, false, CancellationToken.None);
        var manifest = File.ReadAllBytes(Path.Combine(fixture.Path, "prices.manifest.json"));
        using var cancellation = new CancellationTokenSource();
        var publisher = new PriceCachePublisher(TimeProvider.System, beforeManifestReplacement: cancellation.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PriceCacheRefreshService(new FakePriceClient([Price(2)]), publisher).RefreshAsync(fixture.Path, true, cancellation.Token));

        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(fixture.Path, "prices.manifest.json")));
        Assert.True(File.Exists(Path.Combine(fixture.Path, initial.CsvFileName)));
    }

    [Fact]
    public async Task Root_client_enforces_the_two_megabyte_and_one_hundred_thousand_id_bounds()
    {
        using var tooManyHttp = new HttpClient(new ScriptedHandler(_ => Json(HttpStatusCode.OK, "[" + string.Join(',', Enumerable.Range(1, 100_001)) + "]"))) { BaseAddress = new Uri("https://fake.test") };
        await Assert.ThrowsAsync<PriceCatalogDownloadException>(() => new PriceCatalogDownloadClient(tooManyHttp, startGate: new ImmediateStartGate()).GetRootIdsAsync(CancellationToken.None));

        using var tooLargeHttp = new HttpClient(new ScriptedHandler(_ => Json(HttpStatusCode.OK, "[" + new string(' ', 2 * 1024 * 1024) + "]"))) { BaseAddress = new Uri("https://fake.test") };
        await Assert.ThrowsAsync<PriceCatalogDownloadException>(() => new PriceCatalogDownloadClient(tooLargeHttp, startGate: new ImmediateStartGate()).GetRootIdsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[1,1]")]
    [InlineData("[0]")]
    [InlineData("[\"1\"]")]
    public async Task Root_client_rejects_invalid_canonical_id_sets(string body)
    {
        using var http = new HttpClient(new ScriptedHandler(_ => Json(HttpStatusCode.OK, body))) { BaseAddress = new Uri("https://fake.test") };
        await Assert.ThrowsAsync<PriceCatalogDownloadException>(() => new PriceCatalogDownloadClient(http, startGate: new ImmediateStartGate()).GetRootIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Root_client_rejects_partial_content_while_details_may_use_it()
    {
        using var http = new HttpClient(new ScriptedHandler(_ => Json(HttpStatusCode.PartialContent, "[1]"))) { BaseAddress = new Uri("https://fake.test") };
        await Assert.ThrowsAsync<PriceCatalogDownloadException>(() => new PriceCatalogDownloadClient(http, startGate: new ImmediateStartGate()).GetRootIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Test_refresh_rejects_short_root_before_requesting_details_or_publishing()
    {
        using var fixture = DirectoryFixture.Create("prices-test");
        var client = new FakePriceClient(Enumerable.Range(1, 199).Select(id => Price(id)));

        await Assert.ThrowsAsync<PriceCatalogDownloadException>(() => new PriceCacheRefreshService(client, new PriceCachePublisher(TimeProvider.System)).RefreshTestAsync(fixture.Path, CancellationToken.None));

        Assert.Equal(1, client.RootCalls);
        Assert.Empty(client.Batches);
        Assert.False(File.Exists(Path.Combine(fixture.Path, "prices.manifest.json")));
    }

    [Fact]
    public async Task Test_command_rejects_file_output_and_manifest_directory_before_lease_or_service()
    {
        using var fileFixture = DirectoryFixture.Create();
        var fileOutput = Path.Combine(fileFixture.Path, "output-test");
        File.WriteAllText(fileOutput, "not a directory");
        var fileLease = new CountingLeaseFactory();
        var fileService = new RecordingService();
        Assert.Equal(2, await new PriceRefreshCommand(() => fileService, fileLease).RunAsync(["tp-test", "--output", fileOutput], CancellationToken.None));
        Assert.Equal(0, fileLease.Calls);
        Assert.Equal(0, fileService.Calls);

        var intermediate = Path.Combine(fileFixture.Path, "middle");
        File.WriteAllText(intermediate, "not a directory");
        var intermediateLease = new CountingLeaseFactory();
        var intermediateService = new RecordingService();
        Assert.Equal(2, await new PriceRefreshCommand(() => intermediateService, intermediateLease).RunAsync(["tp-test", "--output", Path.Combine(intermediate, "nested-test")], CancellationToken.None));
        Assert.Equal(0, intermediateLease.Calls);
        Assert.Equal(0, intermediateService.Calls);

        using var manifestFixture = DirectoryFixture.Create("manifest-test");
        Directory.CreateDirectory(Path.Combine(manifestFixture.Path, "prices.manifest.json"));
        var manifestLease = new CountingLeaseFactory();
        var manifestService = new RecordingService();
        Assert.Equal(2, await new PriceRefreshCommand(() => manifestService, manifestLease).RunAsync(["tp-test", "--output", manifestFixture.Path], CancellationToken.None));
        Assert.Equal(0, manifestLease.Calls);
        Assert.Equal(0, manifestService.Calls);
    }

    [Fact]
    public async Task Clock_regression_fails_before_production_publication_and_leaves_completed_staging_resumable()
    {
        using var fixture = DirectoryFixture.Create();
        var initial = await new PriceCacheRefreshService(new FakePriceClient([Price(1)]), new PriceCachePublisher(TimeProvider.System), new FixedTimeProvider(new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero))).RefreshAsync(fixture.Path, false, CancellationToken.None);
        var manifest = File.ReadAllBytes(Path.Combine(fixture.Path, "prices.manifest.json"));
        var backwards = new SequenceTimeProvider(new DateTimeOffset(2026, 2, 3, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 2, 3, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero));

        await Assert.ThrowsAsync<PriceClockException>(() => new PriceCacheRefreshService(new FakePriceClient([Price(2)]), new PriceCachePublisher(TimeProvider.System), backwards).RefreshAsync(fixture.Path, true, CancellationToken.None));

        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(fixture.Path, "prices.manifest.json")));
        Assert.True(File.Exists(Path.Combine(fixture.Path, ".prices-staging", "prices.resume-state.json")));
        Assert.True(File.Exists(Path.Combine(fixture.Path, initial.CsvFileName)));

        var resumedClient = new FakePriceClient([Price(2)]);
        await new PriceCacheRefreshService(resumedClient, new PriceCachePublisher(TimeProvider.System), new FixedTimeProvider(new DateTimeOffset(2026, 2, 4, 0, 0, 0, TimeSpan.Zero))).RefreshAsync(fixture.Path, false, CancellationToken.None);
        Assert.Equal(0, resumedClient.RootCalls);
        Assert.Empty(resumedClient.Batches);
    }

    [Fact]
    public async Task Command_reports_clock_regression_without_instructing_a_fresh_refresh()
    {
        using var fixture = DirectoryFixture.Create();
        var errors = new List<string>();
        var lease = new CountingLeaseFactory();
        var clock = new SequenceTimeProvider(new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var command = new PriceRefreshCommand(() => new PriceCacheRefreshService(new FakePriceClient([Price(1)]), new PriceCachePublisher(TimeProvider.System), clock), lease, error: errors.Add);

        Assert.Equal(1, await command.RunAsync(["tp", "--output", fixture.Path], CancellationToken.None));

        Assert.Equal(1, lease.Calls);
        Assert.Equal(["Price cache refresh failed because the system clock moved backwards. Correct the system clock and rerun the same command without --fresh."], errors);
    }

    [Fact]
    public async Task Reparse_output_is_rejected_before_test_lease_or_fresh_deletion_when_supported()
    {
        using var fixture = DirectoryFixture.Create();
        var redirected = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "redirected-test");
        try { Directory.CreateSymbolicLink(redirected, fixture.Path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }
        var lease = new CountingLeaseFactory();
        var service = new RecordingService();

        Assert.Equal(2, await new PriceRefreshCommand(() => service, lease).RunAsync(["tp-test", "--output", redirected], CancellationToken.None));
        Assert.Equal(0, lease.Calls);
        Assert.Equal(0, service.Calls);
        Assert.Throws<PriceStagingException>(() => PriceCacheRefreshService.PrepareFreshOutput(redirected));
        Assert.True(Directory.Exists(fixture.Path));
    }

    [Theory]
    [InlineData("not-a-test-cache")]
    [InlineData("\0")]
    public async Task Test_command_rejects_unsafe_output_before_lease_or_service(string output)
    {
        var lease = new CountingLeaseFactory();
        var service = new RecordingService();
        var command = new PriceRefreshCommand(() => service, lease);

        Assert.Equal(2, await command.RunAsync(["tp-test", "--output", output], CancellationToken.None));
        Assert.Equal(0, lease.Calls);
        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public void Fresh_preflight_refuses_unknown_entries_without_deleting_staging_or_publication()
    {
        using var fixture = DirectoryFixture.Create();
        var stage = Path.Combine(fixture.Path, ".prices-staging");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "unknown.txt"), "keep");
        File.WriteAllText(Path.Combine(fixture.Path, "prices.manifest.json"), "published");

        var error = Assert.Throws<PriceStagingException>(() => PriceCacheRefreshService.PrepareFreshOutput(fixture.Path));

        Assert.True(error.FreshPreflight);
        Assert.True(File.Exists(Path.Combine(stage, "unknown.txt")));
        Assert.Equal("published", File.ReadAllText(Path.Combine(fixture.Path, "prices.manifest.json")));
    }

    [Fact]
    public async Task Corrupt_retained_state_fails_closed_before_any_network_request()
    {
        using var fixture = DirectoryFixture.Create();
        var stage = Path.Combine(fixture.Path, ".prices-staging");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "prices.resume-state.json"), "not-json");
        var client = new FakePriceClient([Price(1)]);

        await Assert.ThrowsAsync<PriceStagingException>(() => new PriceCacheRefreshService(client, new PriceCachePublisher(TimeProvider.System)).RefreshAsync(fixture.Path, false, CancellationToken.None));

        Assert.Equal(0, client.RootCalls);
        Assert.Empty(client.Batches);
    }

    [Theory]
    [InlineData("{\"id\":1,\"whitelisted\":true,\"buys\":{\"quantity\":1,\"unit_price\":0},\"sells\":{\"quantity\":1,\"unit_price\":1}}")]
    [InlineData("{\"id\":1,\"whitelisted\":true,\"buys\":{\"quantity\":-1,\"unit_price\":1},\"sells\":{\"quantity\":1,\"unit_price\":1}}")]
    [InlineData("{\"id\":1,\"whitelisted\":\"true\",\"buys\":{\"quantity\":1,\"unit_price\":1},\"sells\":{\"quantity\":1,\"unit_price\":1}}")]
    public async Task Download_client_rejects_malformed_price_rows(string row)
    {
        using var http = new HttpClient(new ScriptedHandler(_ => Json(HttpStatusCode.OK, "[" + row + "]"))) { BaseAddress = new Uri("https://fake.test"), Timeout = Timeout.InfiniteTimeSpan };
        await Assert.ThrowsAsync<PriceCatalogDownloadException>(() => new PriceCatalogDownloadClient(http, startGate: new ImmediateStartGate()).GetPricesAsync([1], CancellationToken.None));
    }

    [Fact]
    public async Task Download_client_accepts_206_only_when_service_validates_the_exact_requested_subset()
    {
        const string row = "{\"id\":2,\"whitelisted\":true,\"buys\":{\"quantity\":0,\"unit_price\":0},\"sells\":{\"quantity\":1,\"unit_price\":2},\"future\":true}";
        using var http = new HttpClient(new ScriptedHandler(_ => Json(HttpStatusCode.PartialContent, "[" + row + "]"))) { BaseAddress = new Uri("https://fake.test") };
        var prices = await new PriceCatalogDownloadClient(http, startGate: new ImmediateStartGate()).GetPricesAsync([2], CancellationToken.None);
        Assert.Single(prices);
    }

    [Fact]
    public async Task Invalid_or_non_exact_new_batch_is_not_persisted_or_published()
    {
        using var fixture = DirectoryFixture.Create();
        var root = new[] { Price(1), Price(2) };
        var client = new FakePriceClient(root, response: _ => [Price(1), Price(1)]);

        await Assert.ThrowsAsync<PriceCacheIncompleteException>(() => new PriceCacheRefreshService(client, new PriceCachePublisher(TimeProvider.System)).RefreshAsync(fixture.Path, false, CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Path, ".prices-staging"), "prices.batch.*.json"));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "prices.manifest.json")));
    }

    [Fact]
    public async Task Fresh_failure_keeps_an_existing_published_manifest_and_only_replaces_staging_after_preflight()
    {
        using var fixture = DirectoryFixture.Create();
        var initial = new[] { Price(1), Price(2) };
        await new PriceCacheRefreshService(new FakePriceClient(initial), new PriceCachePublisher(TimeProvider.System)).RefreshAsync(fixture.Path, false, CancellationToken.None);
        var manifest = File.ReadAllBytes(Path.Combine(fixture.Path, "prices.manifest.json"));
        var failing = new FakePriceClient(new[] { Price(3), Price(4) }, failingBatchIndex: 0);

        await Assert.ThrowsAsync<PriceCacheIncompleteException>(() => new PriceCacheRefreshService(failing, new PriceCachePublisher(TimeProvider.System)).RefreshAsync(fixture.Path, true, CancellationToken.None));

        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(fixture.Path, "prices.manifest.json")));
        Assert.True(File.Exists(Path.Combine(fixture.Path, ".prices-staging", "prices.resume-state.json")));
    }

    [Fact]
    public async Task Download_client_retries_transient_status_through_the_shared_start_gate()
    {
        var calls = 0;
        var gate = new CountingGate();
        using var http = new HttpClient(new ScriptedHandler(_ => ++calls == 1 ? new HttpResponseMessage((HttpStatusCode)429) : Json(HttpStatusCode.OK, "[1]"))) { BaseAddress = new Uri("https://fake.test"), Timeout = Timeout.InfiniteTimeSpan };

        await new PriceCatalogDownloadClient(http, startGate: gate).GetRootIdsAsync(CancellationToken.None);

        Assert.Equal(2, gate.Calls);
    }

    [Fact]
    public async Task Download_client_honors_retry_after_and_retries_a_timed_out_body_once()
    {
        var time = new ManualTimeProvider();
        var retryCalls = 0;
        using var retryHttp = new HttpClient(new ScriptedHandler(_ => ++retryCalls == 1 ? new HttpResponseMessage((HttpStatusCode)429) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1)) } } : Json(HttpStatusCode.OK, "[1]"))) { BaseAddress = new Uri("https://fake.test"), Timeout = Timeout.InfiniteTimeSpan };
        var retry = new PriceCatalogDownloadClient(retryHttp, time, new ImmediateStartGate());
        var retried = retry.GetRootIdsAsync(CancellationToken.None);
        await Task.Yield();
        time.Advance(TimeSpan.FromSeconds(1));
        await retried;
        Assert.Equal(2, retry.AttemptCount);

        using var timeoutHttp = new HttpClient(new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new BlockingContent() })) { BaseAddress = new Uri("https://fake.test"), Timeout = Timeout.InfiniteTimeSpan };
        var timeout = new PriceCatalogDownloadClient(timeoutHttp, startGate: new ImmediateStartGate(), attemptTimeout: TimeSpan.FromMilliseconds(25));
        var error = await Assert.ThrowsAsync<PriceCatalogDownloadException>(() => timeout.GetRootIdsAsync(CancellationToken.None));
        Assert.Equal(PriceCatalogDownloadFailureKind.Timeout, error.Kind);
        Assert.Equal(2, timeout.AttemptCount);
    }

    private static PriceCatalogPrice Price(long id, long buyQuantity = 2, long buyUnitPrice = 3) => new(id, true, buyQuantity, buyUnitPrice, 2, 3);

    private sealed class FakePriceClient(IEnumerable<PriceCatalogPrice> root, int? failingBatchIndex = null, Func<long[], IReadOnlyList<PriceCatalogPrice>>? response = null) : IPriceCatalogDownloadClient
    {
        private readonly PriceCatalogPrice[] root = root.ToArray();
        public int RootCalls { get; private set; }
        public int AttemptCount => 1;
        public List<long[]> Batches { get; } = [];
        public Task<IReadOnlyList<long>> GetRootIdsAsync(CancellationToken cancellationToken) { RootCalls++; return Task.FromResult<IReadOnlyList<long>>(root.Select(price => price.Id).ToArray()); }
        public Task<IReadOnlyList<PriceCatalogPrice>> GetPricesAsync(IReadOnlyCollection<long> requestedIds, CancellationToken cancellationToken)
        {
            var batch = requestedIds.Order().ToArray();
            Batches.Add(batch);
            if (failingBatchIndex == (batch[0] - 1) / 200) throw new PriceCatalogDownloadException();
            return Task.FromResult(response?.Invoke(batch) ?? root.Where(price => batch.Contains(price.Id)).Reverse().ToArray());
        }
    }

    private sealed class DirectoryFixture : IDisposable
    {
        private DirectoryFixture(string path) => Path = path;
        public string Path { get; }
        public static DirectoryFixture Create(string name = "cache") { var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GW2AccountMCP.Tests", Guid.NewGuid().ToString("N"), name); Directory.CreateDirectory(path); return new(path); }
        public void Dispose() { var root = System.IO.Path.GetDirectoryName(Path)!; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class SequenceTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private int index;
        public override DateTimeOffset GetUtcNow() => values[Math.Min(Interlocked.Increment(ref index) - 1, values.Length - 1)];
    }
    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request)); }
    private sealed class ImmediateStartGate : IApiStartGate { public Task WaitAsync(CancellationToken cancellationToken) => Task.CompletedTask; }
    private sealed class CountingGate : IApiStartGate { public int Calls { get; private set; } public Task WaitAsync(CancellationToken cancellationToken) { Calls++; return Task.CompletedTask; } }
    private sealed class CountingLeaseFactory : IUpdaterLeaseFactory { public int Calls { get; private set; } public IDisposable Acquire() { Calls++; return new MemoryStream(); } }
    private sealed class RecordingService : IPriceCacheRefreshService { public int Calls { get; private set; } public Task<PriceRefreshSummary> RefreshAsync(string outputDirectory, bool fresh, CancellationToken cancellationToken) { Calls++; return Task.FromResult(new PriceRefreshSummary(1, 0, "x", "x")); } public Task<PriceRefreshSummary> RefreshTestAsync(string outputDirectory, CancellationToken cancellationToken) { Calls++; return Task.FromResult(new PriceRefreshSummary(1, 0, "x", "x", true)); } }
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<(TimerCallback Callback, object? State, DateTimeOffset Due)> timers = []; private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) { timers.Add((callback, state, now + dueTime)); return new NoopTimer(); }
        public void Advance(TimeSpan elapsed) { now += elapsed; foreach (var timer in timers.Where(timer => timer.Due <= now).ToArray()) { timers.Remove(timer); timer.Callback(timer.State); } }
        private sealed class NoopTimer : ITimer { public bool Change(TimeSpan dueTime, TimeSpan period) => true; public void Dispose() { } public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
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
