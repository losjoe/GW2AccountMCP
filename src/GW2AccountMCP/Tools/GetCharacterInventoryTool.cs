using System.ComponentModel;
using System.Text.Json.Serialization;
using GW2AccountMCP.Gw2;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GW2AccountMCP.Tools;

[McpServerToolType]
public sealed class GetCharacterInventoryTool(IGw2ApiClient gw2ApiClient, TimeProvider timeProvider)
{
    [McpServerTool(Name = "get_character_inventory", Title = "Get character inventory", ReadOnly = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Gets one roster-selected Guild Wars 2 character's physical equipped-bag stacks. These stacks are already represented by get_account_holdings character-bag contributions and must not be added as a second ownership source.")]
    public async Task<CharacterInventoryResult> GetCharacterInventoryAsync(
        [Description("Exact character name from get_characters.")] string characterName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(characterName)) throw new McpException("characterName is required and must not be blank.");
        try
        {
            var inventory = await gw2ApiClient.GetCharacterInventoryAsync(characterName, cancellationToken);
            return new CharacterInventoryResult(inventory.CharacterName, "SelectedCharacterPhysicalBags",
                new CharacterInventoryCapacityResult(inventory.Capacity.BagPositions, inventory.Capacity.EquippedBags, inventory.Capacity.TotalSlots, inventory.Capacity.OccupiedSlots, inventory.Capacity.EmptySlots),
                inventory.Bags.Select(bag => new CharacterInventoryBagResult(bag.BagPosition,
                    bag.Bag is null ? null : new CharacterInventoryBagDetailsResult(bag.Bag.Id, bag.Bag.Name, bag.Bag.Size),
                    bag.Slots.Select(slot => new CharacterInventorySlotResult(slot.SlotPosition, slot.Stack is null ? null : ToStack(slot.Stack))).ToArray())).ToArray(),
                inventory.IsMetadataComplete, inventory.Warnings.Select(warning => new MetadataWarningResult(warning.Code, warning.Resolver, warning.ReferenceId)).ToArray(), timeProvider.GetUtcNow());
        }
        catch (Gw2ConfigurationException exception) { throw new McpException(exception.Message, exception); }
        catch (HttpRequestException) { throw new McpException("Guild Wars 2 character inventory is unavailable. Try again later."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new McpException("Guild Wars 2 character inventory is unavailable. Try again later."); }
    }

    private static CharacterInventoryStackResult ToStack(Gw2InventoryStack stack) => new(
        new CharacterInventoryItemResult(stack.Item.Id, stack.Item.Name, stack.Item.Type, stack.Item.Subtype, stack.Item.Rarity, stack.Item.Level), stack.Count, stack.Charges,
        stack.Stats is null ? null : new CharacterInventoryStatResult(stack.Stats.Id, stack.Stats.Name, stack.Stats.Source, stack.Stats.Attributes?.Select(attribute => new CharacterInventoryStatAttributeResult(attribute.Name, attribute.Value)).ToArray()),
        stack.Upgrades.Select(reference => new CharacterInventoryReferenceResult(reference.Id, reference.Name)).ToArray(),
        stack.Infusions.Select(reference => new CharacterInventoryReferenceResult(reference.Id, reference.Name)).ToArray(),
        stack.Skin is null ? null : new CharacterInventoryReferenceResult(stack.Skin.Id, stack.Skin.Name), stack.Binding, stack.BoundTo);
}

public sealed record CharacterInventoryResult(
    [property: JsonRequired] string CharacterName,
    [property: JsonRequired] string InventoryScope,
    [property: JsonRequired] CharacterInventoryCapacityResult Capacity,
    [property: JsonRequired] IReadOnlyList<CharacterInventoryBagResult> Bags,
    [property: JsonRequired] bool IsMetadataComplete,
    [property: JsonRequired] IReadOnlyList<MetadataWarningResult> Warnings,
    [property: JsonRequired] DateTimeOffset AsOf);
public sealed record CharacterInventoryCapacityResult(int BagPositions, int EquippedBags, int TotalSlots, int OccupiedSlots, int EmptySlots);
public sealed record CharacterInventoryBagResult(int BagPosition, [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] CharacterInventoryBagDetailsResult? Bag, IReadOnlyList<CharacterInventorySlotResult> Slots);
public sealed record CharacterInventoryBagDetailsResult(long Id, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name, int Size);
public sealed record CharacterInventorySlotResult(int SlotPosition, [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] CharacterInventoryStackResult? Stack);
public sealed record CharacterInventoryStackResult(CharacterInventoryItemResult Item, long Count, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Charges, [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] CharacterInventoryStatResult? Stats, IReadOnlyList<CharacterInventoryReferenceResult> Upgrades, IReadOnlyList<CharacterInventoryReferenceResult> Infusions, [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] CharacterInventoryReferenceResult? Skin, [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Binding, [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? BoundTo);
public sealed record CharacterInventoryItemResult(long Id, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Type, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Subtype, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Rarity, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Level);
public sealed record CharacterInventoryStatResult(long Id, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name, string Source, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<CharacterInventoryStatAttributeResult>? Attributes);
public sealed record CharacterInventoryStatAttributeResult(string Name, int Value);
public sealed record CharacterInventoryReferenceResult(long Id, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name);
