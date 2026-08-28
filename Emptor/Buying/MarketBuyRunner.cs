using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Network.Structures;
using Dalamud.Plugin.Services;
using Emptor.Capture;
using Emptor.GameData;

namespace Emptor.Buying;

/// <summary>Live status snapshot for the UI.</summary>
public sealed record RunnerStatus(
    string Phase,
    string Activity,
    string? WaitReason,
    double WaitRemainingMs,
    double WaitTotalMs,
    int ItemNumber,
    int ItemCount,
    string ItemName,
    int Bought,
    int Requested,
    long GilSpent);

/// <summary>
/// Owns the single active buy order and drives the marketboard the way a person
/// would: dismount, walk to / interact with a real Market Board, type the search,
/// read the listings, buy — with human-variable think-time at each step.
/// </summary>
public sealed class MarketBuyRunner : IDisposable
{
    private enum Phase
    {
        Idle, ItemNext, ItemBegin, BlockedWait,
        Dismount, LocateBoard, TravelToBoard, TravelWait, NavigateBoard, InteractBoard, BoardOpenWait,
        Think,
        PrepSearch, TypeSearch, SubmitSearch, SearchWait,
        OpenListings, ClickResultRow, ListingsWait, Read,
        ChooseAndStage, StageListing, ConfirmWait, ConfirmYes, ResultWait,
    }

    private static readonly TimeSpan BoardOpenTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan NavigateTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DismountTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(14);
    private static readonly TimeSpan ListingsTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ListingsStable = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan BlockedTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LifestreamTravelTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan LifestreamStartGrace = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan LifestreamDoneGrace = TimeSpan.FromSeconds(8);

    private readonly PurchaseConfirmationWatcher watcher = new();
    private readonly BehaviorRecorder recorder;
    private bool startedCaptureForThisOrder;

    private BuyOrder? order;
    private Phase phase = Phase.Idle;
    private DateTime phaseEnteredUtc;

    private DateTime thinkUntil;
    private DateTime thinkStartUtc;
    private double thinkTotalMs;
    private string thinkReason = string.Empty;
    private Phase thinkNext;

    private const float MovementGuardYalms = 6f;

    private IGameObject? board;
    private DateTime interactSentUtc;
    private Vector3 boardApproachStartPos;
    private int listingCountSeen;
    private DateTime listingCountChangedUtc;

    // Lifestream "/li ..." travel to a board
    private bool triedLifestreamThisItem;
    private bool lifestreamTravelActive;
    private bool liSawBusy;
    private DateTime liLastBusyUtc;
    private string? travelDestLabel;

    // per-item working state
    private int itemIndex;
    private BuyRequestItem? item;
    private BuyItemResult? result;
    private uint resolvedItemId;
    private List<CandidateListing> acceptable = new();
    private List<CandidateListing> rejected = new();
    private readonly HashSet<ulong> boughtListingIds = new();
    private int boughtQty;
    private int retypeCount;
    private long stagedGil;
    private int typedChars;
    private DateTime nextKeystrokeUtc;
    private TypingModel typing = new();
    private CandidateListing staged;

    public event Action<BuyOrder>? OrderFinished;
    public Action<string>? Log;

    public MarketBuyRunner(BehaviorRecorder recorder)
    {
        this.recorder = recorder;
        Plugin.MarketBoard.PurchaseRequested += OnPurchaseRequested;
        Plugin.MarketBoard.ItemPurchased += OnItemPurchased;
        Plugin.Framework.Update += OnUpdate;
    }

    public bool IsBusy => order is not null;
    public BuyOrder? Active => order;

    /// <summary>Live, human-readable snapshot of what the runner is doing right now.</summary>
    public RunnerStatus GetStatus()
    {
        if (order is null)
            return new RunnerStatus("Idle", "Idle.", null, 0, 0, 0, 0, string.Empty, 0, 0, 0);

        var waiting = phase == Phase.Think;
        var remain = waiting ? Math.Max(0, (thinkUntil - DateTime.UtcNow).TotalMilliseconds) : 0;
        var reason = waiting ? FriendlyWait(thinkReason) : null;
        var activity = waiting ? DescribeThink(thinkReason, thinkNext) : DescribePhase(phase);

        return new RunnerStatus(
            waiting ? $"Think → {thinkNext}" : phase.ToString(),
            activity,
            reason,
            remain,
            waiting ? thinkTotalMs : 0,
            itemIndex < 0 ? 0 : itemIndex + 1,
            order.Request.Items.Count,
            result?.ItemName ?? item?.ItemName ?? string.Empty,
            boughtQty,
            item?.Quantity ?? 0,
            order.TotalGilSpent);
    }

    private string DescribePhase(Phase p) => p switch
    {
        Phase.ItemNext or Phase.ItemBegin => "Preparing…",
        Phase.BlockedWait => $"Waiting — {GameState.GetBlockReason() ?? "blocked"}",
        Phase.Dismount => "Dismounting…",
        Phase.LocateBoard => "Looking for a Market Board…",
        Phase.TravelToBoard => travelDestLabel is null ? "Heading to a Market Board…" : $"Heading to {travelDestLabel}…",
        Phase.TravelWait => travelDestLabel is null ? "Travelling to a Market Board…" : $"Travelling to {travelDestLabel}…",
        Phase.NavigateBoard => "Walking to the Market Board…",
        Phase.InteractBoard => "Opening the Market Board…",
        Phase.BoardOpenWait => "Waiting for the Market Board window…",
        Phase.PrepSearch => "Focusing the search box…",
        Phase.TypeSearch => $"Typing “{result?.ItemName}” ({typedChars}/{result?.ItemName.Length ?? 0})…",
        Phase.SubmitSearch => "Pressing Enter to search…",
        Phase.SearchWait => "Waiting for search results…",
        Phase.OpenListings or Phase.ClickResultRow => "Opening the item's listings…",
        Phase.ListingsWait => "Loading listings…",
        Phase.Read => "Reading the listings…",
        Phase.ChooseAndStage => "Choosing which listing to buy…",
        Phase.StageListing => "Selecting the listing…",
        Phase.ConfirmWait => "Waiting for the purchase dialog…",
        Phase.ConfirmYes => "Confirming the purchase…",
        Phase.ResultWait => "Waiting for the server to confirm the purchase…",
        _ => p.ToString(),
    };

    private static string DescribeThink(string reason, Phase next) => reason switch
    {
        "orient" => "Looking over the Market Board window…",
        "before-interact" => "Approaching the Market Board…",
        "decide-travel" => "No board here — deciding to head to one…",
        "click-search-field" => "Clicking into the search box…",
        "reach-for-search" => "Moving to the Search button…",
        "read-results" => "Reading the search results…",
        "reach-for-result" => "Picking the item from the results…",
        "scan-listings" => "Scanning the listings…",
        "reach-for-listing" => "Deciding which listing to take…",
        "read-prompt" => "Reading the purchase confirmation…",
        "between-purchases" => "Pausing before buying the next listing…",
        "after-last-purchase" => "Wrapping up this item…",
        "between-items" => "Moving on to the next item…",
        _ => $"Pausing before {next}…",
    };

    private static string FriendlyWait(string reason) => reason switch
    {
        "between-purchases" => "between-purchases pause",
        "between-items" => "between-items pause",
        "read-results" => "reading results",
        "scan-listings" => "scanning listings",
        "read-prompt" => "reading the prompt",
        "orient" => "orienting",
        "decide-travel" => "deciding to travel",
        _ => reason,
    };

    public string? TryStart(BuyOrder newOrder)
    {
        if (order is not null)
            return "An order is already running.";
        if (newOrder.Request.Items.Count == 0)
            return "The order has no items.";

        order = newOrder;
        order.State = OrderState.Running;
        order.StartedUtc = DateTime.UtcNow;
        order.Message = "Running.";
        order.Items.Clear();
        itemIndex = -1;
        boughtListingIds.Clear();
        lifestreamTravelActive = false;
        travelDestLabel = null;
        Goto(Phase.ItemNext);

        startedCaptureForThisOrder = false;
        if (Plugin.Instance.Configuration.CaptureAutomatedRuns && !recorder.IsRecording)
        {
            recorder.Start("automated");
            startedCaptureForThisOrder = true;
        }
        if (recorder.IsRecording)
            recorder.RunnerEvent("order-start", new() { ["orderId"] = order.OrderId, ["items"] = order.Request.Items.Count });

        Log?.Invoke($"Order {order.OrderId} started with {order.Request.Items.Count} item(s).");
        return null;
    }

    public void RequestCancel()
    {
        if (order is not null)
        {
            order.CancelRequested = true;
            Log?.Invoke("Cancel requested.");
        }
    }

    public void Dispose()
    {
        Plugin.MarketBoard.PurchaseRequested -= OnPurchaseRequested;
        Plugin.MarketBoard.ItemPurchased -= OnItemPurchased;
        Plugin.Framework.Update -= OnUpdate;
    }

    private void OnPurchaseRequested(IMarketBoardPurchaseHandler handler) => watcher.OnPurchaseRequested(handler);

    private void OnItemPurchased(IMarketBoardPurchase purchase) => watcher.OnItemPurchased(purchase, GameState.GetGil());

    // ---- state machine -------------------------------------------------

    private void OnUpdate(IFramework framework)
    {
        if (order is null)
            return;
        try
        {
            Step();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Emptor] Runner step threw.");
            FailOrder($"Internal error: {ex.Message}");
        }
    }

    private bool CancelledNow() => order!.CancelRequested;

    private void Step()
    {
        switch (phase)
        {
            case Phase.ItemNext:
                itemIndex++;
                if (CancelledNow()) { FinishOrder(OrderState.Cancelled, "Cancelled."); return; }
                if (itemIndex >= order!.Request.Items.Count) { FinishOrder(OrderState.Completed, "Completed."); return; }
                item = order.Request.Items[itemIndex];
                result = new BuyItemResult { ItemId = item.ItemId, RequestedQuantity = item.Quantity };
                order.Items.Add(result);
                acceptable = new();
                rejected = new();
                boughtQty = 0;
                retypeCount = 0;
                triedLifestreamThisItem = false;
                Goto(Phase.ItemBegin);
                return;

            case Phase.ItemBegin:
            {
                resolvedItemId = item!.ItemId;
                if (resolvedItemId == 0 && !string.IsNullOrWhiteSpace(item.ItemName))
                    resolvedItemId = ItemResolver.ResolveExact(item.ItemName!);
                if (resolvedItemId == 0 || !ItemResolver.IsMarketable(resolvedItemId))
                {
                    StopItem(StopReason.ItemUnresolved, "Item name did not resolve to a single marketable item.");
                    return;
                }
                result!.ItemId = resolvedItemId;
                result.ItemName = ItemResolver.GetName(resolvedItemId);

                if (GameState.GetBlockReason() is { } block && !PlayerActions.IsMounted)
                {
                    Log?.Invoke($"Blocked: {block}");
                    Goto(Phase.BlockedWait);
                    return;
                }

                // Skip straight to searching ONLY if the board is open AND we can
                // see a real Market Board object right next to us (e.g. a
                // multi-item order where item 1 already opened it). A stale board
                // window with no board nearby is NOT good enough — go locate one.
                if (MarketBoardUi.IsBoardOpen() &&
                    MarketBoardLocator.FindNearest(MarketBoardLocator.InteractDistance + 1.5f) is not null)
                {
                    ThinkThen(HumanTiming.OrientAfterOpen(), Phase.PrepSearch, "orient");
                    return;
                }
                Goto(PlayerActions.IsMounted ? Phase.Dismount : Phase.LocateBoard);
                return;
            }

            case Phase.BlockedWait:
            {
                if (CancelledNow()) { StopItem(StopReason.Cancelled, "Cancelled while blocked."); return; }
                if (GameState.GetBlockReason() is null) { Goto(Phase.LocateBoard); return; }
                if (Elapsed > BlockedTimeout) StopItem(StopReason.Blocked, "Still blocked after waiting.");
                return;
            }

            case Phase.Dismount:
            {
                if (!PlayerActions.IsMounted) { Goto(Phase.LocateBoard); return; }
                PlayerActions.Dismount();
                if (Elapsed > DismountTimeout) StopItem(StopReason.Blocked, "Could not dismount.");
                return;
            }

            case Phase.LocateBoard:
            {
                var skip = order!.Request.SkipTravel;

                // Only ever look for a board we could actually reach. With
                // skipTravel we require one we're basically standing at — never
                // pathfind, never open the board UI "cold".
                board = MarketBoardLocator.FindNearest(
                    skip ? MarketBoardLocator.InteractDistance + 1.5f : MarketBoardLocator.NavigateSearchRadius);

                if (board is null)
                {
                    // Nothing in range. If travel is allowed and Lifestream is
                    // here, ride it to the (optionally pinned) city's board, then
                    // come back through LocateBoard to interact with it.
                    if (!skip
                        && !triedLifestreamThisItem
                        && Plugin.Instance.Configuration.UseLifestreamTravel
                        && Lifestream.Available)
                    {
                        ThinkThen(HumanTiming.DecideToTravel(), Phase.TravelToBoard, "decide-travel");
                        return;
                    }

                    StopItem(
                        skip ? StopReason.OpenFailed
                        : triedLifestreamThisItem ? StopReason.TravelFailed
                        : StopReason.NoBoardInZone,
                        skip ? "Not standing at a Market Board (the caller disabled travel)."
                        : triedLifestreamThisItem ? "Lifestream did not reach a Market Board."
                        : Lifestream.Available ? "No Market Board nearby."
                        : "No Market Board nearby — install Lifestream or walk to one.");
                    return;
                }

                var dist = MarketBoardLocator.DistanceTo(board) ?? 999f;
                if (dist <= MarketBoardLocator.InteractDistance)
                {
                    // Standing at a real board. If its window is already up (a
                    // multi-item order), don't re-interact — that can toggle it
                    // shut. Otherwise interact to open it.
                    if (MarketBoardUi.IsBoardOpen())
                    {
                        ThinkThen(HumanTiming.OrientAfterOpen(), Phase.PrepSearch, "orient");
                        return;
                    }
                    boardApproachStartPos = GameState.PlayerPosition();
                    ThinkThen(HumanTiming.BeforeInteractBoard(), Phase.InteractBoard, "before-interact");
                    return;
                }

                if (skip)
                {
                    StopItem(StopReason.OpenFailed, $"A Market Board is {dist:0}y away — too far to reach without travelling.");
                    return;
                }

                if (Plugin.Instance.Configuration.UseNavigation && Navigation.Available && Navigation.IsReady())
                {
                    boardApproachStartPos = GameState.PlayerPosition();
                    Navigation.MoveCloseTo(board.Position, 3.4f);
                    Goto(Phase.NavigateBoard);
                    return;
                }

                StopItem(StopReason.OpenFailed, $"Nearest Market Board is {dist:0}y away — walk to it or enable navigation.");
                return;
            }

            case Phase.TravelToBoard:
            {
                if (CancelledNow()) { StopItem(StopReason.Cancelled, "Cancelled before travelling."); return; }

                var wanted = order!.Request.City;
                var city = MarketCities.Resolve(wanted);
                if (!string.IsNullOrWhiteSpace(wanted) && city is null)
                {
                    StopItem(StopReason.TravelFailed,
                        $"Unknown city \"{wanted}\" — known: {MarketCities.KnownKeys()}.");
                    return;
                }

                // Let any prior Lifestream action (e.g. the caller's world hop)
                // wind down before we issue our own command.
                if (Lifestream.IsBusy())
                {
                    if (Elapsed > LifestreamTravelTimeout)
                        StopItem(StopReason.TravelFailed, "Lifestream stayed busy — could not start travel to a board.");
                    return;
                }

                var arg = city?.LifestreamArg ?? "mb";
                var dest = city is null ? "a Market Board (\"/li mb\")" : $"the {city.Display} Market Board";
                travelDestLabel = city is null ? null : $"{city.Display}";

                if (!Lifestream.RunLiCommand(arg))
                {
                    StopItem(StopReason.TravelFailed, $"Could not dispatch \"/li {arg}\" (is Lifestream loaded?).");
                    return;
                }

                triedLifestreamThisItem = true;
                lifestreamTravelActive = true;
                liSawBusy = false;
                liLastBusyUtc = DateTime.UtcNow;
                Log?.Invoke($"No board nearby — travelling to {dest}.");
                Goto(Phase.TravelWait);
                return;
            }

            case Phase.TravelWait:
            {
                if (CancelledNow())
                {
                    if (lifestreamTravelActive) Lifestream.Abort();
                    lifestreamTravelActive = false;
                    StopItem(StopReason.Cancelled, "Cancelled while travelling.");
                    return;
                }

                var busy = Lifestream.IsBusy();
                if (busy)
                    liLastBusyUtc = DateTime.UtcNow;
                liSawBusy |= busy;

                // Arrived within reach of a board — hand back to LocateBoard,
                // which interacts or does a short vnav hop.
                if (MarketBoardLocator.FindNearest(MarketBoardLocator.NavigateSearchRadius) is not null)
                {
                    lifestreamTravelActive = false;
                    Goto(Phase.LocateBoard);
                    return;
                }

                if (Elapsed > LifestreamTravelTimeout)
                {
                    if (lifestreamTravelActive) Lifestream.Abort();
                    lifestreamTravelActive = false;
                    StopItem(StopReason.TravelFailed, "Timed out travelling to a Market Board.");
                    return;
                }

                // Command never took.
                if (!liSawBusy && Elapsed > LifestreamStartGrace)
                {
                    lifestreamTravelActive = false;
                    StopItem(StopReason.TravelFailed, "Lifestream did not start travelling.");
                    return;
                }

                // Lifestream ran and has been idle a while, but we're not near a
                // board (bad route, or it dropped us short).
                if (liSawBusy && !busy && !GameState.IsBetweenAreas
                    && DateTime.UtcNow - liLastBusyUtc > LifestreamDoneGrace)
                {
                    lifestreamTravelActive = false;
                    StopItem(StopReason.TravelFailed, "Lifestream finished travelling but no Market Board is in reach.");
                    return;
                }

                return;
            }

            case Phase.NavigateBoard:
            {
                if (CancelledNow()) { Navigation.Stop(); StopItem(StopReason.Cancelled, "Cancelled while walking."); return; }
                var dist = board is null ? 999f : MarketBoardLocator.DistanceTo(board) ?? 999f;
                if (dist <= MarketBoardLocator.InteractDistance)
                {
                    Navigation.Stop();
                    boardApproachStartPos = GameState.PlayerPosition(); // reset guard baseline after the walk
                    ThinkThen(HumanTiming.BeforeInteractBoard(), Phase.InteractBoard, "before-interact");
                    return;
                }
                if (Elapsed > NavigateTimeout || (!Navigation.IsRunning() && Elapsed > TimeSpan.FromSeconds(3)))
                {
                    Navigation.Stop();
                    StopItem(StopReason.OpenFailed, $"Navigation stopped {dist:0}y from the Market Board.");
                }
                return;
            }

            case Phase.InteractBoard:
            {
                if (board is not null)
                    MarketBoardLocator.Interact(board);
                interactSentUtc = DateTime.UtcNow;
                Goto(Phase.BoardOpenWait);
                return;
            }

            case Phase.BoardOpenWait:
            {
                if (MarketBoardUi.IsBoardOpen())
                {
                    ThinkThen(HumanTiming.OrientAfterOpen(), Phase.PrepSearch, "orient");
                    return;
                }

                // Safety: if interacting made the character run off (wrong object,
                // out-of-range auto-approach), abort rather than travel blindly.
                var drift = Vector3.Distance(GameState.PlayerPosition(), boardApproachStartPos);
                if (boardApproachStartPos != default && drift > MovementGuardYalms)
                {
                    StopItem(StopReason.OpenFailed, $"Character moved {drift:0}y while opening the board — aborting.");
                    return;
                }

                if (DateTime.UtcNow - interactSentUtc > TimeSpan.FromSeconds(2.5) && board is not null && Elapsed < BoardOpenTimeout)
                {
                    MarketBoardLocator.Interact(board);
                    interactSentUtc = DateTime.UtcNow;
                    return;
                }
                if (Elapsed > BoardOpenTimeout)
                    StopItem(StopReason.OpenFailed, "The Market Board window did not open.");
                return;
            }

            case Phase.Think:
            {
                if (CancelledNow())
                    thinkUntil = DateTime.UtcNow;
                if (DateTime.UtcNow >= thinkUntil)
                    Goto(thinkNext);
                return;
            }

            case Phase.PrepSearch:
            {
                if (!SearchInput.Ready())
                {
                    if (Elapsed > SearchTimeout) StopItem(StopReason.SearchFailed, "Search box never became ready.");
                    return;
                }
                if (!Keyboard.Available)
                {
                    StopItem(StopReason.SearchFailed, "Could not find the game window to send keystrokes.");
                    return;
                }
                if (!SearchInput.Prepare())
                    return; // one prep step per tick (mode / focus / clear)
                typedChars = 0;
                typing = new TypingModel();
                nextKeystrokeUtc = DateTime.UtcNow + typing.Initiation();
                ThinkThen(HumanTiming.ClickIntoField(), Phase.TypeSearch, "click-search-field");
                return;
            }

            case Phase.TypeSearch:
            {
                if (DateTime.UtcNow < nextKeystrokeUtc)
                    return;

                var name = result!.ItemName;
                SearchInput.TypeChar(name[typedChars]);
                typedChars++;

                if (typedChars >= name.Length)
                {
                    if (recorder.IsRecording)
                        recorder.RunnerEvent("typed", new() { ["boxText"] = SearchInput.Observe().Text });
                    ThinkThen(HumanTiming.ReachForButton(), Phase.SubmitSearch, "reach-for-search");
                }
                else
                {
                    nextKeystrokeUtc = DateTime.UtcNow + typing.NextInterval(name[typedChars - 1], name[typedChars]);
                }
                return;
            }

            case Phase.SubmitSearch:
            {
                SearchInput.Submit();
                if (recorder.IsRecording)
                    recorder.RunnerEvent("submit-enter", new() { ["boxText"] = SearchInput.Observe().Text });
                Goto(Phase.SearchWait);
                return;
            }

            case Phase.SearchWait:
            {
                var p = MarketBoardUi.GetSearchProgress(resolvedItemId);
                if (p.ExactItemPresent && p.ExactRowRendered && !p.Working)
                {
                    ThinkThen(HumanTiming.ReadResultsAndPickItem(), Phase.OpenListings, "read-results");
                    return;
                }
                var obs = SearchInput.Observe();
                if (Elapsed > TimeSpan.FromSeconds(3) && !obs.Working && obs.AgentItems == 0)
                {
                    if (retypeCount >= 3)
                    {
                        StopItem(StopReason.SearchFailed, $"Search never ran (box shows \"{obs.Text}\").");
                        return;
                    }
                    retypeCount++;
                    // alternate: re-press Enter, then re-type
                    if (retypeCount % 2 == 1)
                        SearchInput.Submit();
                    else
                        Goto(Phase.PrepSearch);
                    phaseEnteredUtc = DateTime.UtcNow;
                }
                return;
            }

            case Phase.OpenListings:
            {
                var p = MarketBoardUi.GetSearchProgress(resolvedItemId);
                if (p.ExactItemPresent && p.ExactRowRendered)
                {
                    ThinkThen(HumanTiming.ReachForRow(), Phase.ClickResultRow, "reach-for-result");
                    return;
                }
                if (Elapsed > SearchTimeout)
                    StopItem(StopReason.SearchFailed, "Could not open the item's listings.");
                return;
            }

            case Phase.ClickResultRow:
            {
                var p = MarketBoardUi.GetSearchProgress(resolvedItemId);
                if (p.ExactIndex >= 0 && MarketBoardUi.OpenListingsForResultRow(p.ExactIndex))
                {
                    listingCountSeen = -1;
                    listingCountChangedUtc = DateTime.UtcNow;
                    Goto(Phase.ListingsWait);
                    return;
                }
                if (Elapsed > TimeSpan.FromSeconds(5))
                    Goto(Phase.OpenListings); // re-acquire and retry
                return;
            }

            case Phase.ListingsWait:
            {
                var s = MarketBoardUi.GetListingsState();
                if (s.Available && s.ItemId == resolvedItemId)
                {
                    if (s.ListingCount != listingCountSeen)
                    {
                        listingCountSeen = s.ListingCount;
                        listingCountChangedUtc = DateTime.UtcNow;
                    }
                    var stable = DateTime.UtcNow - listingCountChangedUtc >= ListingsStable;
                    if (s.ListingCount > 0 && (stable || Elapsed > TimeSpan.FromSeconds(5)))
                    {
                        Goto(Phase.Read);
                        return;
                    }
                    if (s.ListingCount == 0 && stable && Elapsed > TimeSpan.FromSeconds(3))
                    {
                        RecordNextLowest();
                        StopItem(StopReason.NoListings, "No listings for this item.");
                        return;
                    }
                }
                if (Elapsed > ListingsTimeout)
                    StopItem(StopReason.SearchFailed, "Listings never loaded.");
                return;
            }

            case Phase.Read:
            {
                var raw = ListingReader.ReadRaw(resolvedItemId);
                var ranked = ListingReader.RankByPrice(raw, item!.Quality);
                acceptable = ranked.Where(l => l.UnitPrice <= item.MaxUnitPrice).ToList();
                rejected = ranked.Where(l => l.UnitPrice > item.MaxUnitPrice).ToList();

                if (item.Quantity <= 0)
                {
                    result!.AvailableListings = ranked.Select(l => new PurchaseRecord
                    {
                        UnitPrice = l.UnitPrice, Quantity = l.Quantity, Hq = l.Hq, TotalGil = l.StackCost,
                    }).ToList();
                    RecordNextLowest();
                    StopItem(ranked.Count == 0 ? StopReason.NoListings : StopReason.QuantityMet, "Discovery only.");
                    return;
                }

                ThinkThen(HumanTiming.ScanListings(ranked.Count), Phase.ChooseAndStage, "scan-listings");
                return;
            }

            case Phase.ChooseAndStage:
            {
                if (CancelledNow()) { RecordNextLowest(); StopItem(StopReason.Cancelled, "Cancelled."); return; }
                if (boughtQty >= item!.Quantity) { RecordNextLowest(); StopItem(StopReason.QuantityMet, "Quantity met."); return; }

                var pick = PickListing();
                if (pick is null)
                {
                    RecordNextLowest();
                    var reason = acceptable.Count(l => !boughtListingIds.Contains(l.ListingId)) == 0
                        ? (rejected.Count > 0 ? StopReason.PriceExceeded : StopReason.NoListings)
                        : StopReason.Overshoot;
                    StopItem(reason, "No acceptable listing left to buy.");
                    return;
                }

                var chosen = pick.Value;
                var gil = GameState.GetGil();
                if (gil < chosen.StackCost)
                {
                    RecordNextLowest();
                    StopItem(StopReason.BudgetExceeded, $"Not enough gil ({gil:N0} on hand, need {chosen.StackCost:N0}).");
                    return;
                }
                if (order!.TotalGilBudget > 0 && order.TotalGilSpent + chosen.StackCost > order.TotalGilBudget)
                {
                    RecordNextLowest();
                    StopItem(StopReason.BudgetExceeded, "Order gil budget would be exceeded.");
                    return;
                }

                staged = chosen;
                stagedGil = gil;
                ThinkThen(HumanTiming.ReachForRow(), Phase.StageListing, "reach-for-listing");
                return;
            }

            case Phase.StageListing:
            {
                var diag = MarketBoardUi.BeginPurchase(staged);
                if (diag is not null)
                {
                    Log?.Invoke($"Stage failed: {diag}");
                    acceptable.Remove(staged);
                    Goto(Phase.ChooseAndStage);
                    return;
                }

                watcher.Arm(staged, stagedGil);
                Goto(Phase.ConfirmWait);
                return;
            }

            case Phase.ConfirmWait:
            {
                var yn = MarketBoardUi.GetYesNoState();
                if (yn.Visible)
                {
                    if (LooksLikePurchasePrompt(yn.PromptText))
                    {
                        ThinkThen(HumanTiming.ReadConfirmPrompt() + HumanTiming.ReachForButton(), Phase.ConfirmYes, "read-prompt");
                    }
                    else
                    {
                        MarketBoardUi.AnswerYesNo(false);
                        watcher.Disarm();
                        RecordNextLowest();
                        StopItem(StopReason.PromptMismatch, $"Unexpected confirm prompt: {yn.PromptText}");
                    }
                    return;
                }

                // confirmation disabled in the client -> the event resolves it
                if (watcher.Outcome != PurchaseOutcome.Pending || watcher.RequestObserved)
                {
                    Goto(Phase.ResultWait);
                    return;
                }
                if (Elapsed > ConfirmTimeout)
                {
                    watcher.Disarm();
                    RecordNextLowest();
                    StopItem(StopReason.Indeterminate, "Purchase confirm dialog never appeared.");
                }
                return;
            }

            case Phase.ConfirmYes:
            {
                if (MarketBoardUi.GetYesNoState().Visible)
                    MarketBoardUi.AnswerYesNo(true);
                Goto(Phase.ResultWait);
                return;
            }

            case Phase.ResultWait:
            {
                watcher.Tick();
                switch (watcher.Outcome)
                {
                    case PurchaseOutcome.Pending:
                        return;

                    case PurchaseOutcome.Verified:
                    {
                        var qty = watcher.QuantityConfirmed > 0 ? watcher.QuantityConfirmed : staged.Quantity;
                        boughtQty += qty;
                        boughtListingIds.Add(staged.ListingId);
                        acceptable.Remove(staged);
                        result!.PurchasedQuantity = boughtQty;
                        result.TotalGilSpent += watcher.GilSpent;
                        result.Purchases.Add(new PurchaseRecord
                        {
                            UnitPrice = staged.UnitPrice, Quantity = qty, Hq = staged.Hq,
                            TotalGil = watcher.GilSpent, RetainerId = staged.RetainerId.ToString(),
                        });
                        order!.TotalGilSpent += watcher.GilSpent;
                        Log?.Invoke($"Bought {qty}x {result.ItemName} @ {staged.UnitPrice:N0} ({watcher.GilSpent:N0} gil).");
                        watcher.Disarm();
                        MarketBoardUi.DismissDialogs();
                        MarketBoardUi.ClearStagedPurchase();

                        // Only take the long "between purchases" pause if there is
                        // actually another listing to buy — otherwise finish promptly.
                        var moreToBuy = boughtQty < item!.Quantity && PickListing() is not null;
                        ThinkThen(
                            moreToBuy
                                ? HumanTiming.AfterPurchaseSettle() + HumanTiming.BetweenPurchases()
                                : HumanTiming.AfterPurchaseSettle(),
                            Phase.ChooseAndStage,
                            moreToBuy ? "between-purchases" : "after-last-purchase");
                        return;
                    }

                    case PurchaseOutcome.Rejected:
                        watcher.Disarm(); MarketBoardUi.ClearStagedPurchase(); RecordNextLowest();
                        StopItem(StopReason.PriceExceeded, "Server rejected the purchase. " + watcher.Message);
                        return;

                    case PurchaseOutcome.Conflicted:
                        watcher.Disarm(); MarketBoardUi.ClearStagedPurchase(); RecordNextLowest();
                        StopItem(StopReason.PromptMismatch, watcher.Message);
                        return;

                    default:
                        watcher.Disarm(); MarketBoardUi.ClearStagedPurchase(); RecordNextLowest();
                        StopItem(StopReason.Indeterminate, watcher.Message);
                        return;
                }
            }
        }
    }

    // ---- helpers -------------------------------------------------------

    private TimeSpan Elapsed => DateTime.UtcNow - phaseEnteredUtc;

    private void Goto(Phase next)
    {
        if (recorder.IsRecording && next != phase)
            recorder.RunnerEvent("phase", new() { ["from"] = phase.ToString(), ["to"] = next.ToString() });
        phase = next;
        phaseEnteredUtc = DateTime.UtcNow;
    }

    private void ThinkThen(TimeSpan delay, Phase next, string reason)
    {
        thinkStartUtc = DateTime.UtcNow;
        thinkUntil = thinkStartUtc + delay;
        thinkTotalMs = delay.TotalMilliseconds;
        thinkReason = reason;
        thinkNext = next;
        if (recorder.IsRecording)
            recorder.RunnerEvent("think", new() { ["reason"] = reason, ["ms"] = (long)delay.TotalMilliseconds, ["then"] = next.ToString() });
        Goto(Phase.Think);
    }

    private CandidateListing? PickListing()
    {
        var remaining = item!.Quantity - boughtQty;
        foreach (var l in acceptable.OrderBy(x => x.UnitPrice).ThenBy(x => x.Quantity))
        {
            if (boughtListingIds.Contains(l.ListingId))
                continue;
            switch (item.Overshoot)
            {
                case OvershootPolicy.Allow:
                    return l;
                case OvershootPolicy.Skip:
                    if (l.Quantity <= remaining) return l;
                    continue;
                case OvershootPolicy.Limit:
                    var cap = remaining + (int)Math.Ceiling(item.Quantity * (item.OvershootLimitPercent / 100.0));
                    if (boughtQty + l.Quantity <= cap || l.Quantity <= remaining) return l;
                    continue;
            }
        }
        return null;
    }

    private void RecordNextLowest()
    {
        var pool = acceptable.Where(l => !boughtListingIds.Contains(l.ListingId)).ToList();
        CandidateListing? next = pool.Count > 0
            ? pool.OrderBy(l => l.UnitPrice).First()
            : (rejected.Count > 0 ? rejected.OrderBy(l => l.UnitPrice).First() : (CandidateListing?)null);
        if (next is { } n)
        {
            result!.NextLowestUnitPrice = n.UnitPrice;
            result.NextLowestQuantity = n.Quantity;
            result.NextLowestHq = n.Hq;
            result.ListingsExhausted = false;
        }
        else
        {
            result!.ListingsExhausted = true;
        }
    }

    private void StopItem(StopReason reason, string message)
    {
        result!.StoppedReason = reason;
        Log?.Invoke($"{result.ItemName}: {reason} — {message}");
        var more = itemIndex + 1 < order!.Request.Items.Count && !CancelledNow();
        if (more)
            ThinkThen(HumanTiming.BetweenItems(), Phase.ItemNext, "between-items");
        else
            Goto(Phase.ItemNext);
    }

    private void FinishOrder(OrderState state, string message)
    {
        order!.State = state;
        order.Message = message;
        order.FinishedUtc = DateTime.UtcNow;
        Cleanup();
        var done = order;
        order = null;
        phase = Phase.Idle;

        if (recorder.IsRecording)
            recorder.RunnerEvent("order-finish", new() { ["orderId"] = done.OrderId, ["state"] = state.ToString(), ["gilSpent"] = done.TotalGilSpent });
        if (startedCaptureForThisOrder && recorder.IsRecording)
            Log?.Invoke(recorder.Stop());
        startedCaptureForThisOrder = false;

        Log?.Invoke($"Order {done.OrderId}: {state} — {message}");
        OrderFinished?.Invoke(done);
    }

    private void FailOrder(string message) => FinishOrder(OrderState.Failed, message);

    private void Cleanup()
    {
        watcher.Disarm();
        Navigation.Stop();
        if (lifestreamTravelActive && Lifestream.IsBusy())
            Lifestream.Abort();
        lifestreamTravelActive = false;
        MarketBoardUi.DismissDialogs();
        MarketBoardUi.ClearStagedPurchase();
        if (Plugin.Instance.Configuration.HideBoardWhenFinished)
            MarketBoardUi.HideBoard();
    }

    private static bool LooksLikePurchasePrompt(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return text.Contains("purchase", StringComparison.OrdinalIgnoreCase)
               || (text.Contains("buy", StringComparison.OrdinalIgnoreCase) && text.Contains("gil", StringComparison.OrdinalIgnoreCase));
    }
}
