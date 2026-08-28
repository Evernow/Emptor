using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Emptor.Buying;

/// <summary>
/// Serialises buy orders onto the single <see cref="MarketBuyRunner"/> and keeps
/// finished orders around so callers can still read their results.
/// </summary>
public sealed class OrderQueue : IDisposable
{
    private const int MaxRemembered = 50;

    private readonly MarketBuyRunner runner;
    private readonly Queue<BuyOrder> pending = new();
    private readonly ConcurrentDictionary<string, BuyOrder> byId = new();
    private readonly List<string> finishedOrder = new();
    private readonly object gate = new();

    public OrderQueue(MarketBuyRunner runner)
    {
        this.runner = runner;
        runner.OrderFinished += OnOrderFinished;
        Plugin.Framework.Update += OnUpdate;
    }

    public bool IsBusy => runner.IsBusy;

    public BuyOrder? Active => runner.Active;

    /// <summary>Adds an order to the queue. It starts as soon as the runner is free.</summary>
    public BuyOrder Submit(BuyRequest request, bool fromUi)
    {
        var order = new BuyOrder
        {
            ClientRequestId = request.ClientRequestId,
            Request = request,
            TotalGilBudget = request.TotalGilBudget,
            FromUi = fromUi,
            State = OrderState.Queued,
            Message = "Queued.",
        };
        byId[order.OrderId] = order;

        lock (gate)
        {
            pending.Enqueue(order);
            Remember(order.OrderId);
        }

        return order;
    }

    public BuyOrder? Get(string orderId) => byId.TryGetValue(orderId, out var o) ? o : null;

    public bool Cancel(string orderId)
    {
        var order = Get(orderId);
        if (order is null)
            return false;

        if (ReferenceEquals(order, runner.Active))
        {
            runner.RequestCancel();
            return true;
        }

        lock (gate)
        {
            if (order.State == OrderState.Queued)
            {
                order.State = OrderState.Cancelled;
                order.Message = "Cancelled before it started.";
                order.FinishedUtc = DateTime.UtcNow;
                return true;
            }
        }

        return false;
    }

    private void OnUpdate(Dalamud.Plugin.Services.IFramework _)
    {
        if (runner.IsBusy)
            return;

        BuyOrder? next = null;
        lock (gate)
        {
            while (pending.Count > 0)
            {
                var candidate = pending.Dequeue();
                if (candidate.State == OrderState.Queued)
                {
                    next = candidate;
                    break;
                }
            }
        }

        if (next is null)
            return;

        var error = runner.TryStart(next);
        if (error is not null)
        {
            next.State = OrderState.Rejected;
            next.Message = error;
            next.FinishedUtc = DateTime.UtcNow;
        }
    }

    private void OnOrderFinished(BuyOrder order) => byId[order.OrderId] = order;

    private void Remember(string orderId)
    {
        finishedOrder.Add(orderId);
        while (finishedOrder.Count > MaxRemembered)
        {
            var evict = finishedOrder[0];
            finishedOrder.RemoveAt(0);
            var order = Get(evict);
            if (order is null || order.IsTerminal)
                byId.TryRemove(evict, out _);
            else
                finishedOrder.Add(evict); // still running, keep it
        }
    }

    public void Dispose()
    {
        runner.OrderFinished -= OnOrderFinished;
        Plugin.Framework.Update -= OnUpdate;
    }
}
