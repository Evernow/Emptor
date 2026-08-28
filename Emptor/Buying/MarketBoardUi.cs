using System;
using System.Linq;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Emptor.Buying;

/// <summary>
/// Thin unsafe wrappers over the marketboard addons / agent / info-proxy.
/// Every method is safe to call from the framework thread only.
/// </summary>
public static unsafe class MarketBoardUi
{
    private const string ItemSearchAddon = "ItemSearch";
    private const string ItemSearchResultAddon = "ItemSearchResult";
    private const string SelectYesnoAddon = "SelectYesno";

    private static readonly uint[] CandidateListIds =
        Enumerable.Range(1, 80).Select(i => (uint)i).ToArray();

    // ---- open / close -------------------------------------------------------

    public static bool IsBoardOpen() => GameData.GameState.IsAddonReady(ItemSearchAddon);

    public static bool IsResultOpen() => GameData.GameState.IsAddonReady(ItemSearchResultAddon);

    // Note: Emptor never opens the board "cold" (agent->Show()). It only interacts
    // with a real Market Board object it is standing at — see MarketBoardLocator.

    public static void HideBoard()
    {
        var module = AgentModule.Instance();
        var agent = module == null ? null : module->GetAgentByInternalId(AgentId.ItemSearch);
        if (agent != null && agent->IsAgentActive())
            agent->Hide();
    }

    // ---- search -----------------------------------------------------------
    // (search entry lives in SearchInput; the runner drives it directly)

    public readonly record struct SearchProgress(
        bool AgentAvailable, bool Working, int ItemCount,
        bool ExactItemPresent, int ExactIndex, bool ExactRowRendered);

    public static SearchProgress GetSearchProgress(uint itemId)
    {
        var agent = AgentItemSearch.Instance();
        if (agent == null)
            return new SearchProgress(false, false, 0, false, -1, false);

        var working = agent->IsPartialSearching || agent->IsItemPushPending;
        var count = Math.Min((int)agent->ItemCount, 100);
        var index = -1;
        if (agent->ItemBuffer != null)
        {
            for (var i = 0; i < count; i++)
            {
                if (agent->ItemBuffer[i] == itemId)
                {
                    index = i;
                    break;
                }
            }
        }

        var addon = GetItemSearch();
        var rowRendered = index >= 0 && addon != null && addon->ResultsList != null
            && addon->ResultsList->GetItemCount() > index
            && (addon->ResultsList->IsItemInteractionEnabled || addon->ResultsList->IsItemClickEnabled);

        return new SearchProgress(true, working, count, index >= 0, index, rowRendered);
    }

    /// <summary>
    /// Clicks the exact item row in the search results list, which asks the
    /// server to browse that item's listings. Returns false if the row is not
    /// rendered / interactable yet.
    /// </summary>
    public static bool OpenListingsForResultRow(int agentIndex)
    {
        var addon = GetItemSearch();
        if (addon == null || addon->ResultsList == null)
            return false;

        var list = addon->ResultsList;
        if (list->GetItemCount() <= agentIndex)
            return false;
        if (!list->IsItemInteractionEnabled && !list->IsItemClickEnabled)
            return false;

        list->SelectItem(agentIndex, true);
        list->DispatchItemEvent(agentIndex, AtkEventType.ListItemClick);
        return true;
    }

    // ---- listings ---------------------------------------------------------

    public readonly record struct ListingsState(bool Available, uint ItemId, int ListingCount, bool Loading);

    public static ListingsState GetListingsState()
    {
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null)
            return new ListingsState(false, 0, 0, true);

        return new ListingsState(true, proxy->SearchItemId, (int)proxy->ListingCount, proxy->WaitingForListings);
    }

    // ---- purchase -------------------------------------------------------

    /// <summary>
    /// Stages <paramref name="listing"/> as the "last purchased" item (so the
    /// confirm dialog names it) and clicks its row in the result list.
    /// Returns a diagnostic string; null on success.
    /// </summary>
    public static string? BeginPurchase(CandidateListing listing)
    {
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null)
            return "InfoProxyItemSearch unavailable.";

        var count = Math.Min((int)proxy->ListingCount, proxy->Listings.Length);
        var rowIndex = -1;
        for (var i = 0; i < count; i++)
        {
            if (proxy->Listings[i].ListingId == listing.ListingId &&
                proxy->Listings[i].RetainerId == listing.RetainerId)
            {
                rowIndex = i;
                break;
            }
        }

        if (rowIndex < 0)
            return "Listing is no longer in the result cache.";

        var native = proxy->Listings[rowIndex];
        if (!proxy->SetLastPurchasedItem(&native))
            return "Game refused to stage the listing (SetLastPurchasedItem).";

        var addon = GetItemSearchResult();
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return "Result window closed before the row could be clicked.";

        var listId = FindListingComponentId(addon);
        if (listId == null)
            return "Could not find the clickable listing list component.";

        var listComponent = addon->AtkUnitBase.GetComponentListById(listId.Value);
        if (listComponent == null)
            return "Listing list component vanished before selection.";

        if (listComponent->GetItemCount() <= rowIndex)
            return $"Listing row {rowIndex} is not rendered yet ({listComponent->GetItemCount()} rows).";

        listComponent->ScrollToItem((short)rowIndex);
        listComponent->SelectItem(rowIndex, true);
        listComponent->DispatchItemEvent(rowIndex, AtkEventType.ListItemClick);
        listComponent->DispatchItemEvent(rowIndex, AtkEventType.ListItemDoubleClick);
        return null;
    }

    public readonly record struct YesNoState(bool Visible, string PromptText);

    public static YesNoState GetYesNoState()
    {
        var addon = Plugin.GameGui.GetAddonByName<AddonSelectYesno>(SelectYesnoAddon, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible || addon->PromptText == null)
            return new YesNoState(false, string.Empty);

        var text = addon->PromptText->NodeText.ExtractText()
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Trim();
        return new YesNoState(true, text);
    }

    public static void AnswerYesNo(bool yes)
    {
        var addon = Plugin.GameGui.GetAddonByName<AddonSelectYesno>(SelectYesnoAddon, 1);
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return;

        // Faithful click through the real ReceiveEvent path (ECommons), not FireCallbackInt.
        var master = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.SelectYesno((nint)addon);
        if (yes)
            master.Yes();
        else
            master.No();
    }

    // ---- cleanup --------------------------------------------------------

    public static void DismissDialogs()
    {
        foreach (var name in new[] { SelectYesnoAddon, "SelectOk" })
        {
            var addon = Plugin.GameGui.GetAddonByName<AtkUnitBase>(name, 1);
            if (addon != null && addon->IsVisible)
                addon->Close(false);
        }
    }

    public static void ClearStagedPurchase()
    {
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy != null)
            proxy->LastPurchasedMarketboardItem.ListingId = 0;
    }

    // ---- internals -----------------------------------------------------

    private static AddonItemSearch* GetItemSearch()
        => Plugin.GameGui.GetAddonByName<AddonItemSearch>(ItemSearchAddon, 1);

    private static AddonItemSearchResult* GetItemSearchResult()
        => Plugin.GameGui.GetAddonByName<AddonItemSearchResult>(ItemSearchResultAddon, 1);

    private static uint? FindListingComponentId(AddonItemSearchResult* addon)
    {
        uint? best = null;
        var bestCount = 0;
        var bestInteractive = false;
        foreach (var id in CandidateListIds)
        {
            var list = addon->AtkUnitBase.GetComponentListById(id);
            if (list == null)
                continue;

            var count = list->GetItemCount();
            var interactive = list->IsItemInteractionEnabled || list->IsItemClickEnabled;
            var better = interactive != bestInteractive ? interactive : count > bestCount;
            if (!better)
                continue;

            best = id;
            bestCount = count;
            bestInteractive = interactive;
        }

        return best;
    }
}
