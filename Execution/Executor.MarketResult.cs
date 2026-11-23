using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Restocker.Common;

namespace Restocker.Execution;

public sealed unsafe partial class Executor
{
    private bool TryReadMarketResult(AddonItemSearchResult* result, TimeSpan elapsed)
    {
        if (result == null || !result->IsReady || result->Results == null || Plugin.Instance == null) return false;
        var agent = AgentItemSearch.Instance();
        var search = agent != null ? agent->InfoProxyItemSearch : null;
        var titleItem = ResolveItemFromDialogName(ReadNodeText(result->ItemName)).itemId;
        if (titleItem != 0 ? titleItem != fetchItemId : agent == null || agent->ResultItemId != fetchItemId) return false;

        var rows = result->Results->ListLength;
        var proxyMatches = search != null && search->SearchItemId == fetchItemId;
        uint? proxyOffers = proxyMatches ? search->ListingCount : null;
        var watcher = Plugin.Instance.MarketWatcher;
        if (rows == 0 && MarketResultReadback.IsEmpty(rows, proxyOffers, search != null && search->WaitingForListings, HasEmptyMarketMessage(result))
            && watcher.RecordEmptySearch(fetchItemId))
        {
            LogMarketResult(result, "confirmed empty");
            return true;
        }

        // Give OfferingsReceived priority. The addon and proxy must agree before using live
        // listings from the client as a fallback for a missing event.
        if (elapsed < TimeSpan.FromSeconds(1) || rows <= 0 || !proxyMatches || search->WaitingForListings
            || rows != proxyOffers || rows > search->Listings.Length
            || !watcher.RecordSearchListings(fetchItemId, search->Listings[..rows]))
            return false;

        LogMarketResult(result, "read populated list");
        return true;
    }

    private static bool HasEmptyMarketMessage(AddonItemSearchResult* result)
    {
        var expected = AddonText.NoMarketResults.Trim();
        if (expected.Length == 0) return false;
        if (NodeShows(result->ErrorMessage, expected) || NodeShows(result->HitsMessage, expected)) return true;

        var uld = &result->UldManager;
        if (uld->NodeList == null) return false;
        for (var i = 0; i < uld->NodeListCount; ++i)
        {
            var node = uld->NodeList[i];
            if (node != null && node->GetNodeType() == NodeType.Text && NodeShows((AtkTextNode*)node, expected))
                return true;
        }
        return false;
    }

    private static bool NodeShows(AtkTextNode* node, string text)
        => node != null && node->IsVisible() && MarketResultReadback.MatchesMessage(ReadNodeText(node), text);

    private void LogMarketResult(AddonItemSearchResult* result, string outcome)
    {
        if (result == null || !result->IsReady) return;
        var agent = AgentItemSearch.Instance();
        var search = agent != null ? agent->InfoProxyItemSearch : null;
        var nodes = new List<string>();
        AddText(result->ItemName, "item");
        AddText(result->HitsMessage, "hits");
        AddText(result->ErrorMessage, "error");
        var uld = &result->UldManager;
        if (uld->NodeList != null)
            for (var i = 0; i < uld->NodeListCount; ++i)
            {
                var node = uld->NodeList[i];
                if (node != null && node->GetNodeType() == NodeType.Text)
                    AddText((AtkTextNode*)node, $"node {node->NodeId}");
            }

        log.Debug($"[Restocker] ItemSearchResult {outcome} item={fetchItemId} hq={fetchIsHq} "
            + $"closed before read={searchResultWasClosed} ready={result->IsReady} rows={(result->Results != null ? result->Results->ListLength : -1)} "
            + $"agent item={(agent != null ? agent->ResultItemId : 0)} proxy item={(search != null ? search->SearchItemId : 0)} "
            + $"proxy rows={(search != null ? search->ListingCount : 0)} waiting={search != null && search->WaitingForListings} "
            + $"Addon 1959='{AddonText.NoMarketResults}' nodes=[{string.Join(" | ", nodes)}]");

        void AddText(AtkTextNode* node, string label)
        {
            if (node != null)
                nodes.Add($"{label} visible={node->IsVisible()} '{ReadNodeText(node)}'");
        }
    }
}
