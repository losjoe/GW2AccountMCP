using GW2AccountMCP.Gw2;
using GW2AccountMCP.Tools;
using ModelContextProtocol;
using Xunit;

namespace GW2AccountMCP.Tests;

public sealed class GetRecipesToolTests
{
    [Fact]
    public async Task ByIds_returns_caller_ordered_recipe_facts_with_missing_rows_and_completion_time()
    {
        var client = new FakeGw2ApiClient
        {
            PublicRecipes = new Gw2PublicRecipes(
                [
                    new Gw2PublicRecipe(
                        2,
                        "Refinement",
                        20,
                        3,
                        null,
                        400,
                        1000,
                        ["Weaponsmith", "Artificer"],
                        ["LearnedFromItem"],
                        [new Gw2RecipeIngredient("Item", 10, 2)])
                ],
                [1])
        };
        var tool = new GetRecipesTool(client, new FixedTimeProvider());

        var result = await tool.GetRecipesAsync("ByIds", [1, 2], cancellationToken: CancellationToken.None);

        Assert.Equal("ByIds", result.Mode);
        Assert.Equal([1L, 2L], result.Recipes.Select(recipe => recipe.Id));
        Assert.Equal(["NoPublicRecipeResource", "Found"], result.Recipes.Select(recipe => recipe.Status));
        var output = Assert.IsType<RecipeOutputResult>(result.Recipes[1].Output);
        Assert.Equal("Item", output.Kind);
        Assert.Equal((20L, 3L), (output.Id, output.Count));
        Assert.Equal(["Artificer", "Weaponsmith"], result.Recipes[1].Disciplines);
        Assert.Equal([1L], result.MissingRecipeIds);
        Assert.False(result.AreAllRequestedDefinitionsResolved);
        Assert.Null(result.SelectorAsOf);
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), result.RecipesAsOf);
        Assert.Equal(result.RecipesAsOf, result.AsOf);
        Assert.Null(result.AccountUnlocksAsOf);
        Assert.All(result.Recipes, recipe => Assert.Null(recipe.AccountUnlockListContainsRecipe));
        Assert.False(result.IsAtomicSnapshot);
        Assert.Equal(1, client.DefinitionCalls);
        Assert.Equal(0, client.AccountUnlockCalls);
    }

    [Fact]
    public async Task ByIds_account_annotation_marks_found_and_missing_public_rows_with_distinct_completion_time()
    {
        var client = new FakeGw2ApiClient
        {
            PublicRecipes = new Gw2PublicRecipes([Recipe(2)], [1]),
            AccountRecipeUnlocks = new Gw2AccountRecipeUnlocks([1])
        };
        var tool = new GetRecipesTool(client, new SequenceTimeProvider(
            DateTimeOffset.Parse("2026-08-16T12:00:00Z"),
            DateTimeOffset.Parse("2026-08-16T12:00:01Z")));

        var result = await tool.GetRecipesAsync("ByIds", [1, 2], null, null, null, true, CancellationToken.None);

        Assert.Equal([true, false], result.Recipes.Select(recipe => recipe.AccountUnlockListContainsRecipe));
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), result.RecipesAsOf);
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T12:00:01Z"), result.AccountUnlocksAsOf);
        Assert.Equal(result.AccountUnlocksAsOf, result.AsOf);
        Assert.Equal(1, client.AccountUnlockCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Null_or_false_account_annotation_preserves_public_only_behavior(bool? includeAccountUnlocks)
    {
        var client = new FakeGw2ApiClient { PublicRecipes = new Gw2PublicRecipes([Recipe(1)], []) };

        var result = await new GetRecipesTool(client, new FixedTimeProvider()).GetRecipesAsync(
            "ByIds", [1], null, null, null, includeAccountUnlocks, CancellationToken.None);

        Assert.Null(result.AccountUnlocksAsOf);
        Assert.Null(Assert.Single(result.Recipes).AccountUnlockListContainsRecipe);
        Assert.Equal(result.RecipesAsOf, result.AsOf);
        Assert.Equal(0, client.AccountUnlockCalls);
    }

    [Fact]
    public async Task Complete_empty_account_list_marks_every_requested_recipe_false()
    {
        var client = new FakeGw2ApiClient
        {
            PublicRecipes = new Gw2PublicRecipes([Recipe(1)], [2]),
            AccountRecipeUnlocks = new Gw2AccountRecipeUnlocks([])
        };

        var result = await new GetRecipesTool(client, new SequenceTimeProvider(
                DateTimeOffset.Parse("2026-08-16T12:00:00Z"),
                DateTimeOffset.Parse("2026-08-16T12:00:01Z")))
            .GetRecipesAsync("ByIds", [1, 2], null, null, null, true, CancellationToken.None);

        Assert.Equal([false, false], result.Recipes.Select(recipe => recipe.AccountUnlockListContainsRecipe));
        Assert.NotNull(result.AccountUnlocksAsOf);
    }

    [Fact]
    public async Task InputItem_sorts_and_slices_complete_selector_with_distinct_times()
    {
        var client = new FakeGw2ApiClient
        {
            InputSelector = new Gw2RecipeSelector([5, 1, 3, 2]),
            PublicRecipes = new Gw2PublicRecipes(
                [
                    Recipe(2),
                    Recipe(3)
                ],
                [])
        };
        var tool = new GetRecipesTool(client, new SequenceTimeProvider(
            DateTimeOffset.Parse("2026-08-16T12:00:00Z"),
            DateTimeOffset.Parse("2026-08-16T12:00:01Z")));

        var result = await tool.GetRecipesAsync("InputItem", null, 10, 1, 2, null, CancellationToken.None);

        Assert.Equal([2L, 3L], result.Recipes.Select(recipe => recipe.Id));
        Assert.Equal([2L, 3L], client.DefinitionRequest);
        Assert.True(result.IsSelectorComplete);
        Assert.True(result.IsPageComplete);
        Assert.True(result.AreAllSelectedDefinitionsResolved);
        Assert.Null(result.AreAllRequestedDefinitionsResolved);
        Assert.Equal(4, result.TotalMatches);
        Assert.Equal(1, result.Offset);
        Assert.Equal(2, result.Limit);
        Assert.Equal(2, result.ReturnedCount);
        Assert.True(result.HasMore);
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T12:00:00Z"), result.SelectorAsOf);
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T12:00:01Z"), result.RecipesAsOf);
        Assert.Contains("Item ingredients only", result.SelectorStatement, StringComparison.Ordinal);
        Assert.Equal(1, client.InputSelectorCalls);
    }

    [Fact]
    public async Task Selector_empty_page_skips_definition_lookup_and_reuses_selector_time()
    {
        var client = new FakeGw2ApiClient { InputSelector = new Gw2RecipeSelector([1, 2]) };
        var tool = new GetRecipesTool(client, new FixedTimeProvider());

        var result = await tool.GetRecipesAsync("InputItem", null, 10, 10, null, null, CancellationToken.None);

        Assert.Empty(result.Recipes);
        Assert.Equal(2, result.TotalMatches);
        Assert.Equal(0, result.ReturnedCount);
        Assert.False(result.HasMore);
        Assert.Equal(result.SelectorAsOf, result.RecipesAsOf);
        Assert.Equal(0, client.DefinitionCalls);
    }

    [Fact]
    public async Task Selector_empty_page_still_performs_explicit_account_annotation()
    {
        var client = new FakeGw2ApiClient
        {
            InputSelector = new Gw2RecipeSelector([1]),
            AccountRecipeUnlocks = new Gw2AccountRecipeUnlocks([])
        };

        var result = await new GetRecipesTool(client, new SequenceTimeProvider(
                DateTimeOffset.Parse("2026-08-16T12:00:00Z"),
                DateTimeOffset.Parse("2026-08-16T12:00:01Z")))
            .GetRecipesAsync("InputItem", null, 10, 10, null, true, CancellationToken.None);

        Assert.Empty(result.Recipes);
        Assert.Equal(result.SelectorAsOf, result.RecipesAsOf);
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T12:00:01Z"), result.AccountUnlocksAsOf);
        Assert.Equal(result.AccountUnlocksAsOf, result.AsOf);
        Assert.Equal(0, client.DefinitionCalls);
        Assert.Equal(1, client.AccountUnlockCalls);
    }

    [Fact]
    public async Task OutputItem_maps_guild_output_and_preserves_unknown_ingredient_kind()
    {
        var client = new FakeGw2ApiClient
        {
            OutputSelector = new Gw2RecipeSelector([7]),
            PublicRecipes = new Gw2PublicRecipes(
                [
                    new Gw2PublicRecipe(
                        7,
                        "GuildDecoration",
                        999,
                        4,
                        77,
                        0,
                        500,
                        ["Scribe"],
                        [],
                        [new Gw2RecipeIngredient("FutureKind", 8, 2)])
                ],
                [])
        };

        var result = await new GetRecipesTool(client, new SequenceTimeProvider(
                DateTimeOffset.Parse("2026-08-16T12:00:00Z"),
                DateTimeOffset.Parse("2026-08-16T12:00:01Z")))
            .GetRecipesAsync("OutputItem", null, 999, null, null, null, CancellationToken.None);

        var recipe = Assert.Single(result.Recipes);
        var output = Assert.IsType<RecipeOutputResult>(recipe.Output);
        Assert.Equal(("GuildUpgrade", 77L, 4L), (output.Kind, output.Id, output.Count));
        Assert.Equal(999, recipe.SourceOutputItemId);
        Assert.Equal(("FutureKind", 8L, 2L), Assert.Single(recipe.Ingredients!) is var ingredient ? (ingredient.Kind, ingredient.Id, ingredient.Count) : default);
        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains(result.Warnings, warning => warning.Contains("disclosure-only", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("FutureKind", StringComparison.Ordinal));
        Assert.Contains("bogus", result.SelectorStatement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("must not be treated", result.SelectorStatement, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, client.OutputSelectorCalls);
    }

    [Fact]
    public async Task Rejects_mode_and_argument_contradictions_before_source_work()
    {
        var invalidCalls = new (string? Mode, IReadOnlyList<long>? RecipeIds, long? ItemId, int? Offset, int? Limit)[]
        {
            (null, null, null, null, null),
            ("Future", null, null, null, null),
            ("ByIds", null, null, null, null),
            ("ByIds", [], null, null, null),
            ("ByIds", [1, 1], null, null, null),
            ("ByIds", [0], null, null, null),
            ("ByIds", Enumerable.Range(1, 101).Select(id => (long)id).ToArray(), null, null, null),
            ("ByIds", [1], 2, null, null),
            ("ByIds", [1], null, 0, null),
            ("InputItem", [1], 2, null, null),
            ("InputItem", null, null, null, null),
            ("InputItem", null, 0, null, null),
            ("InputItem", null, 1, -1, null),
            ("InputItem", null, 1, 5000, null),
            ("OutputItem", null, 1, null, 0),
            ("OutputItem", null, 1, null, 101)
        };
        var client = new FakeGw2ApiClient();
        var tool = new GetRecipesTool(client, TimeProvider.System);

        foreach (var invalid in invalidCalls)
        {
            await Assert.ThrowsAsync<McpException>(() => tool.GetRecipesAsync(
                invalid.Mode,
                invalid.RecipeIds,
                invalid.ItemId,
                invalid.Offset,
                invalid.Limit,
                true,
                CancellationToken.None));
        }

        Assert.Equal(0, client.DefinitionCalls);
        Assert.Equal(0, client.InputSelectorCalls);
        Assert.Equal(0, client.OutputSelectorCalls);
        Assert.Equal(0, client.AccountUnlockCalls);
    }

    [Fact]
    public async Task Maps_source_failures_and_preserves_caller_cancellation()
    {
        var unavailable = new FakeGw2ApiClient { DefinitionError = new IOException("private recipe body") };
        var error = await Assert.ThrowsAsync<McpException>(() =>
            new GetRecipesTool(unavailable, TimeProvider.System).GetRecipesAsync("ByIds", [1], null, null, null, null, CancellationToken.None));
        Assert.Contains("recipe facts are unavailable", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", error.Message, StringComparison.OrdinalIgnoreCase);

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var cancelled = new FakeGw2ApiClient { InputSelectorError = new OperationCanceledException(cancellationSource.Token) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GetRecipesTool(cancelled, TimeProvider.System).GetRecipesAsync("InputItem", null, 1, null, null, null, cancellationSource.Token));
    }

    [Fact]
    public async Task Explicit_account_failure_is_total_redacted_and_preserves_caller_cancellation()
    {
        var unavailable = new FakeGw2ApiClient
        {
            PublicRecipes = new Gw2PublicRecipes([Recipe(1)], []),
            AccountUnlockError = new IOException("private account recipe body")
        };
        var error = await Assert.ThrowsAsync<McpException>(() =>
            new GetRecipesTool(unavailable, TimeProvider.System)
                .GetRecipesAsync("ByIds", [1], null, null, null, true, CancellationToken.None));
        Assert.Contains("account recipe unlocks are unavailable", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", error.ToString(), StringComparison.OrdinalIgnoreCase);

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var cancelled = new FakeGw2ApiClient
        {
            PublicRecipes = new Gw2PublicRecipes([Recipe(1)], []),
            AccountUnlockError = new OperationCanceledException(cancellationSource.Token)
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GetRecipesTool(cancelled, TimeProvider.System)
                .GetRecipesAsync("ByIds", [1], null, null, null, true, cancellationSource.Token));
    }

    [Fact]
    public async Task Warnings_are_deterministic_and_capped()
    {
        var client = new FakeGw2ApiClient
        {
            PublicRecipes = new Gw2PublicRecipes(
                [
                    new Gw2PublicRecipe(
                        1,
                        "FutureRecipe",
                        2,
                        1,
                        null,
                        0,
                        0,
                        [],
                        [],
                        Enumerable.Range(1, 20).Select(id => new Gw2RecipeIngredient($"Future{id}", id, 1)).ToArray())
                ],
                [])
        };

        var result = await new GetRecipesTool(client, new FixedTimeProvider())
            .GetRecipesAsync("ByIds", [1], null, null, null, null, CancellationToken.None);

        Assert.Equal(16, result.Warnings.Count);
        Assert.Contains("Future1", result.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("Future16", result.Warnings[^1], StringComparison.Ordinal);
    }

    private sealed class FakeGw2ApiClient : IGw2ApiClient
    {
        public int DefinitionCalls { get; private set; }
        public int InputSelectorCalls { get; private set; }
        public int OutputSelectorCalls { get; private set; }
        public int AccountUnlockCalls { get; private set; }
        public IReadOnlyList<long>? DefinitionRequest { get; private set; }
        public Gw2PublicRecipes PublicRecipes { get; set; } = new([], []);
        public Gw2RecipeSelector InputSelector { get; set; } = new([]);
        public Gw2RecipeSelector OutputSelector { get; set; } = new([]);
        public Gw2AccountRecipeUnlocks AccountRecipeUnlocks { get; set; } = new([]);
        public Exception? DefinitionError { get; set; }
        public Exception? InputSelectorError { get; set; }
        public Exception? OutputSelectorError { get; set; }
        public Exception? AccountUnlockError { get; set; }

        public Task<Gw2PublicRecipes> GetPublicRecipesAsync(IReadOnlyList<long> recipeIds, CancellationToken cancellationToken)
        {
            DefinitionCalls++;
            DefinitionRequest = recipeIds.ToArray();
            return DefinitionError is null
                ? Task.FromResult(PublicRecipes)
                : Task.FromException<Gw2PublicRecipes>(DefinitionError);
        }

        public Task<Gw2RecipeSelector> SearchPublicRecipesByInputItemAsync(long itemId, CancellationToken cancellationToken)
        {
            InputSelectorCalls++;
            return InputSelectorError is null
                ? Task.FromResult(InputSelector)
                : Task.FromException<Gw2RecipeSelector>(InputSelectorError);
        }

        public Task<Gw2RecipeSelector> SearchPublicRecipesByOutputItemAsync(long itemId, CancellationToken cancellationToken)
        {
            OutputSelectorCalls++;
            return OutputSelectorError is null
                ? Task.FromResult(OutputSelector)
                : Task.FromException<Gw2RecipeSelector>(OutputSelectorError);
        }
        public Task<Gw2AccountRecipeUnlocks> GetAccountRecipeUnlocksAsync(CancellationToken cancellationToken)
        {
            AccountUnlockCalls++;
            return AccountUnlockError is null
                ? Task.FromResult(AccountRecipeUnlocks)
                : Task.FromException<Gw2AccountRecipeUnlocks>(AccountUnlockError);
        }
        public Task<Gw2Account> GetAccountAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2Wallet> GetWalletAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2Characters> GetCharactersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterBuild> GetCharacterBuildAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterEquipment> GetCharacterEquipmentAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterInventory> GetCharacterInventoryAsync(string characterName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2AccountStorage> GetAccountStorageAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CharacterBags> GetCharacterBagsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2TradingPostDelivery> GetTradingPostDeliveryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CurrentSells> GetCurrentSellsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CurrentBuysPage> GetCurrentBuysPageAsync(int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2CurrentSellsPage> GetCurrentSellsPageAsync(int page, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2Items> GetItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2PublicItems> GetPublicItemsAsync(IReadOnlyList<long> itemIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2MaterialCategories> GetPublicMaterialCategoriesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2LegendaryArmory> GetLegendaryArmoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2AccountAchievementProgress> GetAccountAchievementProgressAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2PublicAchievements> GetPublicAchievementsAsync(IReadOnlyList<long> achievementIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2AccountMasterySources> GetAccountMasterySourcesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Gw2PublicMasteries> GetPublicMasteriesAsync(IReadOnlyList<long> masteryIds, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-08-16T12:00:00Z");
    }

    private sealed class SequenceTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private int index;

        public override DateTimeOffset GetUtcNow() => values[index++];
    }

    private static Gw2PublicRecipe Recipe(long id) => new(
        id,
        "Refinement",
        id + 100,
        1,
        null,
        0,
        1000,
        [],
        [],
        []);
}
