using System.ComponentModel;
using System.Text.Json.Serialization;
using GW2AccountMCP.Gw2;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GW2AccountMCP.Tools;

[McpServerToolType]
public sealed class GetCharacterEquipmentTabsTool(IGw2ApiClient gw2ApiClient)
{
    [McpServerTool(Name = "get_character_equipment_tabs", Title = "Get character equipment tabs", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Gets one exact roster-selected Guild Wars 2 character's complete PvE/WvW combat equipment tabs. Requires account, characters, builds, and inventories permissions. Reports equipment references, not ownership.")]
    public async Task<CharacterEquipmentTabsResult> GetCharacterEquipmentTabsAsync([Description("Exact character name from get_characters.")] string characterName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(characterName)) throw new McpException("characterName is required and must not be blank.");
        try
        {
            var value = await gw2ApiClient.GetCharacterEquipmentTabsAsync(characterName, cancellationToken);
            return new CharacterEquipmentTabsResult(value.CharacterName, "AllEquipmentTabsPveWvwCombatReferences", value.ActiveTab, value.Tabs.Select(tab => new CharacterEquipmentTabResult(tab.Tab, tab.EquipmentTabName, tab.IsActive, tab.Equipment.Select(Map).ToArray())).ToArray(), false, value.IsMetadataComplete, value.Warnings.Select(w => new MetadataWarningResult(w.Code, w.Resolver, w.ReferenceId)).ToArray(), value.EquipmentTabsAsOf, value.EquipmentAsOf, value.ItemsAsOf, value.ItemStatsAsOf, value.SkinsAsOf, value.AsOf, false, "Returned rows are all PvE/WvW combat equipment-tab references reported for the selected character.", "Equipment rows are references, not physical stacks, inventory, or holdings.", "isOwnershipData is false; repeated equipment references do not establish ownership quantities.");
        }
        catch (Gw2ConfigurationException exception) { throw new McpException(exception.Message, exception); }
        catch (HttpRequestException) { throw new McpException("Guild Wars 2 character equipment tabs are unavailable. Try again later."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new McpException("Guild Wars 2 character equipment tabs are unavailable. Try again later."); }
    }

    private static CharacterEquipmentRowResult Map(Gw2EquipmentRow row) => new(row.Slot, new CharacterEquipmentItemResult(row.Item.Id, row.Item.Name, row.Item.Type, row.Item.Subtype, row.Item.Rarity, row.Item.Level), row.Stats is null ? null : new CharacterEquipmentStatResult(row.Stats.Id, row.Stats.Name, row.Stats.Source, row.Stats.Attributes?.Select(attribute => new CharacterEquipmentStatAttributeResult(attribute.Name, attribute.Value)).ToArray()), row.Upgrades.Select(reference => new CharacterEquipmentReferenceResult(reference.Id, reference.Name)).ToArray(), row.Infusions.Select(reference => new CharacterEquipmentReferenceResult(reference.Id, reference.Name)).ToArray(), row.Skin is null ? null : new CharacterEquipmentReferenceResult(row.Skin.Id, row.Skin.Name), row.Binding, row.BoundTo, row.Location, row.ReferenceKind);
}

public sealed record CharacterEquipmentTabsResult([property: JsonRequired] string CharacterName, [property: JsonRequired] string EquipmentScope, [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? ActiveTab, [property: JsonRequired] IReadOnlyList<CharacterEquipmentTabResult> Tabs, [property: JsonRequired] bool IsOwnershipData, [property: JsonRequired] bool IsMetadataComplete, [property: JsonRequired] IReadOnlyList<MetadataWarningResult> Warnings, [property: JsonRequired] DateTimeOffset EquipmentTabsAsOf, [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? EquipmentAsOf, [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? ItemsAsOf, [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? ItemStatsAsOf, [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? SkinsAsOf, [property: JsonRequired] DateTimeOffset AsOf, [property: JsonRequired] bool IsAtomicSnapshot, [property: JsonRequired] string SourceStatement, [property: JsonRequired] string ScopeStatement, [property: JsonRequired] string OwnershipStatement);
public sealed record CharacterEquipmentTabResult([property: JsonRequired] int Tab, [property: JsonRequired] string EquipmentTabName, [property: JsonRequired] bool IsActive, [property: JsonRequired] IReadOnlyList<CharacterEquipmentRowResult> Equipment);
