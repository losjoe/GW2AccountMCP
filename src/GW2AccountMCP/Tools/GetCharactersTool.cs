using System.ComponentModel;
using GW2AccountMCP.Gw2;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GW2AccountMCP.Tools;

[McpServerToolType]
public sealed class GetCharactersTool(IGw2ApiClient gw2ApiClient, TimeProvider timeProvider)
{
    [McpServerTool(
        Name = "get_characters",
        Title = "Get characters",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Gets complete Guild Wars 2 character core summaries. Requires a locally configured GW2 API key with account and characters permissions.")]
    public async Task<CharactersResult> GetCharactersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var characters = await gw2ApiClient.GetCharactersAsync(cancellationToken);
            return new CharactersResult(
                characters.Characters.Select(character => new CharacterResult(
                    character.Name,
                    character.Race,
                    character.Gender,
                    character.Profession,
                    character.Level,
                    character.AgeSeconds,
                    character.Created,
                    character.LastModified,
                    character.Deaths)).ToArray(),
                timeProvider.GetUtcNow());
        }
        catch (Gw2ConfigurationException exception)
        {
            throw new McpException(exception.Message, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new McpException("Guild Wars 2 character summaries are unavailable. Try again later.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException("Guild Wars 2 character summaries are unavailable. Try again later.", exception);
        }
    }
}

public sealed record CharactersResult(IReadOnlyList<CharacterResult> Characters, DateTimeOffset AsOf);
public sealed record CharacterResult(
    string Name,
    string Race,
    string Gender,
    string Profession,
    int Level,
    long AgeSeconds,
    DateTimeOffset Created,
    DateTimeOffset LastModified,
    long Deaths);
