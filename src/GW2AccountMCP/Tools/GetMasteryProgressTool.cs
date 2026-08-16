using System.ComponentModel;
using System.Text.Json.Serialization;
using GW2AccountMCP.Gw2;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GW2AccountMCP.Tools;

[McpServerToolType]
public sealed class GetMasteryProgressTool(IGw2ApiClient gw2ApiClient, TimeProvider timeProvider)
{
    private const int MaximumWarnings = 32;

    [McpServerTool(Name = "get_mastery_progress", Title = "Get mastery progress", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Gets complete account-reported mastery tracks and point totals with optional compact public English metadata.")]
    public async Task<MasteryProgressResult> GetMasteryProgressAsync(CancellationToken cancellationToken = default)
    {
        Gw2AccountMasterySources account;
        try { account = await gw2ApiClient.GetAccountMasterySourcesAsync(cancellationToken); }
        catch (Gw2ConfigurationException exception) { throw new McpException(exception.Message, exception); }
        catch (HttpRequestException) { throw new McpException("Guild Wars 2 account mastery progress is unavailable. Try again later."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new McpException("Guild Wars 2 account mastery progress is unavailable. Try again later."); }

        var accountTracks = account.Tracks.OrderBy(track => track.Id).ToArray();
        Gw2PublicMasteries? metadata = null;
        DateTimeOffset? metadataAsOf = null;
        var metadataStatus = accountTracks.Length == 0 ? "NotNeeded" : "Unavailable";
        IReadOnlyList<long>? missingMetadataTrackIds = accountTracks.Length == 0 ? [] : null;
        var warnings = new List<string>();
        if (accountTracks.Length != 0)
        {
            try
            {
                metadata = await gw2ApiClient.GetPublicMasteriesAsync(accountTracks.Select(track => track.Id).ToArray(), cancellationToken);
                metadataAsOf = timeProvider.GetUtcNow();
                metadataStatus = metadata.MissingMasteryIds.Count == 0 ? "Complete" : "Partial";
                missingMetadataTrackIds = metadata.MissingMasteryIds.Order().ToArray();
                if (metadataStatus == "Partial") AddWarning(warnings, "Public mastery metadata is partial; some account mastery tracks have no public resource.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Gw2ConfigurationException) { AddWarning(warnings, "Public mastery metadata is unavailable; validated account mastery facts are retained."); }
            catch (HttpRequestException) { AddWarning(warnings, "Public mastery metadata is unavailable; validated account mastery facts are retained."); }
            catch (OperationCanceledException) { AddWarning(warnings, "Public mastery metadata is unavailable; validated account mastery facts are retained."); }
        }

        var pointTotals = account.PointTotals.OrderBy(total => total.Region, StringComparer.Ordinal).Select(total =>
        {
            long? available = total.Spent <= total.Earned ? checked(total.Earned - total.Spent) : (long?)null;
            if (available is null) AddWarning(warnings, $"Mastery point total for region {total.Region} reports spent points greater than earned points.");
            return new MasteryPointTotalResult(total.Region, total.Spent, total.Earned, available);
        }).ToArray();
        var byId = metadata?.Masteries.ToDictionary(mastery => mastery.Id);
        var tracks = accountTracks.Select(track => ToTrack(track, byId?.GetValueOrDefault(track.Id), metadata is null && accountTracks.Length != 0, warnings)).ToArray();
        return new MasteryProgressResult(
            tracks,
            pointTotals,
            metadataStatus,
            missingMetadataTrackIds,
            metadataStatus is "NotNeeded" or "Complete",
            warnings,
            account.AccountMasteriesAsOf,
            account.MasteryPointsAsOf,
            metadataAsOf,
            timeProvider.GetUtcNow(),
            false,
            "Account mastery tracks and point totals are separate authenticated observations; public English mastery metadata is one separate optional explicit-ID observation.",
            metadataStatus == "NotNeeded" ? "Account reported no mastery tracks, so public metadata was not requested." : metadataStatus == "Complete" ? "All account mastery tracks were resolved to public mastery metadata." : metadataStatus == "Partial" ? "Only returned public mastery metadata is resolved; missing IDs have no public mastery resource." : "Validated account mastery facts are retained, but public mastery metadata is unavailable.",
            "sourceLevel is this tool's product-owned zero-based highest trained level interpretation, not an upstream guarantee; an absent account row or level does not infer an unstarted track. This is not an atomic snapshot.");
    }

    private static MasteryTrackResult ToTrack(Gw2AccountMasteryTrack track, Gw2PublicMastery? metadata, bool unavailable, List<string> warnings)
    {
        if (track.SourceLevel == long.MaxValue) throw new McpException("Account mastery source level exceeds supported response limits.");
        long? unlockedCount = track.SourceLevel is { } sourceLevel ? checked(sourceLevel + 1) : (long?)null;
        if (metadata is null)
        {
            return new MasteryTrackResult(track.Id, track.SourceLevel, unlockedCount, unavailable ? "PublicMasteriesUnavailable" : "NoPublicMasteryResource", null, null, null, null, null, null, null);
        }
        MasteryLevelResult? current = null;
        MasteryLevelResult? next = null;
        if (track.SourceLevel is { } level)
        {
            if (metadata.Levels.Count == 0)
            {
                AddWarning(warnings, $"Mastery track {track.Id} has public metadata with no levels; level context is unavailable.");
            }
            else if (level >= metadata.Levels.Count)
            {
                AddWarning(warnings, $"Mastery track {track.Id} sourceLevel {level} is outside the public level range; level context is unavailable.");
            }
            else
            {
                current = ToLevel(level, metadata.Levels[(int)level]);
                if (level + 1 < metadata.Levels.Count) next = ToLevel(level + 1, metadata.Levels[(int)(level + 1)]);
            }
        }
        return new MasteryTrackResult(track.Id, track.SourceLevel, unlockedCount, "Found", metadata.Name, metadata.Requirement, metadata.Region, metadata.Order, metadata.Levels.Count, current, next);
    }

    private static MasteryLevelResult ToLevel(long index, Gw2PublicMasteryLevel level) => new(index, level.Name, level.Description, level.Instruction, level.PointCost, level.ExperienceCost);
    private static void AddWarning(List<string> warnings, string message) { if (warnings.Count < MaximumWarnings) warnings.Add(message); }
}

public sealed record MasteryProgressResult(
    [property: JsonRequired] IReadOnlyList<MasteryTrackResult> Tracks,
    [property: JsonRequired] IReadOnlyList<MasteryPointTotalResult> PointTotals,
    [property: JsonRequired] string MetadataStatus,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<long>? MissingMetadataTrackIds,
    [property: JsonRequired] bool AreAllMetadataTracksResolved,
    [property: JsonRequired] IReadOnlyList<string> Warnings,
    [property: JsonRequired] DateTimeOffset AccountMasteriesAsOf,
    [property: JsonRequired] DateTimeOffset MasteryPointsAsOf,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? MetadataAsOf,
    [property: JsonRequired] DateTimeOffset AsOf,
    [property: JsonRequired] bool IsAtomicSnapshot,
    [property: JsonRequired] string SourceStatement,
    [property: JsonRequired] string CompletenessStatement,
    [property: JsonRequired] string ScopeStatement);

public sealed record MasteryTrackResult(
    [property: JsonRequired] long Id,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? SourceLevel,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? UnlockedLevelCount,
    [property: JsonRequired] string MetadataStatus,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Requirement,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Region,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Order,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? LevelCount,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] MasteryLevelResult? CurrentLevel,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] MasteryLevelResult? NextLevel);

public sealed record MasteryPointTotalResult(
    [property: JsonRequired] string Region,
    [property: JsonRequired] long Spent,
    [property: JsonRequired] long Earned,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? Available);

public sealed record MasteryLevelResult(
    [property: JsonRequired] long Index,
    [property: JsonRequired] string Name,
    [property: JsonRequired] string Description,
    [property: JsonRequired] string Instruction,
    [property: JsonRequired] long PointCost,
    [property: JsonRequired] long ExperienceCost);
