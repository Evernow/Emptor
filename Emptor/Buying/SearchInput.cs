using System;
using Emptor.GameData;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Emptor.Buying;

/// <summary>
/// Text entry into the market-board Item Search box using REAL keystroke
/// messages. Nothing from managed code (SetText, InsertText, input callbacks,
/// RunSearch, clicking the Search button — which crashed the client twice) makes
/// the client run its search. Only genuine typed input + a real Enter does.
/// Partial Match is left exactly as the player has it.
/// </summary>
public static unsafe class SearchInput
{
    private static AddonItemSearch* Addon()
        => Plugin.GameGui.GetAddonByName<AddonItemSearch>("ItemSearch", 1);

    public static bool Ready()
    {
        var a = Addon();
        return a != null && a->AtkUnitBase.IsReady && a->AtkUnitBase.IsVisible;
    }

    /// <summary>Normal mode + focus the text input + clear it with backspaces. One step per call.</summary>
    public static bool Prepare()
    {
        var addon = Addon();
        if (addon == null || !addon->AtkUnitBase.IsReady || !addon->AtkUnitBase.IsVisible)
            return false;

        if (addon->Mode != AddonItemSearch.SearchMode.Normal)
        {
            addon->SetModeFilter(AddonItemSearch.SearchMode.Normal, 0);
            return false;
        }

        var input = addon->SearchTextInput;
        if (input == null)
            return false;
        var inputBase = &input->AtkComponentInputBase;

        Focus(addon, input, inputBase);

        var current = input->AtkComponentInputBase.EvaluatedString.ToString();
        if (current.Length > 0)
        {
            // real backspaces (and select-all-delete as a backup)
            input->SetText(string.Empty);
            Keyboard.Backspace(current.Length + 4);
            return false;
        }

        return inputBase->IsActive;
    }

    public static void Refocus()
    {
        var addon = Addon();
        if (addon == null || addon->SearchTextInput == null)
            return;
        Focus(addon, addon->SearchTextInput, &addon->SearchTextInput->AtkComponentInputBase);
    }

    /// <summary>Send one character as a real keystroke.</summary>
    public static void TypeChar(char c)
    {
        Refocus();
        Keyboard.TypeChar(c);
    }

    /// <summary>Real Enter keystroke to submit the search.</summary>
    public static void Submit()
    {
        Refocus();
        Keyboard.Enter();
    }

    public static (string Text, bool Working, int AgentItems) Observe()
    {
        var addon = Addon();
        if (addon == null || addon->SearchTextInput == null)
            return (string.Empty, false, 0);
        var text = addon->SearchTextInput->AtkComponentInputBase.EvaluatedString.ToString();
        var agent = AgentItemSearch.Instance();
        var working = agent != null && (agent->IsPartialSearching || agent->IsItemPushPending);
        var items = agent == null ? 0 : (int)agent->ItemCount;
        return (text, working, items);
    }

    private static void Focus(AddonItemSearch* addon, AtkComponentTextInput* input, AtkComponentInputBase* inputBase)
    {
        var unitBase = &addon->AtkUnitBase;
        AtkResNode* focusNode = inputBase->CollisionNode != null
            ? &inputBase->CollisionNode->AtkResNode
            : (inputBase->AtkComponentBase.OwnerNode != null ? &inputBase->AtkComponentBase.OwnerNode->AtkResNode : null);
        if (focusNode != null)
        {
            unitBase->Focus();
            unitBase->SetFocusNode(focusNode, true);
            unitBase->SetComponentFocusNode(&inputBase->AtkComponentBase);
            var stage = AtkStage.Instance();
            if (stage != null && stage->AtkInputManager != null)
                stage->AtkInputManager->SetFocus(focusNode, unitBase, 0);
        }
        inputBase->IsActive = true;
    }
}
