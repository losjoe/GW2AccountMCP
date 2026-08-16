using System.Text.Json;
using GW2AccountMCP.Gw2;
using GW2AccountMCP.Tools;
using ModelContextProtocol;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class GetCharacterBuildTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public async Task GetCharacterBuildAsync_rejects_blank_name_before_source_access(string? characterName)
    {
        var client = new FakeGw2ApiClient();
        var tool = new GetCharacterBuildTool(client, TimeProvider.System);

        await Assert.ThrowsAsync<McpException>(() => tool.GetCharacterBuildAsync(characterName!, CancellationToken.None));

        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task GetCharacterBuildAsync_maps_fixed_nullable_slots_and_captures_time_after_client_completion()
    {
        var client = new FakeGw2ApiClient
        {
            Build = new Gw2CharacterBuild(
                "Canonical Hero", 2, "", "Revenant",
                [
                    new Gw2BuildSpecialization(new Gw2NumericReference(10, "Invocation"), [new Gw2NumericReference(11, "Trait"), null, null]),
                    new Gw2BuildSpecialization(null, [null, null, null]),
                    new Gw2BuildSpecialization(null, [null, null, null])
                ],
                new Gw2BuildSkills(new Gw2NumericReference(20, "Heal"), [null, new Gw2NumericReference(21, null), null], null),
                new Gw2BuildSkills(null, [null, null, null], null),
                null,
                new Gw2BuildLegends([new Gw2LegendReference("LegendA", 1, new Gw2NumericReference(30, "Swap")), null], [null, null]),
                false,
                [new Gw2MetadataWarning("metadata_unresolved", "skills", "21")])
        };
        var tool = new GetCharacterBuildTool(client, new RecordingTimeProvider(client));

        var result = await tool.GetCharacterBuildAsync("Canonical Hero", CancellationToken.None);

        Assert.Equal("Canonical Hero", result.CharacterName);
        Assert.Equal(2, result.Tab);
        Assert.Equal(JsonValueKind.Null, JsonSerializer.SerializeToDocument(result.Skills.Terrestrial.Utilities[0]).RootElement.ValueKind);
        Assert.Null(result.Pets);
        Assert.NotNull(result.Legends);
        Assert.Equal("LegendA", result.Legends!.Terrestrial[0]!.Id);
        Assert.Equal(1, result.Legends.Terrestrial[0]!.Code);
        Assert.Null(result.Skills.Terrestrial.Utilities[1]!.Name);
        Assert.False(result.IsMetadataComplete);
        Assert.Equal(["build", "time"], client.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetCharacterBuildAsync_redacts_transport_but_propagates_caller_cancellation(bool callerCancellation)
    {
        using var cancellationSource = new CancellationTokenSource();
        if (callerCancellation) cancellationSource.Cancel();
        var client = new FakeGw2ApiClient
        {
            Error = callerCancellation
                ? new OperationCanceledException(cancellationSource.Token)
                : new HttpRequestException("https://example.test/v2/characters/Secret%20Hero/buildtabs/active failed")
        };
        var tool = new GetCharacterBuildTool(client, TimeProvider.System);

        if (callerCancellation)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.GetCharacterBuildAsync("Secret Hero", cancellationSource.Token));
        }
        else
        {
            var error = await Assert.ThrowsAsync<McpException>(() => tool.GetCharacterBuildAsync("Secret Hero", CancellationToken.None));
            Assert.Equal("Guild Wars 2 character build is unavailable. Try again later.", error.Message);
            Assert.DoesNotContain("Secret", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("/v2/", error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("timeout")]
    public async Task GetCharacterBuildAsync_does_not_retain_private_paths_in_redacted_failure_exception_chains(string failureKind)
    {
        const string privatePath = "/v2/characters/Secret%20Hero/buildtabs/active";
        var client = new FakeGw2ApiClient
        {
            Error = failureKind == "transport"
                ? new HttpRequestException($"https://example.test{privatePath} failed")
                : new OperationCanceledException($"{privatePath} timed out")
        };
        var tool = new GetCharacterBuildTool(client, TimeProvider.System);

        var error = await Assert.ThrowsAsync<McpException>(() => tool.GetCharacterBuildAsync("Secret Hero", CancellationToken.None));

        Assert.Equal("Guild Wars 2 character build is unavailable. Try again later.", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("Secret", ExceptionChain(error), StringComparison.Ordinal);
        Assert.DoesNotContain("/v2/", ExceptionChain(error), StringComparison.Ordinal);
    }

    private static string ExceptionChain(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join("\n", messages);
    }

    private sealed class FakeGw2ApiClient : IGw2ApiClient
    {
        public List<string> Calls { get; } = [];
        public Gw2CharacterBuild Build { get; set; } = null!;
        public Exception? Error { get; set; }

        public Task<Gw2CharacterBuild> GetCharacterBuildAsync(string characterName, CancellationToken cancellationToken)
        {
            Calls.Add("build");
            return Error is null ? Task.FromResult(Build) : Task.FromException<Gw2CharacterBuild>(Error);
        }

        public Task<Gw2CharacterEquipment> GetCharacterEquipmentAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterInventory> GetCharacterInventoryAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Gw2Account> GetAccountAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2Wallet> GetWalletAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2Characters> GetCharactersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2AccountStorage> GetAccountStorageAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterBags> GetCharacterBagsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2TradingPostDelivery> GetTradingPostDeliveryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CurrentSells> GetCurrentSellsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CurrentBuysPage> GetCurrentBuysPageAsync(int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2Items> GetItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2PublicItems> GetPublicItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2MaterialCategories> GetPublicMaterialCategoriesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2PublicRecipes> GetPublicRecipesAsync(IReadOnlyList<long> recipeIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2RecipeSelector> SearchPublicRecipesByInputItemAsync(long itemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2RecipeSelector> SearchPublicRecipesByOutputItemAsync(long itemId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2AccountRecipeUnlocks> GetAccountRecipeUnlocksAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2LegendaryArmory> GetLegendaryArmoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingTimeProvider(FakeGw2ApiClient client) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            client.Calls.Add("time");
            return DateTimeOffset.Parse("2026-08-14T12:00:00Z");
        }
    }
}
