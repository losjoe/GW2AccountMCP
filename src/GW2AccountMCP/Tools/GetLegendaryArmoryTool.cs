using System.ComponentModel;
using System.Text.Json.Serialization;
using GW2AccountMCP.Gw2;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GW2AccountMCP.Tools;

[McpServerToolType]
public sealed class GetLegendaryArmoryTool(IGw2ApiClient gw2ApiClient, TimeProvider timeProvider)
{
    [McpServerTool(Name = "get_legendary_armory", Title = "Get Legendary Armory", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Gets account Legendary Armory ownership. Armory counts are reusable availability for one equipment template, not physical holdings or equipped occurrences, and must not be added to get_account_holdings.")]
    public async Task<LegendaryArmoryResult> GetLegendaryArmoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var armory = await gw2ApiClient.GetLegendaryArmoryAsync(cancellationToken);
            return new LegendaryArmoryResult(
                "AccountLegendaryArmory",
                "AvailableForUseInSingleEquipmentTemplate",
                armory.Entries.Select(entry => new LegendaryArmoryEntryResult(entry.Id, entry.Name, entry.Type, entry.Subtype, entry.WeightClass, entry.ArmoryCount)).ToArray(),
                armory.IsMetadataComplete,
                armory.Warnings.Select(warning => new MetadataWarningResult(warning.Code, warning.Resolver, warning.ReferenceId)).ToArray(),
                timeProvider.GetUtcNow());
        }
        catch (Gw2ConfigurationException exception) { throw new McpException(exception.Message, exception); }
        catch (HttpRequestException) { throw new McpException("Guild Wars 2 Legendary Armory is unavailable. Try again later."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new McpException("Guild Wars 2 Legendary Armory is unavailable. Try again later."); }
    }
}

public sealed record LegendaryArmoryResult(
    [property: JsonRequired] string OwnershipScope,
    [property: JsonRequired] string CountSemantics,
    [property: JsonRequired] IReadOnlyList<LegendaryArmoryEntryResult> Entries,
    [property: JsonRequired] bool IsMetadataComplete,
    [property: JsonRequired] IReadOnlyList<MetadataWarningResult> Warnings,
    [property: JsonRequired] DateTimeOffset AsOf);

public sealed record LegendaryArmoryEntryResult(
    [property: JsonRequired] long Id,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Type,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Subtype,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? WeightClass,
    [property: JsonRequired] long ArmoryCount);
