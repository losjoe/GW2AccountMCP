using System.ComponentModel;
using System.Text.Json.Serialization;
using GW2AccountMCP.Gw2;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GW2AccountMCP.Tools;

[McpServerToolType]
public sealed class GetCharacterBuildTool(IGw2ApiClient gw2ApiClient, TimeProvider timeProvider)
{
    [McpServerTool(
        Name = "get_character_build",
        Title = "Get character build",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Gets one roster-selected Guild Wars 2 character's active build. Requires a locally configured GW2 API key with account, characters, and builds permissions.")]
    public async Task<CharacterBuildResult> GetCharacterBuildAsync(
        [Description("Exact character name from get_characters.")] string characterName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(characterName))
        {
            throw new McpException("characterName is required and must not be blank.");
        }

        try
        {
            var build = await gw2ApiClient.GetCharacterBuildAsync(characterName, cancellationToken);
            return new CharacterBuildResult(
                build.CharacterName,
                build.Tab,
                build.BuildName,
                build.Profession,
                build.Specializations.Select(slot => new CharacterBuildSpecializationResult(
                    ToReference(slot.Specialization),
                    slot.SelectedTraits.Select(ToReference).ToArray())).ToArray(),
                new CharacterBuildSkillsResult(ToReference(build.TerrestrialSkills.Heal), build.TerrestrialSkills.Utilities.Select(ToReference).ToArray(), ToReference(build.TerrestrialSkills.Elite)),
                new CharacterBuildSkillsResult(ToReference(build.AquaticSkills.Heal), build.AquaticSkills.Utilities.Select(ToReference).ToArray(), ToReference(build.AquaticSkills.Elite)),
                build.Pets is null ? null : new CharacterBuildPetsResult(build.Pets.Terrestrial.Select(ToReference).ToArray(), build.Pets.Aquatic.Select(ToReference).ToArray()),
                build.Legends is null ? null : new CharacterBuildLegendsResult(build.Legends.Terrestrial.Select(ToLegendReference).ToArray(), build.Legends.Aquatic.Select(ToLegendReference).ToArray()),
                build.IsMetadataComplete,
                build.Warnings.Select(warning => new MetadataWarningResult(warning.Code, warning.Resolver, warning.ReferenceId)).ToArray(),
                timeProvider.GetUtcNow());
        }
        catch (Gw2ConfigurationException exception)
        {
            throw new McpException(exception.Message, exception);
        }
        catch (HttpRequestException)
        {
            throw new McpException("Guild Wars 2 character build is unavailable. Try again later.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException("Guild Wars 2 character build is unavailable. Try again later.");
        }
    }

    private static CharacterBuildReferenceResult? ToReference(Gw2NumericReference? reference) => reference is null ? null : new CharacterBuildReferenceResult(reference.Id, reference.Name);
    private static CharacterBuildLegendResult? ToLegendReference(Gw2LegendReference? reference) => reference is null ? null : new CharacterBuildLegendResult(reference.Id, reference.Code, ToReference(reference.SwapSkill));
}

public sealed record CharacterBuildResult(
    [property: JsonRequired] string CharacterName,
    [property: JsonRequired] int Tab,
    [property: JsonRequired] string BuildName,
    [property: JsonRequired] string Profession,
    [property: JsonRequired] IReadOnlyList<CharacterBuildSpecializationResult> Specializations,
    [property: JsonRequired] CharacterBuildSkillEnvironmentsResult Skills,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] CharacterBuildPetsResult? Pets,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] CharacterBuildLegendsResult? Legends,
    [property: JsonRequired] bool IsMetadataComplete,
    [property: JsonRequired] IReadOnlyList<MetadataWarningResult> Warnings,
    [property: JsonRequired] DateTimeOffset AsOf)
{
    public CharacterBuildResult(
        string characterName, int tab, string buildName, string profession,
        IReadOnlyList<CharacterBuildSpecializationResult> specializations,
        CharacterBuildSkillsResult terrestrialSkills, CharacterBuildSkillsResult aquaticSkills,
        CharacterBuildPetsResult? pets, CharacterBuildLegendsResult? legends,
        bool isMetadataComplete, IReadOnlyList<MetadataWarningResult> warnings, DateTimeOffset asOf)
        : this(characterName, tab, buildName, profession, specializations, new CharacterBuildSkillEnvironmentsResult(terrestrialSkills, aquaticSkills), pets, legends, isMetadataComplete, warnings, asOf) { }
}

public sealed record CharacterBuildSpecializationResult(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] CharacterBuildReferenceResult? Specialization,
    IReadOnlyList<CharacterBuildReferenceResult?> SelectedTraits);
public sealed record CharacterBuildSkillEnvironmentsResult(CharacterBuildSkillsResult Terrestrial, CharacterBuildSkillsResult Aquatic);
public sealed record CharacterBuildSkillsResult(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] CharacterBuildReferenceResult? Heal,
    IReadOnlyList<CharacterBuildReferenceResult?> Utilities,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] CharacterBuildReferenceResult? Elite);
public sealed record CharacterBuildPetsResult(IReadOnlyList<CharacterBuildReferenceResult?> Terrestrial, IReadOnlyList<CharacterBuildReferenceResult?> Aquatic);
public sealed record CharacterBuildLegendsResult(IReadOnlyList<CharacterBuildLegendResult?> Terrestrial, IReadOnlyList<CharacterBuildLegendResult?> Aquatic);
public sealed record CharacterBuildReferenceResult(int Id, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Name);
public sealed record CharacterBuildLegendResult(string Id, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Code, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] CharacterBuildReferenceResult? SwapSkill);
public sealed record MetadataWarningResult(string Code, string Resolver, string ReferenceId);
