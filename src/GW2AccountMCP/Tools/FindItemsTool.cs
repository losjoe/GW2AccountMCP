using System.ComponentModel;
using GW2AccountMCP.Items;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace GW2AccountMCP.Tools;

[McpServerToolType]
public sealed class FindItemsTool(IItemSearchIndex itemSearchIndex)
{
    private const int DefaultLimit = 10;
    private const int MinimumQueryLength = 2;
    private const int MaximumQueryLength = 100;
    private const int MinimumLimit = 1;
    private const int MaximumLimit = 25;

    [McpServerTool(
        Name = "find_items",
        Title = "Find items",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Resolves bounded English Guild Wars 2 item-name fragments using only the public catalog, without choosing ambiguous names.")]
    public async Task<FindItemsResult> FindItemsAsync(
        [Description("Required English item-name fragment, normalized to 2-100 characters.")] string query,
        [Description("Optional maximum candidates to return, from 1 to 25. Defaults to 10.")] int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (query is null)
        {
            throw new McpException("Query must be 2 to 100 characters.");
        }

        var normalizedQuery = ItemSearchIndex.NormalizeWhitespace(query);
        if (normalizedQuery.Length is < MinimumQueryLength or > MaximumQueryLength)
        {
            throw new McpException("Query must be 2 to 100 characters.");
        }

        var requestedLimit = limit ?? DefaultLimit;
        if (requestedLimit is < MinimumLimit or > MaximumLimit)
        {
            throw new McpException("Limit must be between 1 and 25.");
        }

        try
        {
            var search = await itemSearchIndex.SearchAsync(normalizedQuery, requestedLimit, cancellationToken);
            return new FindItemsResult(normalizedQuery, search.Candidates, search.HasMore);
        }
        catch (Exception exception) when (IsUnavailableFailure(exception, cancellationToken))
        {
            throw new McpException("Guild Wars 2 item search is unavailable. Try again later.", exception);
        }
    }

    private static bool IsUnavailableFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is ItemCacheException
        || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;
}

public sealed record FindItemsResult(string NormalizedQuery, IReadOnlyList<ItemSearchCandidate> Candidates, bool HasMore);
