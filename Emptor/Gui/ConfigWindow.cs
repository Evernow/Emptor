using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Emptor.Buying;
using Emptor.GameData;
using Emptor.Pricing;

namespace Emptor.Gui;

public sealed class ConfigWindow : Window, IDisposable
{
    private static readonly string[] QualityLabels = { "Either", "NQ only", "HQ only" };
    private static readonly string[] OvershootLabels = { "Buy anyway", "Skip big stacks", "Limit %" };

    private readonly Plugin plugin;
    private readonly List<string> log = new();
    private const int MaxLog = 200;

    private string nameFilter = string.Empty;

    // Prices tab state
    private static readonly string[] ScopeLabels = { "World", "Data centre", "Region", "Reachable (region + Materia)" };
    private string priceItemInput = string.Empty;
    private int priceScopeIdx = 1;
    private string priceTarget = string.Empty;
    private PriceRequest? priceActive;
    private string priceMsg = string.Empty;

    public ConfigWindow(Plugin plugin) : base("Emptor##EmptorMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 380),
            MaximumSize = new Vector2(4000, 3000),
        };
    }

    private Configuration Config => plugin.Configuration;

    public void AppendLog(string line)
    {
        log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        if (log.Count > MaxLog)
            log.RemoveRange(0, log.Count - MaxLog);
    }

    public void Dispose() { }

    public override void Draw()
    {
        var busy = plugin.Orders.IsBusy;

        if (ImGui.BeginTabBar("##emptorTabs"))
        {
            if (ImGui.BeginTabItem("Buy"))
            {
                DrawShoppingList(busy);
                ImGui.Separator();
                DrawControls(busy);
                ImGui.Separator();
                DrawCapture();
                ImGui.Separator();
                DrawProgress();
                ImGui.Separator();
                DrawLog();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Prices"))
            {
                DrawPrices();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    private void DrawShoppingList(bool busy)
    {
        ImGui.TextUnformatted("Shopping list");
        if (busy)
            ImGui.BeginDisabled();

        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg
            | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;

        if (ImGui.BeginTable("##emptorList", 8, flags, new Vector2(0, 200)))
        {
            ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 28);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("Id", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn("Max unit", ImGuiTableColumnFlags.WidthFixed, 110);
            ImGui.TableSetupColumn("Quality", ImGuiTableColumnFlags.WidthFixed, 90);
            ImGui.TableSetupColumn("Overshoot", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 24);
            ImGui.TableHeadersRow();

            ShoppingListRow? toRemove = null;
            for (var i = 0; i < Config.ShoppingList.Count; i++)
            {
                var row = Config.ShoppingList[i];
                ImGui.TableNextRow();
                ImGui.PushID(i);

                ImGui.TableNextColumn();
                var enabled = row.Enabled;
                if (ImGui.Checkbox("##on", ref enabled)) { row.Enabled = enabled; Config.Save(); }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                var name = row.ItemName;
                if (ImGui.InputText("##name", ref name, 128))
                {
                    row.ItemName = name;
                    row.ItemId = ItemResolver.ResolveExact(name);
                    Config.Save();
                }
                if (ImGui.IsItemDeactivatedAfterEdit() && row.ItemId == 0 && !string.IsNullOrWhiteSpace(name))
                {
                    var hits = ItemResolver.Search(name, 1);
                    if (hits.Count == 1) { row.ItemName = hits[0].Name; row.ItemId = hits[0].ItemId; Config.Save(); }
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.ItemId == 0 ? "—" : row.ItemId.ToString());

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                var qty = row.Quantity;
                if (ImGui.InputInt("##qty", ref qty, 0, 0)) { row.Quantity = Math.Max(0, qty); Config.Save(); }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                var price = (int)Math.Clamp(row.MaxUnitPrice, 0, int.MaxValue);
                if (ImGui.InputInt("##price", ref price, 0, 0)) { row.MaxUnitPrice = Math.Max(0, price); Config.Save(); }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                var q = (int)row.Quality;
                if (Combo("##quality", QualityLabels, ref q)) { row.Quality = (QualityFilter)q; Config.Save(); }

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                var o = (int)row.Overshoot;
                if (Combo("##overshoot", OvershootLabels, ref o)) { row.Overshoot = (OvershootPolicy)o; Config.Save(); }
                if (row.Overshoot == OvershootPolicy.Limit)
                {
                    ImGui.SetNextItemWidth(-1);
                    var pct = row.OvershootLimitPercent;
                    if (ImGui.InputInt("##pct", ref pct, 0, 0)) { row.OvershootLimitPercent = Math.Clamp(pct, 0, 1000); Config.Save(); }
                }

                ImGui.TableNextColumn();
                if (ImGui.Button("x")) toRemove = row;

                ImGui.PopID();
            }

            ImGui.EndTable();

            if (toRemove is not null) { Config.ShoppingList.Remove(toRemove); Config.Save(); }
        }

        if (ImGui.Button("Add row"))
        {
            Config.ShoppingList.Add(new ShoppingListRow());
            Config.Save();
        }

        if (busy)
            ImGui.EndDisabled();
    }

    private void DrawControls(bool busy)
    {
        var canStart = !busy && Config.ShoppingList.Any(r => r.Enabled && r.Quantity > 0 && r.ItemId != 0);
        if (!canStart) ImGui.BeginDisabled();
        if (ImGui.Button("Start"))
        {
            var request = new BuyRequest
            {
                ClientRequestId = "ui",
                TotalGilBudget = Config.TotalGilBudget,
                City = string.IsNullOrWhiteSpace(Config.PreferredCity) ? null : Config.PreferredCity,
                World = string.IsNullOrWhiteSpace(Config.PreferredWorld) ? null : Config.PreferredWorld,
                Items = Config.ShoppingList
                    .Where(r => r.Enabled && r.Quantity > 0 && r.ItemId != 0)
                    .Select(r => r.ToRequestItem())
                    .ToList(),
            };
            plugin.Orders.Submit(request, fromUi: true);
        }
        if (!canStart) ImGui.EndDisabled();

        ImGui.SameLine();
        if (!busy) ImGui.BeginDisabled();
        if (ImGui.Button("Stop"))
            plugin.Runner.RequestCancel();
        if (!busy) ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        var budget = (int)Math.Clamp(Config.TotalGilBudget, 0, int.MaxValue);
        if (ImGui.InputInt("Gil budget (0 = none)", ref budget, 0, 0)) { Config.TotalGilBudget = Math.Max(0, budget); Config.Save(); }

        var useNav = Config.UseNavigation;
        if (ImGui.Checkbox("Walk to a nearby board (vnavmesh)", ref useNav)) { Config.UseNavigation = useNav; Config.Save(); }
        ImGui.SameLine();
        var useLi = Config.UseLifestreamTravel;
        if (ImGui.Checkbox("Travel to a board (Lifestream \"/li mb\")", ref useLi)) { Config.UseLifestreamTravel = useLi; Config.Save(); }
        if (Config.UseLifestreamTravel)
        {
            ImGui.SameLine();
            var ok = Lifestream.Available;
            ImGui.TextColored(
                ok ? new Vector4(0.4f, 0.85f, 0.4f, 1f) : new Vector4(0.85f, 0.5f, 0.4f, 1f),
                ok ? "Lifestream ready" : "Lifestream not loaded");

            ImGui.SetNextItemWidth(200);
            var cities = MarketCities.All;
            var names = new string[cities.Count + 1];
            names[0] = "Auto (Ul'dah / nearest)";
            for (var i = 0; i < cities.Count; i++)
                names[i + 1] = cities[i].Display;
            var sel = 0;
            for (var i = 0; i < cities.Count; i++)
                if (string.Equals(cities[i].Key, Config.PreferredCity, StringComparison.OrdinalIgnoreCase))
                    sel = i + 1;
            if (Combo("Travel to##city", names, ref sel))
            {
                Config.PreferredCity = sel == 0 ? string.Empty : cities[sel - 1].Key;
                Config.Save();
            }

            // World to travel to first — only worlds the character can reach
            // (home region's data centres + Materia).
            var worlds = Worlds.ReachableWorlds();
            var wNames = new string[worlds.Count + 1];
            wNames[0] = "Current world";
            for (var i = 0; i < worlds.Count; i++)
                wNames[i + 1] = $"{worlds[i].Name}  ({worlds[i].DcName})";
            var wSel = 0;
            for (var i = 0; i < worlds.Count; i++)
                if (string.Equals(worlds[i].Name, Config.PreferredWorld, StringComparison.OrdinalIgnoreCase))
                    wSel = i + 1;
            // Saved world no longer reachable (e.g. logged into a different
            // character) — drop the pin.
            if (wSel == 0 && !string.IsNullOrEmpty(Config.PreferredWorld) && worlds.Count > 0)
            {
                Config.PreferredWorld = string.Empty;
                Config.Save();
            }
            ImGui.SetNextItemWidth(240);
            if (Combo("World##buyworld", wNames, ref wSel))
            {
                Config.PreferredWorld = wSel == 0 ? string.Empty : worlds[wSel - 1].Name;
                Config.Save();
            }

            ImGui.SetNextItemWidth(90);
            var retry = Math.Clamp(Config.TravelRetrySeconds, 0, 600);
            if (ImGui.InputInt("Retry a cancelled teleport for (s)##travelRetry", ref retry, 0, 0))
            {
                Config.TravelRetrySeconds = Math.Clamp(retry, 0, 600);
                Config.Save();
            }
        }
    }

    private void DrawCapture()
    {
        var rec = plugin.Recorder;
        ImGui.TextUnformatted("Behavior capture");

        if (rec.IsRecording)
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), $"● Recording — {rec.EventCount} events");
            ImGui.SameLine();
            if (ImGui.Button("Stop capture"))
                AppendLog(rec.Stop());
        }
        else
        {
            if (ImGui.Button("Capture normal behavior"))
            {
                AppendLog(rec.Start("manual"));
                AppendLog("Now go target a Market Board and buy an item normally. Click 'Stop capture' when done.");
            }
        }

        var capAuto = plugin.Configuration.CaptureAutomatedRuns;
        if (ImGui.Checkbox("Also record automated runs", ref capAuto))
        {
            plugin.Configuration.CaptureAutomatedRuns = capAuto;
            plugin.Configuration.Save();
        }

        if (rec.LastSavedPath is { } path)
            ImGui.TextDisabled($"Last: {path}");

        if (rec.LastSummary is { } s)
        {
            if (ImGui.TreeNode("Last capture summary"))
            {
                Row("Item", s.ItemName ?? s.ItemId?.ToString() ?? "—");
                if (s.PurchaseObserved)
                {
                    Row("Paid", $"{s.PaidUnitPrice:N0} x {s.PaidQuantity} {(s.PaidHq == true ? "HQ" : "NQ")}  = {s.PaidTotalGil:N0} gil");
                    Row("Rank chosen", s.ChosenRankByPrice is { } r ? $"#{r} cheapest of {s.OptionsAtPurchase.Count}" : "unknown");
                }
                else
                {
                    Row("Purchase", "none observed");
                }
                Row("target→search open", Ms(s.TargetBoardToSearchOpenMs));
                Row("search open→item picked", Ms(s.SearchOpenToFirstSearchMs));
                Row("item picked→listings", Ms(s.SearchToOfferingsMs));
                Row("listings→row click", Ms(s.OfferingsToRowClickMs));
                Row("row click→confirm", Ms(s.RowClickToConfirmMs));
                Row("confirm→yes", Ms(s.ConfirmToYesMs));
                Row("yes→server confirm", Ms(s.YesToServerConfirmMs));
                Row("scrolled / hovered", $"{s.Scrolled} / {s.HoveredRows}");
                Row("searches / pages", $"{s.SearchCount} / {s.OfferingsPagesReceived}");
                foreach (var note in s.Notes)
                    ImGui.TextWrapped("• " + note);
                if (s.OptionsAtPurchase.Count > 0 && ImGui.TreeNode("Options that were visible"))
                {
                    foreach (var o in s.OptionsAtPurchase)
                        ImGui.TextUnformatted($"  {o.UnitPrice,10:N0} x {o.Quantity,-4} {(o.Hq ? "HQ" : "  ")}  {o.RetainerName}");
                    ImGui.TreePop();
                }
                ImGui.TreePop();
            }
        }

        static string Ms(long? v) => v is { } x ? $"{x} ms" : "—";
    }

    private static void Row(string label, string value)
    {
        ImGui.TextDisabled(label + ":");
        ImGui.SameLine(220);
        ImGui.TextUnformatted(value);
    }

    private void DrawProgress()
    {
        ImGui.TextUnformatted("Status");

        var order = plugin.Runner.Active;
        if (order is null)
        {
            ImGui.TextDisabled(plugin.Orders.IsBusy ? "Queued…" : "Idle — no order running.");
            return;
        }

        var s = plugin.Runner.GetStatus();

        // headline: what it's doing right now
        ImGui.TextColored(new Vector4(0.55f, 0.85f, 1f, 1f), s.Activity);

        // where it is
        var where = $"item {s.ItemNumber}/{s.ItemCount}";
        if (!string.IsNullOrEmpty(s.ItemName))
            where += $"  ·  {s.ItemName}  ·  bought {s.Bought}/{s.Requested}";
        where += $"  ·  {s.GilSpent:N0} gil spent";
        ImGui.TextDisabled(where);
        ImGui.TextDisabled($"phase: {s.Phase}");

        // current delay it's waiting out
        if (s.WaitReason is not null && s.WaitTotalMs > 0)
        {
            var done = 1f - (float)(s.WaitRemainingMs / s.WaitTotalMs);
            ImGui.ProgressBar(Math.Clamp(done, 0f, 1f), new Vector2(-1, 0),
                $"{s.WaitReason}  ·  {s.WaitRemainingMs / 1000.0:0.0}s / {s.WaitTotalMs / 1000.0:0.0}s");
        }

        ImGui.Spacing();
        foreach (var it in order.Items)
        {
            var line = $"  {it.ItemName}: {it.PurchasedQuantity}/{it.RequestedQuantity}";
            if (it.StoppedReason != StopReason.None) line += $"  [{it.StoppedReason}]";
            if (it.NextLowestUnitPrice is { } n) line += $"  next {n:N0} ×{it.NextLowestQuantity}";
            ImGui.TextUnformatted(line);
        }
    }

    private void DrawPrices()
    {
        ImGui.TextWrapped("Universalis price lookup — works anywhere, no Market Board needed.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##priceItem", "item name", ref priceItemInput, 128);

        ImGui.SetNextItemWidth(220);
        Combo("Scope##priceScope", ScopeLabels, ref priceScopeIdx);

        if (priceScopeIdx != 3)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(160);
            var hint = priceScopeIdx switch { 0 => "world (blank = current)", 1 => "DC (blank = current)", _ => "region (blank = current)" };
            ImGui.InputTextWithHint("##priceTarget", hint, ref priceTarget, 64);
        }

        var scope = (PriceScope)priceScopeIdx;

        if (ImGui.Button("Look up"))
        {
            priceMsg = string.Empty;
            var id = ItemResolver.ResolveExact(priceItemInput);
            if (id == 0)
            {
                var hits = ItemResolver.Search(priceItemInput, 1);
                id = hits.Count == 1 ? hits[0].ItemId : 0;
            }
            if (id == 0)
                priceMsg = $"No marketable item matches \"{priceItemInput}\".";
            else
                priceActive = new PriceRequest(new[] { id }, scope, string.IsNullOrWhiteSpace(priceTarget) ? null : priceTarget.Trim());
        }
        ImGui.SameLine();
        if (ImGui.Button("Refresh") && priceActive is not null)
            plugin.Prices.Lookup(priceActive, refresh: true);

        var home = Worlds.HomeWorld();
        if (home is not null)
            ImGui.TextDisabled($"you: {Worlds.CurrentWorld()?.Name ?? home.Name} · {home.DcName} · {home.RegionName}");

        if (!string.IsNullOrEmpty(priceMsg))
            ImGui.TextColored(new Vector4(0.9f, 0.6f, 0.4f, 1f), priceMsg);

        if (priceActive is null)
            return;

        var result = plugin.Prices.Lookup(priceActive);
        if (result.Error is not null)
        {
            ImGui.TextColored(new Vector4(0.9f, 0.5f, 0.4f, 1f), result.Error);
            return;
        }
        if (result.Pending.Count > 0 && result.Ready.Count == 0)
        {
            ImGui.TextDisabled("Fetching from Universalis…");
            return;
        }

        foreach (var ip in result.Ready.Values)
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.55f, 0.85f, 1f, 1f), $"{ip.ItemName}  ({ip.Scope})");
            if (ip.Error is not null) { ImGui.TextDisabled(ip.Error); continue; }

            if (ImGui.BeginTable("##prices", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Level");
                ImGui.TableSetupColumn("Cheapest NQ");
                ImGui.TableSetupColumn("Cheapest HQ");
                ImGui.TableSetupColumn("Avg sale");
                ImGui.TableSetupColumn("Sales/day");
                ImGui.TableSetupColumn("Recent");
                ImGui.TableHeadersRow();

                foreach (var lvl in ip.Levels)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(lvl.Location.Length > 0 ? $"{lvl.Level} ({lvl.Location})" : lvl.Level);
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(FmtPoint(lvl.Nq.MinListing));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(FmtPoint(lvl.Hq.MinListing));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(FmtAvg(lvl.Nq.AverageSalePrice, lvl.Hq.AverageSalePrice));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(FmtAvg(lvl.Nq.DailySaleVelocity, lvl.Hq.DailySaleVelocity));
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(FmtPoint(lvl.Nq.RecentPurchase ?? lvl.Hq.RecentPurchase));
                }
                ImGui.EndTable();
            }
        }

        static string FmtPoint(PricePoint? p)
        {
            if (p is null) return "—";
            var s = $"{p.Price:N0}g";
            if (!string.IsNullOrEmpty(p.World)) s += $" @ {p.World}";
            if (!string.IsNullOrEmpty(p.Age)) s += $" ({p.Age})";
            return s;
        }

        static string FmtAvg(double? nq, double? hq)
        {
            if (nq is null && hq is null) return "—";
            var parts = new List<string>();
            if (nq is { } n) parts.Add($"{n:N0}");
            if (hq is { } h) parts.Add($"{h:N0} HQ");
            return string.Join(" / ", parts);
        }
    }

    private void DrawLog()
    {
        ImGui.TextUnformatted("Log");
        if (ImGui.BeginChild("##emptorLog", new Vector2(0, 140), true))
        {
            foreach (var line in log)
                ImGui.TextUnformatted(line);
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1)
                ImGui.SetScrollHereY(1f);
        }
        ImGui.EndChild();
    }

    private static bool Combo(string id, string[] labels, ref int current)
    {
        var changed = false;
        var preview = current >= 0 && current < labels.Length ? labels[current] : "?";
        if (ImGui.BeginCombo(id, preview))
        {
            for (var i = 0; i < labels.Length; i++)
            {
                var selected = i == current;
                if (ImGui.Selectable(labels[i], selected)) { current = i; changed = true; }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        return changed;
    }
}
