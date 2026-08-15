using System.ComponentModel;
using System.Text.Json.Serialization;
using GW2AccountMCP.Gw2;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GW2AccountMCP.Tools;

[McpServerToolType]
public sealed class GetCharacterEquipmentTool(IGw2ApiClient gw2ApiClient, TimeProvider timeProvider)
{
    [McpServerTool(Name = "get_character_equipment", Title = "Get character equipment", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Gets one roster-selected Guild Wars 2 character's active combat equipment. Requires a locally configured GW2 API key with account, characters, builds, and inventories permissions.")]
    public async Task<CharacterEquipmentResult> GetCharacterEquipmentAsync(
        [Description("Exact character name from get_characters.")] string characterName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(characterName)) throw new McpException("characterName is required and must not be blank.");
        try
        {
            var equipment = await gw2ApiClient.GetCharacterEquipmentAsync(characterName, cancellationToken);
            return new CharacterEquipmentResult(equipment.CharacterName, equipment.Tab, equipment.EquipmentTabName,
                equipment.Equipment.Select(row => new CharacterEquipmentRowResult(row.Slot,
                    new CharacterEquipmentItemResult(row.Item.Id, row.Item.Name, row.Item.Type, row.Item.Subtype, row.Item.Rarity, row.Item.Level),
                    row.Stats is null ? null : new CharacterEquipmentStatResult(row.Stats.Id, row.Stats.Name, row.Stats.Source, row.Stats.Attributes?.Select(attribute => new CharacterEquipmentStatAttributeResult(attribute.Name, attribute.Value)).ToArray()),
                    row.Upgrades.Select(reference => new CharacterEquipmentReferenceResult(reference.Id, reference.Name)).ToArray(),
                    row.Infusions.Select(reference => new CharacterEquipmentReferenceResult(reference.Id, reference.Name)).ToArray(),
                    row.Skin is null ? null : new CharacterEquipmentReferenceResult(row.Skin.Id, row.Skin.Name), row.Binding, row.BoundTo, row.Location, row.ReferenceKind)).ToArray(),
                false, equipment.IsMetadataComplete, equipment.Warnings.Select(warning => new MetadataWarningResult(warning.Code, warning.Resolver, warning.ReferenceId)).ToArray(), timeProvider.GetUtcNow());
        }
        catch (Gw2ConfigurationException exception) { throw new McpException(exception.Message, exception); }
        catch (HttpRequestException) { throw new McpException("Guild Wars 2 character equipment is unavailable. Try again later."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new McpException("Guild Wars 2 character equipment is unavailable. Try again later."); }
    }
}

public sealed record CharacterEquipmentResult(
    [property: JsonRequired] string CharacterName,
    [property: JsonRequired] int Tab,
    [property: JsonRequired] string EquipmentTabName,
    [property: JsonRequired] IReadOnlyList<CharacterEquipmentRowResult> Equipment,
    [property: JsonRequired] bool IsOwnershipData,
    [property: JsonRequired] bool IsMetadataComplete,
    [property: JsonRequired] IReadOnlyList<MetadataWarningResult> Warnings,
    [property: JsonRequired] DateTimeOffset AsOf);
public sealed record CharacterEquipmentRowResult(
    [property: JsonRequired] string Slot,
    [property: JsonRequired] CharacterEquipmentItemResult Item,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] CharacterEquipmentStatResult? Stats,
    [property: JsonRequired] IReadOnlyList<CharacterEquipmentReferenceResult> Upgrades,
    [property: JsonRequired] IReadOnlyList<CharacterEquipmentReferenceResult> Infusions,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] CharacterEquipmentReferenceResult? Skin,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Binding,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? BoundTo,
    [property: JsonRequired] string Location,
    [property: JsonRequired] string ReferenceKind);
public sealed record CharacterEquipmentItemResult(long Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Subtype,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Rarity,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Level);
public sealed record CharacterEquipmentStatResult(long Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name,
    string Source,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<CharacterEquipmentStatAttributeResult>? Attributes);
public sealed record CharacterEquipmentStatAttributeResult(string Name, int Value);
public sealed record CharacterEquipmentReferenceResult(long Id, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name);
