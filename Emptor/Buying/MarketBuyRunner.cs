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
        WorldTravel, WorldTravelWait, ReturnHome, ReturnHomeWait,
        Dismount, LocateBoard, TravelToBoard, TravelWait, ApproachAnchor, NavigateBoard, InteractBoard, BoardOpenWait,
        Think,
        PrepSearch, TypeSearch, SubmitSearch, SearchWait,
        OpenListings, ClickResultRow, ListingsWait, Read,
        ChooseAndStage, StageListing, ConfirmWait, ConfirmYes, ResultWait,
    }

    private static readonly TimeSpan BoardOpenTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan NavigateTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DismountTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(14);
    private static readonly TimeSpan ListingsTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ListingsStable = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan BlockedTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LifestreamTravelTimeout = TimeSpan.FromSeconds(150);
    // After we've clearly arrived (zone changed, settled), how long to let the
    // board object stream in before giving up on it.
    private static readonly TimeSpan TravelArriveSettle = TimeSpan.FromSeconds(6);
    // World / data-centre travel: the queue at the far end can be minutes.
    private static readonly TimeSpan WorldTravelTimeout = TimeSpan.FromMinutes(6);
    private static readonly TimeSpan WorldTravelQuiet = TimeSpan.FromSeconds(35);
    // How long a single teleport attempt can show no progress before it counts
    // as failed (a real teleport cast is ~5 s of silence for "/li tp").
    private static readonly TimeSpan TravelAttemptQuiet = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan TravelRetryDelay = TimeSpan.FromSeconds(5);

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
    private string? travelDestLabel;
    private uint travelStartTerritory;
    private Vector3 travelLastPos;
    private DateTime travelLastSignalUtc;
    private DateTime travelArrivedUtc;
    private Vector3 anchorTarget;

    // world / DC travel (order-scoped)
    private bool worldTravelDone;
    private bool worldHopped;
    private uint homeWorldId;
    private uint targetWorldId;
    private uint worldTravelStartWorld;
    private DateTime worldTravelLastSignalUtc;

    // teleport retry (per travel leg)
    private int travelAttempt;
    private DateTime travelDeadlineUtc;
    private DateTime travelRetryAtUtc;
    private bool travelBlockNoticeShown;

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
        Phase.WorldTravel or Phase.WorldTravelWait =>
            $"Travelling to {Worlds.ById(targetWorldId)?.Name ?? "another world"}…",
        Phase.ReturnHome or Phase.ReturnHomeWait =>
            $"Returning to {Worlds.ById(homeWorldId)?.Name ?? "the home world"}…",
        Phase.Dismount => "Dismounting…",
        Phase.LocateBoard => "Looking for a Market Board…",
        Phase.TravelToBoard => travelDestLabel is null ? "Heading to a Market Board…" : $"Heading to {travelDestLabel}…",
        Phase.TravelWait => travelDestLabel is null ? "Travelling to a Market Board…" : $"Travelling to {travelDestLabel}…",
        Phase.ApproachAnchor => "Walking to the Market Board area…",
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
        worldTravelDone = false;
        worldHopped = false;
        homeWorldId = Worlds.HomeWorld()?.Id ?? 0;
        travelAttempt = 0;
        travelDeadlineUtc = default;
        travelRetryAtUtc = default;
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
                if (itemIndex >= order!.Request.Items.Count)
                {
                    if (order.Request.ReturnToHomeWorld && worldHopped && homeWorldId != 0 && Lifestream.Available
                        && (Worlds.CurrentWorld()?.Id ?? 0) != homeWorldId)
                    {
                        BeginTravelRetries();
                        Goto(Phase.ReturnHome);
                        return;
                    }
                    FinishOrder(OrderState.Completed, "Completed.");
                    return;
                }
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

                // World / data-centre travel happens once, before anything
                // board-related. skipTravel means "use the board I'm at" — no hop.
                if (!worldTravelDone && !order!.Request.SkipTravel
                    && !string.IsNullOrWhiteSpace(order.Request.World))
                {
                    BeginTravelRetries();
                    Goto(Phase.WorldTravel);
                    return;
                }
                worldTravelDone = true;

                // Board still open at a real board (a multi-item order carrying
                // on): the "occupied in event" flags are just the open board, not
                // a real block — go straight to the next search. This check must
                // come BEFORE GetBlockReason(), which counts an open board as a
                // block and would otherwise strand a multi-item order.
                if (BoardReadyForNextSearch())
                {
                    ThinkThen(HumanTiming.OrientAfterOpen(), Phase.PrepSearch, "orient");
                    return;
                }

                if (GameState.GetBlockReason() is { } block && !PlayerActions.IsMounted)
                {
                    Log?.Invoke($"Blocked: {block}");
                    Goto(Phase.BlockedWait);
                    return;
                }

                Goto(PlayerActions.IsMounted ? Phase.Dismount : Phase.LocateBoard);
                return;
            }

            case Phase.BlockedWait:
            {
                if (CancelledNow()) { StopItem(StopReason.Cancelled, "Cancelled while blocked."); return; }
                // If the board opened / is still open at a real board and we're
                // not in combat, the block is just the board — carry on.
                if (BoardReadyForNextSearch())
                {
                    ThinkThen(HumanTiming.OrientAfterOpen(), Phase.PrepSearch, "orient");
                    return;
                }
                if (GameState.GetBlockReason() is null) { Goto(Phase.LocateBoard); return; }
                if (Elapsed > BlockedTimeout) StopItem(StopReason.Blocked, "Still blocked after waiting.");
                return;
            }

            case Phase.WorldTravel:
            {
                if (CancelledNow()) { FailOrder("Cancelled before world travel."); return; }

                if (!Lifestream.Available)
                {
                    FailOrder("This order pins a world, but Lifestream is not installed.");
                    return;
                }

                var target = Worlds.Resolve(order!.Request.World);
                if (target is null)
                {
                    FailOrder($"Unknown world \"{order.Request.World}\".");
                    return;
                }

                var cur = Worlds.CurrentWorld();
                if (cur is not null && cur.Id == target.Id)
                {
                    worldTravelDone = true;
                    Goto(Phase.ItemBegin);
                    return;
                }

                if (Lifestream.IsBusy())
                {
                    if (Elapsed > WorldTravelTimeout) FailOrder("Lifestream stayed busy — could not start world travel.");
                    return;
                }

                if (!Worlds.IsReachable(target))
                {
                    FailOrder($"{target.Name} ({target.DcName}) isn't reachable — data-centre travel only covers your home region's data centres and Materia.");
                    return;
                }
                if (!Lifestream.CanVisitSameDc(target.Name) && !Lifestream.CanVisitCrossDc(target.Name))
                {
                    FailOrder($"Lifestream can't currently travel to {target.Name} (world full, or a new-character restriction).");
                    return;
                }

                if (!TravelCanFire(out var whHard, out var whBlock))
                {
                    if (whHard)
                        FailOrder($"Still {whBlock} after retrying world travel for {Plugin.Instance.Configuration.TravelRetrySeconds}s.");
                    return;
                }

                if (!Lifestream.ChangeWorld(target.Name))
                {
                    FailOrder($"Lifestream refused travel to {target.Name}.");
                    return;
                }

                travelAttempt++;
                targetWorldId = target.Id;
                worldTravelStartWorld = cur?.Id ?? 0;
                worldTravelLastSignalUtc = DateTime.UtcNow;
                travelArrivedUtc = default;
                travelRetryAtUtc = default;
                Log?.Invoke($"{(travelAttempt > 1 ? $"Retry {travelAttempt - 1}: t" : "T")}ravelling to world {target.Name}…");
                Goto(Phase.WorldTravelWait);
                return;
            }

            case Phase.WorldTravelWait:
            {
                if (CancelledNow()) { Lifestream.Abort(); FailOrder("Cancelled during world travel."); return; }

                var nowWorld = Worlds.CurrentWorld()?.Id ?? 0;
                if (nowWorld == targetWorldId && !GameState.IsBetweenAreas && !Lifestream.IsBusy())
                {
                    if (travelArrivedUtc == default) travelArrivedUtc = DateTime.UtcNow;
                    if (DateTime.UtcNow - travelArrivedUtc > TravelArriveSettle)
                    {
                        worldTravelDone = true;
                        worldHopped = true;
                        travelArrivedUtc = default;
                        Log?.Invoke($"Arrived on {Worlds.ById(targetWorldId)?.Name ?? "the target world"}.");
                        Goto(Phase.ItemBegin);
                    }
                    return;
                }
                travelArrivedUtc = default;

                var inTransit = nowWorld != worldTravelStartWorld || GameState.IsBetweenAreas;
                if (Lifestream.IsBusy() || inTransit)
                    worldTravelLastSignalUtc = DateTime.UtcNow;

                if (Elapsed > WorldTravelTimeout)
                {
                    Lifestream.Abort();
                    FailOrder("Timed out travelling to the target world.");
                    return;
                }

                // Once the world hop has actually begun, be patient (the DC queue
                // is slow). Before that, a short silence means the teleport was
                // cancelled — retry it.
                var quiet = inTransit ? WorldTravelQuiet : TravelAttemptQuiet;
                if (DateTime.UtcNow - worldTravelLastSignalUtc > quiet)
                {
                    Lifestream.Abort();
                    var why = GameState.GetTeleportBlock() ?? "the teleport didn't start";
                    if (!inTransit && TravelRetryOrGiveUp(why))
                    {
                        Goto(Phase.WorldTravel);
                        return;
                    }
                    FailOrder($"World travel failed ({why}).");
                    return;
                }
                return;
            }

            case Phase.ReturnHome:
            {
                if (CancelledNow()) { FinishOrder(OrderState.Completed, "Completed (return-home cancelled)."); return; }
                if (Lifestream.IsBusy())
                {
                    if (Elapsed > WorldTravelTimeout)
                        FinishOrder(OrderState.Completed, "Completed (Lifestream busy — did not return home).");
                    return;
                }

                if (!TravelCanFire(out var rhHard, out _))
                {
                    if (rhHard)
                        FinishOrder(OrderState.Completed, "Completed (could not return home — kept getting blocked).");
                    return;
                }

                var home = Worlds.ById(homeWorldId);
                if (home is null || !Lifestream.ChangeWorld(home.Name))
                {
                    FinishOrder(OrderState.Completed, "Completed (could not start the return-home trip).");
                    return;
                }

                travelAttempt++;
                targetWorldId = homeWorldId;
                worldTravelStartWorld = Worlds.CurrentWorld()?.Id ?? 0;
                worldTravelLastSignalUtc = DateTime.UtcNow;
                travelRetryAtUtc = default;
                Log?.Invoke($"Returning to home world {home.Name}…");
                Goto(Phase.ReturnHomeWait);
                return;
            }

            case Phase.ReturnHomeWait:
            {
                var nowWorld = Worlds.CurrentWorld()?.Id ?? 0;
                if (nowWorld == homeWorldId && !GameState.IsBetweenAreas && !Lifestream.IsBusy())
                {
                    FinishOrder(OrderState.Completed, "Completed.");
                    return;
                }

                var rhTransit = nowWorld != worldTravelStartWorld || GameState.IsBetweenAreas;
                if (Lifestream.IsBusy() || rhTransit)
                    worldTravelLastSignalUtc = DateTime.UtcNow;

                if (Elapsed > WorldTravelTimeout)
                {
                    Lifestream.Abort();
                    FinishOrder(OrderState.Completed, "Completed (the return-home trip did not finish).");
                    return;
                }
                if (DateTime.UtcNow - worldTravelLastSignalUtc > (rhTransit ? WorldTravelQuiet : TravelAttemptQuiet))
                {
                    Lifestream.Abort();
                    if (!rhTransit && TravelRetryOrGiveUp(GameState.GetTeleportBlock() ?? "the teleport didn't start"))
                    {
                        Goto(Phase.ReturnHome);
                        return;
                    }
                    FinishOrder(OrderState.Completed, "Completed (the return-home trip did not finish).");
                    return;
                }
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
                        BeginTravelRetries();
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

                // Wait out the retry backoff and any teleport-blocking condition.
                if (!TravelCanFire(out var hardBlock, out var blockReason))
                {
                    if (hardBlock)
                        StopItem(StopReason.TravelFailed,
                            $"Still {blockReason} after retrying for {Plugin.Instance.Configuration.TravelRetrySeconds}s.");
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

                travelAttempt++;
                triedLifestreamThisItem = true;
                lifestreamTravelActive = true;
                travelStartTerritory = GameState.TerritoryId;
                travelLastPos = GameState.PlayerPosition();
                travelLastSignalUtc = DateTime.UtcNow;
                travelArrivedUtc = default;
                travelRetryAtUtc = default;
                Log?.Invoke($"{(travelAttempt > 1 ? $"Retry {travelAttempt - 1}: " : "No board nearby — ")}travelling to {dest}.");
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

                // Success: a board is in reach — hand back to LocateBoard, which
                // interacts or does a short vnav hop.
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

                // "Travel is happening" — key off anything observable, not just
                // Lifestream.IsBusy (a plain "/li tp" teleport never sets it).
                var here = GameState.PlayerPosition();
                var moving = Vector3.Distance(here, travelLastPos) > 0.3f;
                travelLastPos = here;
                var zoned = GameState.TerritoryId != travelStartTerritory;
                if (Lifestream.IsBusy() || GameState.IsBetweenAreas || zoned || moving)
                    travelLastSignalUtc = DateTime.UtcNow;

                // Clearly arrived: zone changed, not zoning, Lifestream idle.
                // Give the board object a few seconds to stream in.
                if (zoned && !GameState.IsBetweenAreas && !Lifestream.IsBusy())
                {
                    if (travelArrivedUtc == default)
                        travelArrivedUtc = DateTime.UtcNow;
                    if (DateTime.UtcNow - travelArrivedUtc <= TravelArriveSettle)
                        return;

                    lifestreamTravelActive = false;

                    // Still nothing loaded — if we have a known board spot for this
                    // city and can walk, head there and let it stream in.
                    var city = MarketCities.Resolve(order!.Request.City);
                    if (city?.Anchor is { } anchor
                        && GameState.TerritoryId == city.AnchorTerritory
                        && Plugin.Instance.Configuration.UseNavigation
                        && Navigation.Available && Navigation.IsReady())
                    {
                        anchorTarget = anchor;
                        Navigation.MoveCloseTo(anchor, 6f);
                        Log?.Invoke($"No board object at the landing spot — walking to the known {city.Display} board area.");
                        Goto(Phase.ApproachAnchor);
                        return;
                    }

                    StopItem(StopReason.TravelFailed,
                        $"Reached {travelDestLabel ?? "the destination"} but no Market Board is in reach — "
                        + "the landing spot is too far from the board (needs a board anchor), or vnavmesh isn't ready.");
                    return;
                }
                travelArrivedUtc = default;

                // This attempt shows no sign of travel — treat it as failed and
                // retry (combat / movement can cancel the teleport cast).
                if (DateTime.UtcNow - travelLastSignalUtc > TravelAttemptQuiet)
                {
                    if (lifestreamTravelActive) Lifestream.Abort();
                    lifestreamTravelActive = false;
                    var why = GameState.GetTeleportBlock() ?? "the teleport didn't start";
                    if (TravelRetryOrGiveUp(why))
                    {
                        Goto(Phase.TravelToBoard);
                        return;
                    }
                    StopItem(StopReason.TravelFailed, $"Travel to a Market Board kept failing ({why}).");
                    return;
                }

                return;
            }

            case Phase.ApproachAnchor:
            {
                if (CancelledNow())
                {
                    Navigation.Stop();
                    StopItem(StopReason.Cancelled, "Cancelled while walking to the board.");
                    return;
                }

                // Board streamed in while walking — hand to LocateBoard.
                if (MarketBoardLocator.FindNearest(MarketBoardLocator.NavigateSearchRadius) is not null)
                {
                    Navigation.Stop();
                    Goto(Phase.LocateBoard);
                    return;
                }

                var d = Vector3.Distance(GameState.PlayerPosition(), anchorTarget);
                if (d <= 8f || (!Navigation.IsRunning() && Elapsed > TimeSpan.FromSeconds(4)))
                {
                    Navigation.Stop();
                    StopItem(StopReason.TravelFailed,
                        $"Walked to the {travelDestLabel ?? "city"} Market Board spot but no board object appeared — the anchor may be wrong.");
                    return;
                }
                if (Elapsed > NavigateTimeout)
                {
                    Navigation.Stop();
                    StopItem(StopReason.TravelFailed, "Timed out walking to the Market Board.");
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

    /// <summary>
    /// The Market Board window is open, a real board object is right next to us,
    /// and we're not in combat — so the runner can go straight to a search
    /// without re-interacting. An open board sets Occupied* condition flags that
    /// <see cref="GameState.GetBlockReason"/> would otherwise treat as a block.
    /// </summary>
    private static bool BoardReadyForNextSearch()
        => !GameState.InCombat
           && MarketBoardUi.IsBoardOpen()
           && MarketBoardLocator.FindNearest(MarketBoardLocator.InteractDistance + 1.5f) is not null;

    private void Goto(Phase next)
    {
        if (recorder.IsRecording && next != phase)
            recorder.RunnerEvent("phase", new() { ["from"] = phase.ToString(), ["to"] = next.ToString() });
        phase = next;
        phaseEnteredUtc = DateTime.UtcNow;
    }

    // ---- teleport retry -----------------------------------------------

    /// <summary>Start a fresh retry budget for one travel leg (city, world, or return-home).</summary>
    private void BeginTravelRetries()
    {
        travelAttempt = 0;
        travelRetryAtUtc = default;
        travelBlockNoticeShown = false;
        travelDeadlineUtc = DateTime.UtcNow
            + TimeSpan.FromSeconds(Math.Max(0, Plugin.Instance.Configuration.TravelRetrySeconds));
    }

    /// <summary>
    /// In a "fire the travel command" phase: returns true when the caller may
    /// dispatch now. Returns false while backing off from a retry or waiting out
    /// a teleport-blocking condition (combat, occupied, …). Sets
    /// <paramref name="hardBlock"/> when the budget is spent and the caller
    /// should fail.
    /// </summary>
    private bool TravelCanFire(out bool hardBlock, out string blockReason)
    {
        hardBlock = false;
        blockReason = string.Empty;

        if (travelRetryAtUtc != default && DateTime.UtcNow < travelRetryAtUtc)
            return false;

        var block = GameState.GetTeleportBlock();
        if (block is null)
        {
            travelBlockNoticeShown = false;
            return true;
        }

        blockReason = block;
        if (travelAttempt > 0 && DateTime.UtcNow >= travelDeadlineUtc)
        {
            hardBlock = true;
            return false;
        }

        if (!travelBlockNoticeShown)
        {
            travelBlockNoticeShown = true;
            Plugin.ChatGui.Print($"[Emptor] Can't teleport while {block} — waiting…");
            Log?.Invoke($"Travel held: {block}.");
        }
        return false;
    }

    /// <summary>
    /// In a travel "wait" phase, once an attempt is judged to have failed:
    /// announces + schedules a 5s retry and returns true, or returns false when
    /// the retry budget is spent (caller fails terminally).
    /// </summary>
    private bool TravelRetryOrGiveUp(string reason)
    {
        if (DateTime.UtcNow >= travelDeadlineUtc)
            return false;

        var secs = (int)TravelRetryDelay.TotalSeconds;
        Plugin.ChatGui.Print($"[Emptor] Teleport failed ({reason}) — retrying in {secs}s…");
        Log?.Invoke($"Travel attempt {travelAttempt} failed ({reason}); retrying in {secs}s.");
        travelRetryAtUtc = DateTime.UtcNow + TravelRetryDelay;
        return true;
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
        {
            // Close the board so the next item starts from a clean state — an
            // open board keeps the character "occupied in event", which the
            // block check would otherwise read as stuck.
            MarketBoardUi.DismissDialogs();
            MarketBoardUi.HideBoard();
            ThinkThen(HumanTiming.BetweenItems(), Phase.ItemNext, "between-items");
        }
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
