using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using ECommons;
using Emptor.Buying;
using Emptor.Capture;
using Emptor.Gui;
using Emptor.Ipc;

namespace Emptor;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IMarketBoard MarketBoard { get; private set; } = null!;
    [PluginService] internal static INotificationManager NotificationManager { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;

    internal static Plugin Instance { get; private set; } = null!;

    private const string CommandName = "/emptor";

    internal Configuration Configuration { get; }
    internal MarketBuyRunner Runner { get; }
    internal OrderQueue Orders { get; }
    internal BehaviorRecorder Recorder { get; }

    private readonly EmptorIpc ipc;
    private readonly WindowSystem windowSystem = new("Emptor");
    private readonly ConfigWindow configWindow;

    public Plugin()
    {
        Instance = this;
        ECommonsMain.Init(PluginInterface, this);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Recorder = new BehaviorRecorder();
        Runner = new MarketBuyRunner(Recorder);
        Orders = new OrderQueue(Runner);
        ipc = new EmptorIpc(Orders, Runner);

        configWindow = new ConfigWindow(this);
        Runner.Log = configWindow.AppendLog;
        windowSystem.AddWindow(configWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Emptor window.",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMainUi;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;

        Log.Information("[Emptor] Loaded. Use /emptor.");
    }

    private void OnCommand(string command, string args) => OpenMainUi();

    private void OpenMainUi() => configWindow.Toggle();

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMainUi;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        windowSystem.RemoveAllWindows();
        CommandManager.RemoveHandler(CommandName);

        ipc.Dispose();
        Orders.Dispose();
        Runner.Dispose();
        Recorder.Dispose();
        configWindow.Dispose();

        ECommonsMain.Dispose();
    }
}
