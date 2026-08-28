using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Emptor.Buying;

namespace Emptor;

/// <summary>One editable row in the config-window shopping list.</summary>
[Serializable]
public sealed class ShoppingListRow
{
    public bool Enabled { get; set; } = true;
    public string ItemName { get; set; } = string.Empty;
    public uint ItemId { get; set; }
    public int Quantity { get; set; } = 1;
    public long MaxUnitPrice { get; set; } = 1000;
    public QualityFilter Quality { get; set; } = QualityFilter.Either;
    public OvershootPolicy Overshoot { get; set; } = OvershootPolicy.Allow;
    public int OvershootLimitPercent { get; set; } = 25;

    public BuyRequestItem ToRequestItem() => new()
    {
        ItemId = ItemId,
        ItemName = ItemName,
        MaxUnitPrice = MaxUnitPrice,
        Quantity = Quantity,
        Quality = Quality,
        Overshoot = Overshoot,
        OvershootLimitPercent = OvershootLimitPercent,
    };
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public List<ShoppingListRow> ShoppingList { get; set; } = new();

    /// <summary>Optional cap on total gil spent by a config-window run. 0 = no cap.</summary>
    public long TotalGilBudget { get; set; }

    public int PaceMinMs { get; set; } = 1300;
    public int PaceMaxMs { get; set; } = 1900;

    /// <summary>Hide the marketboard window when a run finishes.</summary>
    public bool HideBoardWhenFinished { get; set; } = true;

    // --- human emulation (calibrated from captures) -------------------

    /// <summary>Add human-like think-time, real board interaction, faithful clicks.</summary>
    public bool HumanEmulation { get; set; } = true;

    /// <summary>Multiplier on every emulated delay. 1.0 = nominal, &lt;1 impatient, &gt;1 leisurely.</summary>
    public float EmulationSpeed { get; set; } = 1.0f;

    /// <summary>Walk to the nearest Market Board with vnavmesh if it is not in interact range.</summary>
    public bool UseNavigation { get; set; } = true;

    /// <summary>Record every automated run to a capture file for later diffing.</summary>
    public bool CaptureAutomatedRuns { get; set; }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
