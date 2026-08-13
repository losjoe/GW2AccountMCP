using System.Net;
using System.Text;
using GW2AccountMCP.Gw2;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class Gw2ApiClientTests
{
    [Fact]
    public async Task GetAccountAsync_missing_key_is_actionable_and_makes_no_request()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(string.Empty, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestUris);
    }

    [Fact]
    public async Task GetAccountAsync_validates_account_permission_before_mapping_account_response()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            """{"permissions":["account","wallet"]}""",
            """{"name":"Example.1234","world":2206,"created":"2020-01-02T03:04:05Z","access":["GuildWars2","PathOfFire"]}""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var account = await client.GetAccountAsync(CancellationToken.None);

        Assert.Equal("Example.1234", account.Name);
        Assert.Equal(2206, account.World);
        Assert.Equal(DateTimeOffset.Parse("2020-01-02T03:04:05Z"), account.Created);
        Assert.Equal(["GuildWars2", "PathOfFire"], account.Access);
        Assert.Equal(["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/account?lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
        Assert.All(handler.AuthorizationHeaders, value => Assert.Equal($"Bearer {apiKey}", value));
    }

    [Fact]
    public async Task GetAccountAsync_missing_account_permission_is_actionable_and_redacted()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler("""{"permissions":["wallet"]}""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountAsync(CancellationToken.None));

        Assert.Contains("account permission", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task GetAccountAsync_invalid_key_error_is_actionable_and_redacted()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler("Invalid key", HttpStatusCode.Unauthorized, "Invalid key", HttpStatusCode.Unauthorized);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetAccountAsync_does_not_retry_non_invalid_key_unauthorized_response()
    {
        var handler = new RecordingHandler("Permission denied", HttpStatusCode.Forbidden);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountAsync(CancellationToken.None));

        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task GetAccountAsync_retries_a_transient_token_info_response()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("", HttpStatusCode.ServiceUnavailable),
            new ResponseSpec("""{"permissions":["account"]}"""),
            new ResponseSpec("""{"name":"Example.1234","world":2206,"created":"2020-01-02T03:04:05Z","access":[]}"""));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var account = await client.GetAccountAsync(CancellationToken.None);

        Assert.Equal("Example.1234", account.Name);
        Assert.Equal(["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/account?lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
    }

    [Fact]
    public async Task GetAccountAsync_retries_a_transient_account_response()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account"]}"""),
            new ResponseSpec("", HttpStatusCode.BadGateway),
            new ResponseSpec("""{"name":"Example.1234","world":2206,"created":"2020-01-02T03:04:05Z","access":[]}"""));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var account = await client.GetAccountAsync(CancellationToken.None);

        Assert.Equal("Example.1234", account.Name);
        Assert.Equal(["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/account?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/account?lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
    }

    [Fact]
    public async Task GetAccountAsync_stops_after_one_transient_retry()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account"]}"""),
            new ResponseSpec("", HttpStatusCode.GatewayTimeout),
            new ResponseSpec("", HttpStatusCode.GatewayTimeout));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountAsync(CancellationToken.None));

        Assert.Contains("HTTP 504", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetAccountAsync_honors_retry_after_without_waiting_in_the_test()
    {
        var timeProvider = new ImmediateTimeProvider();
        var handler = new RecordingHandler(
            new ResponseSpec("", HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(2)),
            new ResponseSpec("""{"permissions":["account"]}"""),
            new ResponseSpec("""{"name":"Example.1234","world":2206,"created":"2020-01-02T03:04:05Z","access":[]}"""));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"), timeProvider);

        await client.GetAccountAsync(CancellationToken.None);

        Assert.Equal(3, handler.RequestUris.Count);
        Assert.Equal([TimeSpan.FromSeconds(2)], timeProvider.RequestedDelays);
    }

    [Fact]
    public async Task GetAccountAsync_retries_an_invalid_key_marker_from_account()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account"]}"""),
            new ResponseSpec("Invalid key", HttpStatusCode.Unauthorized),
            new ResponseSpec("""{"name":"Example.1234","world":2206,"created":"2020-01-02T03:04:05Z","access":[]}"""));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var account = await client.GetAccountAsync(CancellationToken.None);

        Assert.Equal("Example.1234", account.Name);
        Assert.Equal(3, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetAccountAsync_does_not_retry_an_ordinary_account_auth_failure()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account"]}"""),
            new ResponseSpec("Permission denied", HttpStatusCode.Forbidden));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountAsync(CancellationToken.None));

        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":null,\"world\":null,\"created\":null,\"access\":null}")]
    [InlineData("{malformed")]
    public async Task GetAccountAsync_rejects_incomplete_or_malformed_account_responses(string accountResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account"]}"""),
            new ResponseSpec(accountResponse));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountAsync(CancellationToken.None));

        Assert.Contains("account response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(accountResponse, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"permissions\":null}")]
    [InlineData("{malformed")]
    public async Task GetAccountAsync_rejects_incomplete_or_malformed_token_permission_responses(string tokenResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(new ResponseSpec(tokenResponse));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountAsync(CancellationToken.None));

        Assert.Contains("token-permission response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(tokenResponse, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAccountAsync_propagates_cancellation_during_a_retry()
    {
        using var cancellationSource = new CancellationTokenSource();
        var handler = new RecordingHandler(new ResponseSpec("", HttpStatusCode.ServiceUnavailable))
        {
            OnRequest = cancellationSource.Cancel
        };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAccountAsync(cancellationSource.Token));
    }

    [Fact]
    public async Task GetWalletAsync_joins_currency_names_in_wallet_order_and_uses_canonical_public_request()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","wallet"]}"""),
            new ResponseSpec("""[{"id":2,"value":0},{"id":1,"value":42},{"id":2,"value":7}]"""),
            new ResponseSpec("""[{"id":1,"name":"Coin"},{"id":2,"name":"Karma"}]"""));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var wallet = await client.GetWalletAsync(CancellationToken.None);

        Assert.Equal([(2, "Karma", 0L), (1, "Coin", 42L), (2, "Karma", 7L)], wallet.Balances.Select(balance => (balance.Id, balance.Name, balance.Value)));
        Assert.Empty(wallet.Warnings);
        Assert.Equal(
            ["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/account/wallet?lang=en&v=2025-08-29T01%3A00%3A00.000Z", "/v2/currencies?ids=1%2C2&lang=en&v=2025-08-29T01%3A00%3A00.000Z"],
            handler.RequestUris);
        Assert.Equal(["Bearer " + apiKey, "Bearer " + apiKey, null], handler.AuthorizationHeaders);
    }

    [Fact]
    public async Task GetWalletAsync_missing_or_invalid_key_stops_before_wallet_and_currency_requests()
    {
        var missingKeyHandler = new RecordingHandler();
        using var missingKeyHttpClient = new HttpClient(missingKeyHandler) { BaseAddress = new Uri("https://example.test") };
        var missingKeyClient = new Gw2ApiClient(missingKeyHttpClient, new Gw2ApiOptions(string.Empty, "https://example.test"));

        var missingKeyError = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => missingKeyClient.GetWalletAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", missingKeyError.Message, StringComparison.Ordinal);
        Assert.Empty(missingKeyHandler.RequestUris);

        var apiKey = new string('k', 16);
        var invalidKeyHandler = new RecordingHandler("Invalid key", HttpStatusCode.Unauthorized, "Invalid key", HttpStatusCode.Unauthorized);
        using var invalidKeyHttpClient = new HttpClient(invalidKeyHandler) { BaseAddress = new Uri("https://example.test") };
        var invalidKeyClient = new Gw2ApiClient(invalidKeyHttpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var invalidKeyError = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => invalidKeyClient.GetWalletAsync(CancellationToken.None));

        Assert.DoesNotContain(apiKey, invalidKeyError.Message, StringComparison.Ordinal);
        Assert.Equal(2, invalidKeyHandler.RequestUris.Count);
    }

    [Fact]
    public async Task GetWalletAsync_rejects_malformed_token_permissions_before_downstream_calls()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler("{malformed");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetWalletAsync(CancellationToken.None));

        Assert.Contains("token-permission response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.Single(handler.RequestUris);
    }

    [Theory]
    [InlineData("{\"permissions\":[\"account\"]}", "wallet permission")]
    [InlineData("{\"permissions\":[\"wallet\"]}", "account permission")]
    public async Task GetWalletAsync_requires_each_operation_specific_permission_before_downstream_calls(string tokenResponse, string requiredPermission)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(new ResponseSpec(tokenResponse));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetWalletAsync(CancellationToken.None));

        Assert.Contains(requiredPermission, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.Equal(["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
    }

    [Fact]
    public async Task GetWalletAsync_empty_wallet_skips_currency_metadata()
    {
        var handler = new RecordingHandler("""{"permissions":["account","wallet"]}""", "[]");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var wallet = await client.GetWalletAsync(CancellationToken.None);

        Assert.Empty(wallet.Balances);
        Assert.Empty(wallet.Warnings);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetWalletAsync_retains_balance_and_returns_bounded_warning_when_currency_metadata_is_missing()
    {
        var handler = new RecordingHandler(
            """{"permissions":["account","wallet"]}""",
            """[{"id":1,"value":99},{"id":2,"value":0}]""",
            new ResponseSpec("""[{"id":1,"name":"Coin"}]""", HttpStatusCode.PartialContent));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var wallet = await client.GetWalletAsync(CancellationToken.None);

        Assert.Equal([(1, "Coin", 99L), (2, null, 0L)], wallet.Balances.Select(balance => (balance.Id, balance.Name, balance.Value)));
        var warning = Assert.Single(wallet.Warnings);
        Assert.Equal("currency_metadata_missing", warning.Code);
        Assert.Equal(2, warning.CurrencyId);
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":0,\"value\":1}]")]
    [InlineData("[{\"id\":1,\"value\":-1}]")]
    public async Task GetWalletAsync_rejects_malformed_or_invalid_wallet_responses(string walletResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler("""{"permissions":["account","wallet"]}""", walletResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetWalletAsync(CancellationToken.None));

        Assert.Contains("wallet response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":0,\"name\":\"Coin\"}]")]
    public async Task GetWalletAsync_rejects_malformed_or_invalid_currency_responses(string currencyResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler("""{"permissions":["account","wallet"]}""", """[{"id":1,"value":0}]""", currencyResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetWalletAsync(CancellationToken.None));

        Assert.Contains("currency response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAccountStorageAsync_normalizes_all_sources_without_aggregating_stacks()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            """{"permissions":["account","inventories"]}""",
            """[null,{"id":11,"count":2},{"id":11,"count":3}]""",
            """[{"id":11,"category":1,"count":0},{"id":12,"category":2,"count":4}]""",
            """[{"id":11,"count":1},null,{"id":13,"count":5}]""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var storage = await client.GetAccountStorageAsync(CancellationToken.None);

        Assert.Equal(
            [
                (11, 2L, Gw2StorageSource.Bank, (int?)1),
                (11, 3L, Gw2StorageSource.Bank, (int?)2),
                (11, 0L, Gw2StorageSource.MaterialStorage, null),
                (12, 4L, Gw2StorageSource.MaterialStorage, null),
                (11, 1L, Gw2StorageSource.SharedInventory, (int?)0),
                (13, 5L, Gw2StorageSource.SharedInventory, (int?)2)
            ],
            storage.Stacks.Select(stack => (stack.Id, stack.Count, stack.Source, stack.SlotIndex)));
        Assert.Equal(
            [
                "/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/account/bank?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/account/materials?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/account/inventory?lang=en&v=2025-08-29T01%3A00%3A00.000Z"
            ],
            handler.RequestUris);
        Assert.All(handler.AuthorizationHeaders, value => Assert.Equal($"Bearer {apiKey}", value));
    }

    [Fact]
    public async Task GetAccountStorageAsync_missing_key_is_actionable_and_makes_no_request()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(string.Empty, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountStorageAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestUris);
    }

    [Theory]
    [InlineData("{\"permissions\":[\"account\"]}", "inventories permission")]
    [InlineData("{\"permissions\":[\"inventories\"]}", "account permission")]
    public async Task GetAccountStorageAsync_requires_each_permission_before_storage_requests(string tokenResponse, string requiredPermission)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(tokenResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountStorageAsync(CancellationToken.None));

        Assert.Contains(requiredPermission, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.Equal(["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
    }

    [Fact]
    public async Task GetAccountStorageAsync_accepts_empty_sources()
    {
        var handler = new RecordingHandler("""{"permissions":["account","inventories"]}""", "[]", "[]", "[]");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var storage = await client.GetAccountStorageAsync(CancellationToken.None);

        Assert.Empty(storage.Stacks);
        Assert.Equal(4, handler.RequestUris.Count);
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("null")]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":0,\"count\":1}]")]
    [InlineData("[{\"id\":1,\"count\":0}]")]
    [InlineData("[{\"id\":1,\"count\":-1}]")]
    public async Task GetAccountStorageAsync_rejects_invalid_bank_responses(string bankResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler("""{"permissions":["account","inventories"]}""", bankResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountStorageAsync(CancellationToken.None));

        Assert.Contains("bank response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(bankResponse, error.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":1,\"count\":0}]")]
    [InlineData("[{\"id\":0,\"category\":1,\"count\":0}]")]
    [InlineData("[{\"id\":1,\"category\":1,\"count\":-1}]")]
    [InlineData("[{\"id\":1,\"category\":1,\"count\":0},{\"id\":1,\"category\":1,\"count\":2}]")]
    public async Task GetAccountStorageAsync_rejects_invalid_material_responses(string materialResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler("""{"permissions":["account","inventories"]}""", "[]", materialResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountStorageAsync(CancellationToken.None));

        Assert.Contains("material-storage response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(materialResponse, error.Message, StringComparison.Ordinal);
        Assert.Equal(3, handler.RequestUris.Count);
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("null")]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":0,\"count\":1}]")]
    [InlineData("[{\"id\":1,\"count\":0}]")]
    [InlineData("[{\"id\":1,\"count\":-1}]")]
    public async Task GetAccountStorageAsync_rejects_invalid_shared_inventory_responses(string inventoryResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler("""{"permissions":["account","inventories"]}""", "[]", "[]", inventoryResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountStorageAsync(CancellationToken.None));

        Assert.Contains("shared-inventory response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(inventoryResponse, error.Message, StringComparison.Ordinal);
        Assert.Equal(4, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetAccountStorageAsync_source_failure_is_total_and_does_not_continue()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","inventories"]}"""),
            new ResponseSpec("[]"),
            new ResponseSpec("account data must not appear in the error", HttpStatusCode.InternalServerError));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetAccountStorageAsync(CancellationToken.None));

        Assert.Contains("material-storage request failed with HTTP 500", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("account data", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetAccountStorageAsync_reuses_authenticated_single_retry_for_storage_sources()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","inventories"]}"""),
            new ResponseSpec("", HttpStatusCode.ServiceUnavailable),
            new ResponseSpec("[]"),
            new ResponseSpec("[]"),
            new ResponseSpec("[]"));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var storage = await client.GetAccountStorageAsync(CancellationToken.None);

        Assert.Empty(storage.Stacks);
        Assert.Equal(5, handler.RequestUris.Count);
        Assert.Equal(2, handler.RequestUris.Count(uri => uri.StartsWith("/v2/account/bank?", StringComparison.Ordinal)));
        Assert.All(handler.AuthorizationHeaders, value => Assert.Equal($"Bearer {apiKey}", value));
    }

    [Fact]
    public async Task GetCharacterBagsAsync_missing_key_is_actionable_and_makes_no_request()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(string.Empty, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharacterBagsAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestUris);
    }

    [Theory]
    [InlineData("{\"permissions\":[\"characters\",\"inventories\"]}", "account permission")]
    [InlineData("{\"permissions\":[\"account\",\"inventories\"]}", "characters permission")]
    [InlineData("{\"permissions\":[\"account\",\"characters\"]}", "inventories permission")]
    public async Task GetCharacterBagsAsync_requires_each_permission_before_character_requests(string tokenResponse, string requiredPermission)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(tokenResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharacterBagsAsync(CancellationToken.None));

        Assert.Contains(requiredPermission, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.Equal(["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
    }

    [Fact]
    public async Task GetCharacterBagsAsync_traverses_every_character_and_preserves_stack_locations()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            """{"permissions":["account","characters","inventories"]}""",
            """["First Hero","Path/Query?# Hero","Last Hero"]""",
            """{"bags":[null,{"id":901,"size":3,"inventory":[null,{"id":11,"count":2},{"id":11,"count":3}]}]}""",
            """{"bags":[{"id":902,"size":1,"inventory":[{"id":11,"count":4}]}]}""",
            """{"bags":[{"id":903,"size":2,"inventory":[null,{"id":12,"count":5}]}]}""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var bags = await client.GetCharacterBagsAsync(CancellationToken.None);

        Assert.Equal(
            [
                (11, 2L, "First Hero", 1, 1),
                (11, 3L, "First Hero", 1, 2),
                (11, 4L, "Path/Query?# Hero", 0, 0),
                (12, 5L, "Last Hero", 0, 1)
            ],
            bags.Stacks.Select(stack => (stack.Id, stack.Count, stack.Character, stack.BagIndex, stack.SlotIndex)));
        Assert.Equal(
            [
                "/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/characters?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/characters/First%20Hero/inventory?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/characters/Path%2FQuery%3F%23%20Hero/inventory?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/characters/Last%20Hero/inventory?lang=en&v=2025-08-29T01%3A00%3A00.000Z"
            ],
            handler.RequestUris);
        Assert.All(handler.AuthorizationHeaders, value => Assert.Equal($"Bearer {apiKey}", value));
    }

    [Fact]
    public async Task GetCharacterBagsAsync_accepts_empty_characters_bags_and_slots()
    {
        var emptyCharactersHandler = new RecordingHandler(
            """{"permissions":["account","characters","inventories"]}""",
            "[]");
        using var emptyCharactersHttpClient = new HttpClient(emptyCharactersHandler) { BaseAddress = new Uri("https://example.test") };
        var emptyCharactersClient = new Gw2ApiClient(emptyCharactersHttpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var emptyCharacters = await emptyCharactersClient.GetCharacterBagsAsync(CancellationToken.None);

        Assert.Empty(emptyCharacters.Stacks);
        Assert.Equal(2, emptyCharactersHandler.RequestUris.Count);

        var emptyBagsHandler = new RecordingHandler(
            """{"permissions":["account","characters","inventories"]}""",
            """["Empty Bags","Empty Slots"]""",
            """{"bags":[]}""",
            """{"bags":[{"id":904,"size":2,"inventory":[null,null]}]}""");
        using var emptyBagsHttpClient = new HttpClient(emptyBagsHandler) { BaseAddress = new Uri("https://example.test") };
        var emptyBagsClient = new Gw2ApiClient(emptyBagsHttpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var emptyBags = await emptyBagsClient.GetCharacterBagsAsync(CancellationToken.None);

        Assert.Empty(emptyBags.Stacks);
        Assert.Equal(4, emptyBagsHandler.RequestUris.Count);
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"Duplicate Hero\",\"Duplicate Hero\"]")]
    public async Task GetCharacterBagsAsync_rejects_invalid_character_lists(string characterResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            """{"permissions":["account","characters","inventories"]}""",
            characterResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharacterBagsAsync(CancellationToken.None));

        Assert.Contains("character-list response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(characterResponse, error.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"bags\":null}")]
    [InlineData("{\"bags\":[{}]}")]
    [InlineData("{\"bags\":[{\"id\":0,\"size\":1,\"inventory\":[null]}]}")]
    [InlineData("{\"bags\":[{\"id\":1,\"size\":0,\"inventory\":[]}]}")]
    [InlineData("{\"bags\":[{\"id\":1,\"size\":1,\"inventory\":null}]}")]
    [InlineData("{\"bags\":[{\"id\":1,\"size\":2,\"inventory\":[null]}]}")]
    [InlineData("{\"bags\":[{\"id\":1,\"size\":1,\"inventory\":[{}]}]}")]
    [InlineData("{\"bags\":[{\"id\":1,\"size\":1,\"inventory\":[{\"id\":0,\"count\":1}]}]}")]
    [InlineData("{\"bags\":[{\"id\":1,\"size\":1,\"inventory\":[{\"id\":2,\"count\":0}]}]}")]
    public async Task GetCharacterBagsAsync_rejects_invalid_character_inventory(string inventoryResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            """{"permissions":["account","characters","inventories"]}""",
            """["Synthetic Hero"]""",
            inventoryResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharacterBagsAsync(CancellationToken.None));

        Assert.Contains("character-inventory response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(inventoryResponse, error.Message, StringComparison.Ordinal);
        Assert.Equal(3, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetCharacterBagsAsync_character_failure_is_total_and_stops_sequential_traversal()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","characters","inventories"]}"""),
            new ResponseSpec("""["First Hero","Failed Hero","Unrequested Hero"]"""),
            new ResponseSpec("""{"bags":[{"id":901,"size":1,"inventory":[{"id":11,"count":2}]}]}"""),
            new ResponseSpec("account data must not appear in the error", HttpStatusCode.InternalServerError));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharacterBagsAsync(CancellationToken.None));

        Assert.Contains("character-inventory request failed with HTTP 500", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("account data", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, handler.RequestUris.Count);
        Assert.DoesNotContain(handler.RequestUris, uri => uri.Contains("Unrequested", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetCharacterBagsAsync_rejects_partial_character_list()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","characters","inventories"]}"""),
            new ResponseSpec("""["Partial Hero"]""", HttpStatusCode.PartialContent));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharacterBagsAsync(CancellationToken.None));

        Assert.Contains("character-list request failed with HTTP 206", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetCharacterBagsAsync_rejects_partial_character_inventory_and_stops_traversal()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","characters","inventories"]}"""),
            new ResponseSpec("""["Partial Hero","Unrequested Hero"]"""),
            new ResponseSpec("""{"bags":[]}""", HttpStatusCode.PartialContent));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetCharacterBagsAsync(CancellationToken.None));

        Assert.Contains("character-inventory request failed with HTTP 206", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, handler.RequestUris.Count);
        Assert.DoesNotContain(handler.RequestUris, uri => uri.Contains("Unrequested", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetCharacterBagsAsync_reuses_authenticated_single_retry_for_character_inventory()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","characters","inventories"]}"""),
            new ResponseSpec("""["Retry Hero"]"""),
            new ResponseSpec("", HttpStatusCode.ServiceUnavailable),
            new ResponseSpec("""{"bags":[]}"""));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var bags = await client.GetCharacterBagsAsync(CancellationToken.None);

        Assert.Empty(bags.Stacks);
        Assert.Equal(4, handler.RequestUris.Count);
        Assert.Equal(2, handler.RequestUris.Count(uri => uri.StartsWith("/v2/characters/Retry%20Hero/inventory?", StringComparison.Ordinal)));
        Assert.All(handler.AuthorizationHeaders, value => Assert.Equal($"Bearer {apiKey}", value));
    }

    [Fact]
    public async Task GetTradingPostDeliveryAsync_missing_key_is_actionable_and_makes_no_request()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(string.Empty, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetTradingPostDeliveryAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", error.Message, StringComparison.Ordinal);
        Assert.Empty(handler.RequestUris);
    }

    [Theory]
    [InlineData("{\"permissions\":[\"tradingpost\"]}", "account permission")]
    [InlineData("{\"permissions\":[\"account\"]}", "tradingpost permission")]
    public async Task GetTradingPostDeliveryAsync_requires_each_permission_before_delivery_request(string tokenResponse, string requiredPermission)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(tokenResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetTradingPostDeliveryAsync(CancellationToken.None));

        Assert.Contains(requiredPermission, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.Equal(["/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z"], handler.RequestUris);
    }

    [Fact]
    public async Task GetTradingPostDeliveryAsync_normalizes_complete_delivery_without_aggregating_items()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            """{"permissions":["account","tradingpost"]}""",
            """{"coins":4294967295,"items":[{"id":101,"count":2},{"id":101,"count":4294967296},{"id":202,"count":3}]}""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var delivery = await client.GetTradingPostDeliveryAsync(CancellationToken.None);

        Assert.Equal(4294967295L, delivery.Coins);
        Assert.Equal([(101L, 2L), (101L, 4294967296L), (202L, 3L)], delivery.Items.Select(item => (item.Id, item.Count)));
        Assert.Equal(
            [
                "/v2/tokeninfo?lang=en&v=2025-08-29T01%3A00%3A00.000Z",
                "/v2/commerce/delivery?lang=en&v=2025-08-29T01%3A00%3A00.000Z"
            ],
            handler.RequestUris);
        Assert.All(handler.AuthorizationHeaders, value => Assert.Equal($"Bearer {apiKey}", value));
    }

    [Fact]
    public async Task GetTradingPostDeliveryAsync_accepts_zero_coins_and_empty_items()
    {
        var handler = new RecordingHandler(
            """{"permissions":["account","tradingpost"]}""",
            """{"coins":0,"items":[]}""");
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var delivery = await client.GetTradingPostDeliveryAsync(CancellationToken.None);

        Assert.Equal(0L, delivery.Coins);
        Assert.Empty(delivery.Items);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Theory]
    [InlineData("{malformed")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"coins\":null,\"items\":[]}")]
    [InlineData("{\"coins\":-1,\"items\":[]}")]
    [InlineData("{\"coins\":0}")]
    [InlineData("{\"coins\":0,\"items\":null}")]
    [InlineData("{\"coins\":0,\"items\":[null]}")]
    [InlineData("{\"coins\":0,\"items\":[{}]}")]
    [InlineData("{\"coins\":0,\"items\":[{\"id\":0,\"count\":1}]}")]
    [InlineData("{\"coins\":0,\"items\":[{\"id\":-1,\"count\":1}]}")]
    [InlineData("{\"coins\":0,\"items\":[{\"id\":1,\"count\":0}]}")]
    [InlineData("{\"coins\":0,\"items\":[{\"id\":1,\"count\":-1}]}")]
    [InlineData("{\"coins\":0,\"items\":[{\"id\":\"1\",\"count\":1}]}")]
    [InlineData("{\"coins\":0,\"items\":[{\"id\":1,\"count\":\"1\"}]}")]
    public async Task GetTradingPostDeliveryAsync_rejects_malformed_or_invalid_delivery_responses(string deliveryResponse)
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            """{"permissions":["account","tradingpost"]}""",
            deliveryResponse);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetTradingPostDeliveryAsync(CancellationToken.None));

        Assert.Contains("delivery response", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(deliveryResponse, error.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetTradingPostDeliveryAsync_rejects_partial_content_even_when_payload_is_valid()
    {
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
            new ResponseSpec("""{"coins":0,"items":[]}""", HttpStatusCode.PartialContent));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(new string('k', 16), "https://example.test"));

        var error = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => client.GetTradingPostDeliveryAsync(CancellationToken.None));

        Assert.Contains("delivery request failed with HTTP 206", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.RequestUris.Count);
    }

    [Fact]
    public async Task GetTradingPostDeliveryAsync_reuses_authenticated_single_retry_for_delivery()
    {
        var apiKey = new string('k', 16);
        var handler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
            new ResponseSpec("", HttpStatusCode.ServiceUnavailable),
            new ResponseSpec("""{"coins":0,"items":[]}"""));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.test") };
        var client = new Gw2ApiClient(httpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var delivery = await client.GetTradingPostDeliveryAsync(CancellationToken.None);

        Assert.Empty(delivery.Items);
        Assert.Equal(3, handler.RequestUris.Count);
        Assert.Equal(2, handler.RequestUris.Count(uri => uri.StartsWith("/v2/commerce/delivery?", StringComparison.Ordinal)));
        Assert.All(handler.AuthorizationHeaders, value => Assert.Equal($"Bearer {apiKey}", value));
    }

    [Fact]
    public async Task GetTradingPostDeliveryAsync_maps_auth_and_http_failures_without_exposing_response_content()
    {
        var apiKey = new string('k', 16);
        var authHandler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
            new ResponseSpec("private delivery content", HttpStatusCode.Forbidden));
        using var authHttpClient = new HttpClient(authHandler) { BaseAddress = new Uri("https://example.test") };
        var authClient = new Gw2ApiClient(authHttpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var authError = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => authClient.GetTradingPostDeliveryAsync(CancellationToken.None));

        Assert.Contains("GW2_API_KEY", authError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, authError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private delivery content", authError.Message, StringComparison.Ordinal);
        Assert.Equal(2, authHandler.RequestUris.Count);

        var httpHandler = new RecordingHandler(
            new ResponseSpec("""{"permissions":["account","tradingpost"]}"""),
            new ResponseSpec("private delivery content", HttpStatusCode.InternalServerError));
        using var failureHttpClient = new HttpClient(httpHandler) { BaseAddress = new Uri("https://example.test") };
        var failureClient = new Gw2ApiClient(failureHttpClient, new Gw2ApiOptions(apiKey, "https://example.test"));

        var httpError = await Assert.ThrowsAsync<Gw2ConfigurationException>(() => failureClient.GetTradingPostDeliveryAsync(CancellationToken.None));

        Assert.Contains("delivery request failed with HTTP 500", httpError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(apiKey, httpError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private delivery content", httpError.Message, StringComparison.Ordinal);
        Assert.Equal(2, httpHandler.RequestUris.Count);
    }

    private sealed record ResponseSpec(string Content, HttpStatusCode StatusCode = HttpStatusCode.OK, TimeSpan? RetryAfter = null);

    private sealed class ImmediateTimeProvider : TimeProvider
    {
        public List<TimeSpan> RequestedDelays { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            RequestedDelays.Add(dueTime);
            callback(state);
            return new ImmediateTimer();
        }
    }

    private sealed class ImmediateTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingHandler(params object[] responses) : HttpMessageHandler
    {
        private readonly Queue<object> responses = new(responses);

        public List<string> RequestUris { get; } = [];
        public List<string?> AuthorizationHeaders { get; } = [];
        public Action? OnRequest { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.PathAndQuery);
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());
            OnRequest?.Invoke();
            var response = responses.Dequeue() switch
            {
                string content => new ResponseSpec(content, responses.Count > 0 && responses.Peek() is HttpStatusCode status ? (HttpStatusCode)responses.Dequeue() : HttpStatusCode.OK),
                ResponseSpec specification => specification,
                _ => throw new InvalidOperationException("Unsupported test response.")
            };
            var message = new HttpResponseMessage(response.StatusCode) { Content = new StringContent(response.Content, Encoding.UTF8, "application/json") };
            if (response.RetryAfter is { } retryAfter)
            {
                message.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
            }

            return Task.FromResult(message);
        }
    }
}
