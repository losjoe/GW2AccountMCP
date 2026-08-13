using System.ComponentModel;
using GW2AccountMCP.Gw2;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GW2AccountMCP.Tools;

[McpServerToolType]
public sealed class GetAccountTool(IGw2ApiClient gw2ApiClient, TimeProvider timeProvider)
{
    [McpServerTool(
        Name = "get_account",
        Title = "Get account",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Gets basic Guild Wars 2 account facts. Requires a locally configured GW2 API key with account permission.")]
    public async Task<AccountResult> GetAccountAsync(CancellationToken cancellationToken)
    {
        try
        {
            var account = await gw2ApiClient.GetAccountAsync(cancellationToken);
            return new AccountResult(account.Name, account.World, account.Created, account.Access, timeProvider.GetUtcNow());
        }
        catch (Gw2ConfigurationException exception)
        {
            throw new McpException(exception.Message, exception);
        }
    }
}

public sealed record AccountResult(string Name, int World, DateTimeOffset Created, IReadOnlyList<string> Access, DateTimeOffset AsOf);
