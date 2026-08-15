using GW2AccountMCP.Gw2;
using GW2AccountMCP.Tools;
using ModelContextProtocol;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class GetAccountHoldingsTests
{
    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task GetAccountHoldingsAsync_rejects_invalid_arguments_before_client_calls(long[]? itemIds, int[]? currencyIds)
    {
        var client = new FakeGw2ApiClient();
        var tool = new GetAccountHoldingsTool(client, TimeProvider.System);

        var error = await Assert.ThrowsAsync<McpException>(() =>
            tool.GetAccountHoldingsAsync(itemIds, currencyIds, CancellationToken.None));

        Assert.Contains("ID", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(client.Calls);
    }

    public static TheoryData<long[]?, int[]?> InvalidArguments => new()
    {
        { null, null },
        { [], [] },
        { [0], null },
        { [-1], null },
        { [1, 1], null },
        { null, [0] },
        { null, [-1] },
        { null, [1, 1] },
        { Enumerable.Range(1, 21).Select(value => (long)value).ToArray(), null },
        { Enumerable.Range(1, 11).Select(value => (long)value).ToArray(), Enumerable.Range(1, 10).ToArray() }
    };

    [Fact]
    public async Task GetAccountHoldingsAsync_item_only_aggregates_all_sources_in_caller_and_location_order()
    {
        var client = new FakeGw2ApiClient
        {
            AccountStorage = new Gw2AccountStorage(
            [
                new Gw2StorageStack(101, 2, Gw2StorageSource.Bank, 0),
                new Gw2StorageStack(101, 3, Gw2StorageSource.Bank, 1),
                new Gw2StorageStack(101, 0, Gw2StorageSource.MaterialStorage, null),
                new Gw2StorageStack(101, 1, Gw2StorageSource.SharedInventory, 0)
            ]),
            CharacterBags = new Gw2CharacterBags(
            [
                new Gw2CharacterBagStack(101, 4, "First Hero", 0, 0),
                new Gw2CharacterBagStack(101, 5, "Later Hero", 0, 0)
            ]),
            Delivery = new Gw2TradingPostDelivery(999, [new Gw2TradingPostDeliveryItem(101, 6), new Gw2TradingPostDeliveryItem(101, 7)]),
            CurrentSells = new Gw2CurrentSells(
            [
                new Gw2CurrentSellOrder(1, 101, 5000, 8, DateTimeOffset.Parse("2026-01-01T00:00:00Z")),
                new Gw2CurrentSellOrder(2, 101, 1, 9, DateTimeOffset.Parse("2026-01-02T00:00:00Z"))
            ]),
            Items = new Gw2Items([new Gw2Item(101, "Synthetic Item"), new Gw2Item(202, "Absent Item")], [])
        };
        var timeProvider = new RecordingTimeProvider(DateTimeOffset.Parse("2026-08-13T12:00:00Z"), client);
        var tool = new GetAccountHoldingsTool(client, timeProvider);

        var result = await tool.GetAccountHoldingsAsync([202, 101], null, CancellationToken.None);

        Assert.Equal([202L, 101L], result.Items.Select(item => item.Id));
        var absent = result.Items[0];
        Assert.Equal("Absent Item", absent.Name);
        Assert.Equal(0, absent.OnHand);
        Assert.Equal(0, absent.InTradingPostDelivery);
        Assert.Equal(0, absent.ListedForSale);
        Assert.Equal(0, absent.OwnedTotal);
        Assert.Empty(absent.Locations);

        var item = result.Items[1];
        Assert.Equal("Synthetic Item", item.Name);
        Assert.Equal(15, item.OnHand);
        Assert.Equal(13, item.InTradingPostDelivery);
        Assert.Equal(17, item.ListedForSale);
        Assert.Equal(45, item.OwnedTotal);
        Assert.Equal(
        [
            ("Bank", 5L, (string?)null),
            ("SharedInventory", 1L, null),
            ("CharacterBag", 4L, "First Hero"),
            ("CharacterBag", 5L, "Later Hero"),
            ("TradingPostDelivery", 13L, null),
            ("TradingPostSell", 17L, null)
        ], item.Locations.Select(location => (location.Kind, location.Count, location.Character)));
        Assert.True(result.IsComplete);
        Assert.Equal(
            ["Bank", "MaterialStorage", "SharedInventory", "CharacterBag", "TradingPostDelivery", "TradingPostSell"],
            result.QueriedLocations);
        Assert.Empty(result.UnavailableLocations);
        Assert.Empty(result.Warnings);
        Assert.Equal(DateTimeOffset.Parse("2026-08-13T12:00:00Z"), result.AsOf);
        Assert.Equal(["storage", "bags", "delivery", "sells", "items", "time"], client.Calls);
    }

    [Fact]
    public async Task GetAccountHoldingsAsync_currency_only_uses_wallet_and_preserves_explicit_zero()
    {
        var client = new FakeGw2ApiClient
        {
            Wallet = new Gw2Wallet(
            [
                new Gw2WalletBalance(2, "Second Currency", 0),
                new Gw2WalletBalance(1, "First Currency", 42)
            ], [])
        };
        var tool = new GetAccountHoldingsTool(client, TimeProvider.System);

        var result = await tool.GetAccountHoldingsAsync(null, [1, 2], CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal([1, 2], result.Currencies.Select(currency => currency.Id));
        Assert.Equal((42L, 42L, "First Currency"), (result.Currencies[0].OnHand, result.Currencies[0].OwnedTotal, result.Currencies[0].Name));
        var walletLocation = Assert.Single(result.Currencies[0].Locations);
        Assert.Equal(("Wallet", 42L), (walletLocation.Kind, walletLocation.Count));
        Assert.Equal((0L, 0L, "Second Currency"), (result.Currencies[1].OnHand, result.Currencies[1].OwnedTotal, result.Currencies[1].Name));
        Assert.Empty(result.Currencies[1].Locations);
        Assert.True(result.IsComplete);
        Assert.Equal(["Wallet"], result.QueriedLocations);
        Assert.Equal(["wallet"], client.Calls.Take(1));
        Assert.DoesNotContain(client.Calls, call => call is "storage" or "bags" or "delivery" or "sells" or "items");
    }

    [Fact]
    public async Task GetAccountHoldingsAsync_keeps_item_and_currency_namespaces_separate()
    {
        var client = new FakeGw2ApiClient
        {
            AccountStorage = new Gw2AccountStorage([new Gw2StorageStack(7, 3, Gw2StorageSource.Bank, 0)]),
            Wallet = new Gw2Wallet([new Gw2WalletBalance(7, "Currency Seven", 11)], []),
            Items = new Gw2Items([new Gw2Item(7, "Item Seven")], [])
        };
        var tool = new GetAccountHoldingsTool(client, TimeProvider.System);

        var result = await tool.GetAccountHoldingsAsync([7], [7], CancellationToken.None);

        Assert.Equal(3, Assert.Single(result.Items).OnHand);
        Assert.Equal(3, Assert.Single(result.Items).OwnedTotal);
        Assert.Equal(11, Assert.Single(result.Currencies).OnHand);
        Assert.Equal(11, Assert.Single(result.Currencies).OwnedTotal);
    }

    [Fact]
    public async Task GetAccountHoldingsAsync_absent_currency_is_unknown_with_warning()
    {
        var client = new FakeGw2ApiClient { Wallet = new Gw2Wallet([], []) };
        var tool = new GetAccountHoldingsTool(client, TimeProvider.System);

        var result = await tool.GetAccountHoldingsAsync(null, [99], CancellationToken.None);

        var currency = Assert.Single(result.Currencies);
        Assert.Null(currency.Name);
        Assert.Null(currency.OnHand);
        Assert.Null(currency.OwnedTotal);
        Assert.Empty(currency.Locations);
        Assert.False(result.IsComplete);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("currency_balance_missing", warning.Code);
        Assert.Equal(99, warning.CurrencyId);
    }

    [Fact]
    public async Task GetAccountHoldingsAsync_preserves_requested_currency_metadata_warning_without_marking_balance_incomplete()
    {
        var client = new FakeGw2ApiClient
        {
            Wallet = new Gw2Wallet(
                [new Gw2WalletBalance(1, null, 5)],
                [new Gw2WalletWarning("currency_metadata_missing", 1), new Gw2WalletWarning("currency_metadata_missing", 2)])
        };
        var tool = new GetAccountHoldingsTool(client, TimeProvider.System);

        var result = await tool.GetAccountHoldingsAsync(null, [1], CancellationToken.None);

        var currency = Assert.Single(result.Currencies);
        Assert.Null(currency.Name);
        Assert.Equal(5, currency.OwnedTotal);
        Assert.True(result.IsComplete);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("currency_metadata_missing", warning.Code);
        Assert.Equal(1, warning.CurrencyId);
    }

    [Fact]
    public async Task GetAccountHoldingsAsync_storage_failure_preserves_known_physical_and_trading_post_facts_without_subtotals()
    {
        var client = new FakeGw2ApiClient
        {
            StorageError = new Gw2ConfigurationException("private storage response"),
            CharacterBags = new Gw2CharacterBags([new Gw2CharacterBagStack(1, 2, "Synthetic Hero", 0, 0)]),
            Delivery = new Gw2TradingPostDelivery(0, [new Gw2TradingPostDeliveryItem(1, 3)]),
            CurrentSells = new Gw2CurrentSells([new Gw2CurrentSellOrder(1, 1, 999, 4, DateTimeOffset.Parse("2026-01-01T00:00:00Z"))]),
            Items = new Gw2Items([new Gw2Item(1, "Synthetic Item")], [])
        };
        var tool = new GetAccountHoldingsTool(client, TimeProvider.System);

        var result = await tool.GetAccountHoldingsAsync([1], null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Null(item.OnHand);
        Assert.Equal(3, item.InTradingPostDelivery);
        Assert.Equal(4, item.ListedForSale);
        Assert.Null(item.OwnedTotal);
        Assert.Equal([("CharacterBag", 2L), ("TradingPostDelivery", 3L), ("TradingPostSell", 4L)], item.Locations.Select(location => (location.Kind, location.Count)));
        Assert.Equal(["Bank", "MaterialStorage", "SharedInventory"], result.UnavailableLocations);
        Assert.DoesNotContain("private storage response", string.Join(' ', result.Warnings.Select(warning => warning.Message)), StringComparison.Ordinal);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task GetAccountHoldingsAsync_character_failure_preserves_storage_locations_without_on_hand_subtotal()
    {
        var client = new FakeGw2ApiClient
        {
            AccountStorage = new Gw2AccountStorage([new Gw2StorageStack(1, 5, Gw2StorageSource.MaterialStorage, null)]),
            CharacterError = new Gw2ConfigurationException("private character response"),
            Items = new Gw2Items([new Gw2Item(1, "Synthetic Item")], [])
        };
        var tool = new GetAccountHoldingsTool(client, TimeProvider.System);

        var result = await tool.GetAccountHoldingsAsync([1], null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Null(item.OnHand);
        Assert.Null(item.OwnedTotal);
        Assert.Equal(("MaterialStorage", 5L), (Assert.Single(item.Locations).Kind, Assert.Single(item.Locations).Count));
        Assert.Equal(["CharacterBag"], result.UnavailableLocations);
    }

    [Theory]
    [InlineData("delivery")]
    [InlineData("sells")]
    public async Task GetAccountHoldingsAsync_trading_post_failure_only_nulls_its_category_and_total(string failedSource)
    {
        var client = new FakeGw2ApiClient
        {
            AccountStorage = new Gw2AccountStorage([new Gw2StorageStack(1, 2, Gw2StorageSource.Bank, 0)]),
            Delivery = new Gw2TradingPostDelivery(0, [new Gw2TradingPostDeliveryItem(1, 3)]),
            CurrentSells = new Gw2CurrentSells([new Gw2CurrentSellOrder(1, 1, 999, 4, DateTimeOffset.Parse("2026-01-01T00:00:00Z"))]),
            Items = new Gw2Items([new Gw2Item(1, "Synthetic Item")], [])
        };
        if (failedSource == "delivery")
        {
            client.DeliveryError = new Gw2ConfigurationException("private delivery response");
        }
        else
        {
            client.SellsError = new Gw2ConfigurationException("private sells response");
        }

        var result = await new GetAccountHoldingsTool(client, TimeProvider.System)
            .GetAccountHoldingsAsync([1], null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(2, item.OnHand);
        Assert.Equal(failedSource == "delivery" ? null : 3, item.InTradingPostDelivery);
        Assert.Equal(failedSource == "sells" ? null : 4, item.ListedForSale);
        Assert.Null(item.OwnedTotal);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task GetAccountHoldingsAsync_wallet_failure_can_coexist_with_complete_item_facts()
    {
        var client = new FakeGw2ApiClient
        {
            WalletError = new Gw2ConfigurationException("private wallet response"),
            Items = new Gw2Items([new Gw2Item(1, "Synthetic Item")], [])
        };
        var tool = new GetAccountHoldingsTool(client, TimeProvider.System);

        var result = await tool.GetAccountHoldingsAsync([1], [2], CancellationToken.None);

        Assert.Equal(0, Assert.Single(result.Items).OwnedTotal);
        Assert.Null(Assert.Single(result.Currencies).OwnedTotal);
        Assert.Contains("Wallet", result.UnavailableLocations);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public async Task GetAccountHoldingsAsync_metadata_failure_keeps_complete_counts_and_null_name()
    {
        var client = new FakeGw2ApiClient { ItemsError = new Gw2ConfigurationException("private metadata payload") };
        var tool = new GetAccountHoldingsTool(client, TimeProvider.System);

        var result = await tool.GetAccountHoldingsAsync([1], null, CancellationToken.None);

        Assert.Null(Assert.Single(result.Items).Name);
        Assert.Equal(0, Assert.Single(result.Items).OwnedTotal);
        Assert.True(result.IsComplete);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("item_metadata_missing", warning.Code);
        Assert.Equal(1, warning.ItemId);
        Assert.DoesNotContain("private metadata payload", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("timeout")]
    public async Task GetAccountHoldingsAsync_expected_operational_source_failure_becomes_redacted_unavailability(string failureKind)
    {
        var client = new FakeGw2ApiClient
        {
            StorageError = failureKind == "transport"
                ? new HttpRequestException("private transport details")
                : new OperationCanceledException("private timeout details"),
            Items = new Gw2Items([new Gw2Item(1, "Synthetic Item")], [])
        };
        var tool = new GetAccountHoldingsTool(client, TimeProvider.System);

        var result = await tool.GetAccountHoldingsAsync([1], null, CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(["Bank", "MaterialStorage", "SharedInventory"], result.UnavailableLocations);
        Assert.DoesNotContain("private", string.Join(' ', result.Warnings.Select(warning => warning.Message)), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("timeout")]
    public async Task GetAccountHoldingsAsync_expected_operational_metadata_failure_is_downgraded(string failureKind)
    {
        var client = new FakeGw2ApiClient
        {
            ItemsError = failureKind == "transport"
                ? new HttpRequestException("private transport details")
                : new OperationCanceledException("private timeout details")
        };
        var tool = new GetAccountHoldingsTool(client, TimeProvider.System);

        var result = await tool.GetAccountHoldingsAsync([1], null, CancellationToken.None);

        Assert.Null(Assert.Single(result.Items).Name);
        Assert.True(result.IsComplete);
        Assert.Equal("item_metadata_missing", Assert.Single(result.Warnings).Code);
    }

    [Fact]
    public async Task GetAccountHoldingsAsync_partial_metadata_warns_only_for_missing_requested_ids()
    {
        var client = new FakeGw2ApiClient
        {
            Items = new Gw2Items([new Gw2Item(2, "Second Item")], [1])
        };
        var tool = new GetAccountHoldingsTool(client, TimeProvider.System);

        var result = await tool.GetAccountHoldingsAsync([1, 2], null, CancellationToken.None);

        Assert.Null(result.Items[0].Name);
        Assert.Equal("Second Item", result.Items[1].Name);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal(1, warning.ItemId);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public async Task GetAccountHoldingsAsync_fails_when_every_relevant_source_fails()
    {
        var client = new FakeGw2ApiClient
        {
            StorageError = new Gw2ConfigurationException("storage payload"),
            CharacterError = new Gw2ConfigurationException("character payload"),
            DeliveryError = new Gw2ConfigurationException("delivery payload"),
            SellsError = new Gw2ConfigurationException("sells payload"),
            WalletError = new Gw2ConfigurationException("wallet payload"),
            ItemsError = new Gw2ConfigurationException("metadata payload")
        };
        var tool = new GetAccountHoldingsTool(client, TimeProvider.System);

        var error = await Assert.ThrowsAsync<McpException>(() =>
            tool.GetAccountHoldingsAsync([1], [1], CancellationToken.None));

        Assert.Contains("holdings", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAccountHoldingsAsync_propagates_cancellation_and_stops_traversal()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var client = new FakeGw2ApiClient { StorageError = new OperationCanceledException(cancellationSource.Token) };
        var tool = new GetAccountHoldingsTool(client, TimeProvider.System);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tool.GetAccountHoldingsAsync([1], null, cancellationSource.Token));

        Assert.Equal(["storage"], client.Calls);
    }

    [Theory]
    [InlineData("category")]
    [InlineData("total")]
    public async Task GetAccountHoldingsAsync_checked_overflow_fails_concisely(string overflowKind)
    {
        var client = new FakeGw2ApiClient
        {
            AccountStorage = overflowKind == "category"
                ? new Gw2AccountStorage(
                [
                    new Gw2StorageStack(1, long.MaxValue, Gw2StorageSource.Bank, 0),
                    new Gw2StorageStack(1, 1, Gw2StorageSource.SharedInventory, 0)
                ])
                : new Gw2AccountStorage([new Gw2StorageStack(1, long.MaxValue, Gw2StorageSource.Bank, 0)]),
            Delivery = overflowKind == "total"
                ? new Gw2TradingPostDelivery(0, [new Gw2TradingPostDeliveryItem(1, 1)])
                : new Gw2TradingPostDelivery(0, []),
            Items = new Gw2Items([new Gw2Item(1, "Synthetic Item")], [])
        };
        var tool = new GetAccountHoldingsTool(client, TimeProvider.System);

        var error = await Assert.ThrowsAsync<McpException>(() =>
            tool.GetAccountHoldingsAsync([1], null, CancellationToken.None));

        Assert.Contains("too large", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(long.MaxValue.ToString(), error.Message, StringComparison.Ordinal);
    }

    private sealed class FakeGw2ApiClient : IGw2ApiClient
    {
        public List<string> Calls { get; } = [];
        public Gw2Wallet Wallet { get; set; } = new([], []);
        public Gw2AccountStorage AccountStorage { get; set; } = new([]);
        public Gw2CharacterBags CharacterBags { get; set; } = new([]);
        public Gw2TradingPostDelivery Delivery { get; set; } = new(0, []);
        public Gw2CurrentSells CurrentSells { get; set; } = new([]);
        public Gw2Items Items { get; set; } = new([], []);
        public Exception? WalletError { get; set; }
        public Exception? StorageError { get; set; }
        public Exception? CharacterError { get; set; }
        public Exception? DeliveryError { get; set; }
        public Exception? SellsError { get; set; }
        public Exception? ItemsError { get; set; }

        public Task<Gw2Account> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Gw2Account("Example.1234", 1, DateTimeOffset.Parse("2020-01-01T00:00:00Z"), []));

        public Task<Gw2Wallet> GetWalletAsync(CancellationToken cancellationToken) => Complete("wallet", Wallet, WalletError);
        public Task<Gw2Characters> GetCharactersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterBuild> GetCharacterBuildAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterEquipment> GetCharacterEquipmentAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterInventory> GetCharacterInventoryAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2AccountStorage> GetAccountStorageAsync(CancellationToken cancellationToken) => Complete("storage", AccountStorage, StorageError);
        public Task<Gw2CharacterBags> GetCharacterBagsAsync(CancellationToken cancellationToken) => Complete("bags", CharacterBags, CharacterError);
        public Task<Gw2TradingPostDelivery> GetTradingPostDeliveryAsync(CancellationToken cancellationToken) => Complete("delivery", Delivery, DeliveryError);
        public Task<Gw2CurrentSells> GetCurrentSellsAsync(CancellationToken cancellationToken) => Complete("sells", CurrentSells, SellsError);
        public Task<Gw2Items> GetItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken) => Complete("items", Items, ItemsError);
        public Task<Gw2LegendaryArmory> GetLegendaryArmoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        private Task<T> Complete<T>(string call, T result, Exception? error)
        {
            Calls.Add(call);
            return error is null ? Task.FromResult(result) : Task.FromException<T>(error);
        }
    }

    private sealed class RecordingTimeProvider(DateTimeOffset now, FakeGw2ApiClient client) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            client.Calls.Add("time");
            return now;
        }
    }
}
