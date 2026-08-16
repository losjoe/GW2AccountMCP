using System.ComponentModel;
using System.Text.Json.Serialization;
using GW2AccountMCP.Gw2;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GW2AccountMCP.Tools;

[McpServerToolType]
public sealed class GetAchievementProgressTool(IGw2ApiClient gw2ApiClient, TimeProvider timeProvider)
{
    private const int MaximumAchievementIds = 20;
    private const int MaximumCompletedBits = 512;
    private const int MaximumWarnings = 32;

    [McpServerTool(Name = "get_achievement_progress", Title = "Get achievement progress", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Gets bounded account achievement progress for explicit canonical achievement IDs and optionally joins compact public English definitions.")]
    public async Task<AchievementProgressResult> GetAchievementProgressAsync(
        [Description("Required caller-ordered list of 1 through 20 distinct positive Int64 achievement IDs.")] IReadOnlyList<long> achievementIds,
        CancellationToken cancellationToken = default)
    {
        if (achievementIds is null || achievementIds.Count is 0 or > MaximumAchievementIds || achievementIds.Any(id => id <= 0) || achievementIds.Distinct().Count() != achievementIds.Count)
        {
            throw new McpException("achievementIds must contain 1 to 20 unique positive Int64 values.");
        }

        Gw2AccountAchievementProgress account;
        try { account = await gw2ApiClient.GetAccountAchievementProgressAsync(cancellationToken); }
        catch (Gw2ConfigurationException exception) { throw new McpException(exception.Message, exception); }
        catch (HttpRequestException) { throw new McpException("Guild Wars 2 account achievement progress is unavailable. Try again later."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new McpException("Guild Wars 2 account achievement progress is unavailable. Try again later."); }
        var accountProgressAsOf = timeProvider.GetUtcNow();
        var accountById = account.Entries.ToDictionary(entry => entry.Id);
        var selectedBitCount = achievementIds.Sum(id => accountById.GetValueOrDefault(id)?.CompletedBits?.Count ?? 0);
        if (selectedBitCount > MaximumCompletedBits) throw new McpException("Selected account achievement progress exceeds the completed-bit response limit.");

        Gw2PublicAchievements? definitions = null;
        DateTimeOffset? definitionsAsOf = null;
        try
        {
            definitions = await gw2ApiClient.GetPublicAchievementsAsync(achievementIds, cancellationToken);
            definitionsAsOf = timeProvider.GetUtcNow();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Gw2ConfigurationException) { }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) { }

        var definitionsById = definitions?.Achievements.ToDictionary(definition => definition.Id);
        var warnings = new List<string>();
        var rows = achievementIds.Select(id => ToRow(id, accountById.GetValueOrDefault(id), definitions is null ? null : definitionsById!.GetValueOrDefault(id), definitions is null, warnings)).ToArray();
        return new AchievementProgressResult(
            rows,
            definitions?.MissingAchievementIds,
            definitions is not null && definitions.MissingAchievementIds.Count == 0,
            warnings,
            accountProgressAsOf,
            definitionsAsOf,
            timeProvider.GetUtcNow(),
            false,
            "Account progress is a complete bounded authenticated account response; English definitions are one separate explicit public-ID response.",
            definitions is null ? "Account progress is retained, but public definitions were unavailable." : definitions.MissingAchievementIds.Count == 0 ? "All requested public achievement definitions were resolved." : "Only returned public definitions are resolved; missing IDs have no public achievement resource.",
            "Rows are limited to requested IDs. Account absence does not infer zero progress, completion, unlock state, repeatability, or bit state; this is not an atomic snapshot.");
    }

    private static AchievementProgressRow ToRow(long id, Gw2AccountAchievementProgressEntry? account, Gw2PublicAchievement? definition, bool definitionsUnavailable, List<string> warnings)
    {
        var completedBits = account?.CompletedBits?.Select(index => ToCompletedBit(id, index, definition, definitionsUnavailable, warnings)).ToArray();
        return new AchievementProgressRow(
            id,
            account is null ? "NoAccountProgressRecord" : "ReportedAccountProgress",
            account?.Current,
            account?.Max,
            account?.Done,
            account?.Repeated,
            account?.IsUnlocked,
            completedBits,
            definitionsUnavailable ? "PublicDefinitionsUnavailable" : definition is null ? "NoPublicAchievementResource" : "Found",
            definition?.Name,
            definition?.Description,
            definition?.Requirement,
            definition?.LockedText,
            definition?.Type,
            definition?.Flags.OrderBy(flag => flag, StringComparer.Ordinal).ToArray(),
            definition?.Bits?.Count);
    }

    private static AchievementCompletedBitResult ToCompletedBit(long achievementId, long index, Gw2PublicAchievement? definition, bool definitionsUnavailable, List<string> warnings)
    {
        var bit = definition?.Bits is { } bits && index < bits.Count ? bits[(int)index] : null;
        if (!string.IsNullOrWhiteSpace(bit?.Type)) return new AchievementCompletedBitResult(index, true, bit.Type, bit.Id, bit.Text);
        if (warnings.Count < MaximumWarnings)
        {
            warnings.Add(definitionsUnavailable
                ? $"Achievement {achievementId} completed bit {index} could not be resolved because public definitions are unavailable."
                : $"Achievement {achievementId} completed bit {index} has no resolvable public bit definition.");
        }
        return new AchievementCompletedBitResult(index, false, null, null, null);
    }
}

public sealed record AchievementProgressResult(
    [property: JsonRequired] IReadOnlyList<AchievementProgressRow> Rows,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<long>? MissingDefinitionIds,
    [property: JsonRequired] bool AreAllDefinitionsResolved,
    [property: JsonRequired] IReadOnlyList<string> Warnings,
    [property: JsonRequired] DateTimeOffset AccountProgressAsOf,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? DefinitionsAsOf,
    [property: JsonRequired] DateTimeOffset AsOf,
    [property: JsonRequired] bool IsAtomicSnapshot,
    [property: JsonRequired] string SourceStatement,
    [property: JsonRequired] string CompletenessStatement,
    [property: JsonRequired] string ScopeStatement);

public sealed record AchievementProgressRow(
    [property: JsonRequired] long Id,
    [property: JsonRequired] string AccountProgressStatus,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Current,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Max,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? Done,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Repeated,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? IsUnlocked,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<AchievementCompletedBitResult>? CompletedBits,
    [property: JsonRequired] string DefinitionStatus,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Description,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Requirement,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? LockedText,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Type,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<string>? Flags,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? BitCount);

public sealed record AchievementCompletedBitResult(
    [property: JsonRequired] long Index,
    [property: JsonRequired] bool IsDefinitionResolved,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Type,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Id,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Text);
