using System.ComponentModel;
using System.Text.Json.Serialization;
using GW2AccountMCP.Gw2;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GW2AccountMCP.Tools;

[McpServerToolType]
public sealed class GetRecipesTool(IGw2ApiClient gw2ApiClient, TimeProvider timeProvider)
{
    private const int MaximumIds = 100;
    private const int MaximumOffset = 4_999;
    private const int DefaultLimit = 50;
    private const int MaximumLimit = 100;
    private const int MaximumWarnings = 16;
    private const string SourceStatement = "Public request-time Guild Wars 2 recipe observations; not a source publication or currentness guarantee.";
    private const string ScopeStatement = "Player-discovered crafting recipes only; excludes Mystic Forge, vendor, achievement, acquisition-route, price, cost, and recommendation behavior.";
    private const string InputSelectorStatement = "The input selector indexes Item ingredients only; Currency and GuildUpgrade ingredients are not selector matches.";
    private const string OutputSelectorStatement = "The output selector matches raw source output_item_id values, which can be bogus for guild recipes and must not be treated as their semantic Item output; those recipes use GuildUpgrade semantics.";
    private static readonly HashSet<string> KnownIngredientKinds = ["Item", "Currency", "GuildUpgrade"];

    [McpServerTool(Name = "get_recipes", Title = "Get recipes", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns bounded public Guild Wars 2 player-discovered crafting recipe facts by recipe IDs or by an input/output item selector. It does not use credentials, account unlocks, caches, prices, costs, or recommendations.")]
    public async Task<GetRecipesResult> GetRecipesAsync(
        [Description("Required mode: ByIds, InputItem, or OutputItem.")] string? mode,
        [Description("Required only for ByIds: 1 through 100 distinct positive canonical recipe IDs.")] IReadOnlyList<long>? recipeIds = null,
        [Description("Required only for InputItem or OutputItem: one positive canonical item ID.")] long? itemId = null,
        [Description("Optional only for selector modes: local sorted-result offset from 0 through 4999. Omitted or null means 0.")] int? offset = null,
        [Description("Optional only for selector modes: local page size from 1 through 100. Omitted or null means 50.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (mode == "ByIds")
        {
            if (recipeIds is null
                || recipeIds.Count is < 1 or > MaximumIds
                || recipeIds.Any(id => id <= 0)
                || recipeIds.Distinct().Count() != recipeIds.Count
                || itemId is not null
                || offset is not null
                || limit is not null)
            {
                throw InvalidArguments();
            }

            var byIdsRecipes = await GetDefinitionsAsync(recipeIds, cancellationToken).ConfigureAwait(false);
            var byIdsRecipesAsOf = timeProvider.GetUtcNow();
            return BuildResult(
                mode,
                recipeIds,
                byIdsRecipes,
                null,
                byIdsRecipesAsOf,
                true,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }

        if (mode is not "InputItem" and not "OutputItem"
            || recipeIds is not null
            || itemId is not > 0
            || offset is < 0 or > MaximumOffset
            || limit is < 1 or > MaximumLimit)
        {
            throw InvalidArguments();
        }

        var effectiveOffset = offset ?? 0;
        var effectiveLimit = limit ?? DefaultLimit;
        Gw2RecipeSelector selector;
        try
        {
            selector = mode == "InputItem"
                ? await gw2ApiClient.SearchPublicRecipesByInputItemAsync(itemId.Value, cancellationToken).ConfigureAwait(false)
                : await gw2ApiClient.SearchPublicRecipesByOutputItemAsync(itemId.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (Gw2ConfigurationException exception) { throw new McpException(exception.Message, exception); }
        catch (Exception exception) when (exception is HttpRequestException or IOException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new McpException("Guild Wars 2 public recipe facts are unavailable. Try again later.");
        }

        var selectorAsOf = timeProvider.GetUtcNow();
        var selectedIds = selector.RecipeIds.Order().Skip(effectiveOffset).Take(effectiveLimit).ToArray();
        var recipes = selectedIds.Length == 0
            ? new Gw2PublicRecipes([], [])
            : await GetDefinitionsAsync(selectedIds, cancellationToken).ConfigureAwait(false);
        var recipesAsOf = selectedIds.Length == 0 ? selectorAsOf : timeProvider.GetUtcNow();
        return BuildResult(
            mode,
            selectedIds,
            recipes,
            selectorAsOf,
            recipesAsOf,
            null,
            true,
            true,
            recipes.MissingRecipeIds.Count == 0,
            selector.RecipeIds.Count,
            effectiveOffset,
            effectiveLimit,
            selectedIds.Length,
            effectiveOffset + selectedIds.Length < selector.RecipeIds.Count);
    }

    private async Task<Gw2PublicRecipes> GetDefinitionsAsync(IReadOnlyList<long> recipeIds, CancellationToken cancellationToken)
    {
        try
        {
            return await gw2ApiClient.GetPublicRecipesAsync(recipeIds, cancellationToken).ConfigureAwait(false);
        }
        catch (Gw2ConfigurationException exception) { throw new McpException(exception.Message, exception); }
        catch (Exception exception) when (exception is HttpRequestException or IOException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new McpException("Guild Wars 2 public recipe facts are unavailable. Try again later.");
        }
    }

    private static GetRecipesResult BuildResult(
        string mode,
        IReadOnlyList<long> requestedIds,
        Gw2PublicRecipes publicRecipes,
        DateTimeOffset? selectorAsOf,
        DateTimeOffset recipesAsOf,
        bool? areAllRequestedDefinitionsResolved,
        bool? isSelectorComplete,
        bool? isPageComplete,
        bool? areAllSelectedDefinitionsResolved,
        int? totalMatches,
        int? offset,
        int? limit,
        int? returnedCount,
        bool? hasMore)
    {
        var recipesById = publicRecipes.Recipes.ToDictionary(recipe => recipe.Id);
        var missingRecipeIds = requestedIds.Where(id => !recipesById.ContainsKey(id)).ToArray();
        var warnings = new List<string>();
        var rows = requestedIds.Select(id => recipesById.TryGetValue(id, out var recipe)
            ? Found(recipe, warnings)
            : Missing(id)).ToArray();
        return new GetRecipesResult(
            mode,
            rows,
            requestedIds.Where(recipesById.ContainsKey).ToArray(),
            missingRecipeIds,
            mode == "ByIds" ? missingRecipeIds.Length == 0 : areAllRequestedDefinitionsResolved,
            selectorAsOf,
            recipesAsOf,
            recipesAsOf,
            false,
            isSelectorComplete,
            isPageComplete,
            areAllSelectedDefinitionsResolved,
            totalMatches,
            offset,
            limit,
            returnedCount,
            hasMore,
            mode == "InputItem" ? InputSelectorStatement : mode == "OutputItem" ? OutputSelectorStatement : null,
            warnings.Take(MaximumWarnings).ToArray(),
            SourceStatement,
            ScopeStatement);
    }

    private static PublicRecipeResult Found(Gw2PublicRecipe recipe, List<string> warnings)
    {
        RecipeOutputResult output;
        if (recipe.OutputUpgradeId is { } outputUpgradeId)
        {
            output = new RecipeOutputResult("GuildUpgrade", outputUpgradeId, recipe.OutputItemCount);
            warnings.Add($"Recipe {recipe.Id} has a GuildUpgrade output; sourceOutputItemId can be bogus and is disclosure-only, not its semantic Item output.");
        }
        else
        {
            output = new RecipeOutputResult("Item", recipe.OutputItemId, recipe.OutputItemCount);
        }

        foreach (var ingredient in recipe.Ingredients.Where(ingredient => !KnownIngredientKinds.Contains(ingredient.Kind)))
        {
            warnings.Add($"Recipe {recipe.Id} contains unknown ingredient kind '{ingredient.Kind}', preserved as reported.");
        }

        return new PublicRecipeResult(
            "Found",
            recipe.Id,
            recipe.Type,
            output,
            recipe.OutputItemId,
            recipe.MinRating,
            recipe.Disciplines.Order(StringComparer.Ordinal).ToArray(),
            recipe.Flags.Order(StringComparer.Ordinal).ToArray(),
            recipe.TimeToCraftMs,
            recipe.Ingredients.Select(ingredient => new RecipeIngredientResult(ingredient.Kind, ingredient.Id, ingredient.Count)).ToArray());
    }

    private static PublicRecipeResult Missing(long id) => new(
        "NoPublicRecipeResource",
        id,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null);

    private static McpException InvalidArguments() => new(
        "Use mode ByIds with 1 through 100 distinct positive recipeIds only, or mode InputItem/OutputItem with one positive itemId and optional offset 0 through 4999 and limit 1 through 100.");
}

public sealed record GetRecipesResult(
    [property: JsonRequired] string Mode,
    [property: JsonRequired] IReadOnlyList<PublicRecipeResult> Recipes,
    [property: JsonRequired] IReadOnlyList<long> ResolvedRecipeIds,
    [property: JsonRequired] IReadOnlyList<long> MissingRecipeIds,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? AreAllRequestedDefinitionsResolved,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? SelectorAsOf,
    [property: JsonRequired] DateTimeOffset RecipesAsOf,
    [property: JsonRequired] DateTimeOffset AsOf,
    [property: JsonRequired] bool IsAtomicSnapshot,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? IsSelectorComplete,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? IsPageComplete,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? AreAllSelectedDefinitionsResolved,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? TotalMatches,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Offset,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Limit,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? ReturnedCount,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? HasMore,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SelectorStatement,
    [property: JsonRequired] IReadOnlyList<string> Warnings,
    [property: JsonRequired] string SourceStatement,
    [property: JsonRequired] string ScopeStatement);

public sealed record PublicRecipeResult(
    [property: JsonRequired] string Status,
    [property: JsonRequired] long Id,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Type,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] RecipeOutputResult? Output,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? SourceOutputItemId,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? MinRating,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<string>? Disciplines,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<string>? Flags,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? TimeToCraftMs,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<RecipeIngredientResult>? Ingredients);

public sealed record RecipeOutputResult(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] long Id,
    [property: JsonRequired] long Count);

public sealed record RecipeIngredientResult(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] long Id,
    [property: JsonRequired] long Count);
