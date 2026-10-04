using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System;
using WachuMakeyMaking.Models;
using WachuMakeyMaking.Services;
using WachuMakeyMaking.Utils;
using WachuMakeyMaking.Windows;

namespace WachuMakeyMaking;

public sealed class Plugin : IDalamudPlugin
{
    public static readonly string Name = "WachuMakeyMaking";

    [PluginService]
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    [PluginService]
    internal static ITextureProvider TextureProvider { get; private set; } = null!;

    [PluginService]
    internal static ICommandManager CommandManager { get; private set; } = null!;

    [PluginService]
    internal static IClientState ClientState { get; private set; } = null!;

    [PluginService]
    internal static IPlayerState PlayerState { get; private set; } = null!;

    [PluginService]
    internal static IDataManager DataManager { get; private set; } = null!;

    [PluginService]
    internal static IGameInventory GameInventory { get; private set; } = null!;

    [PluginService]
    internal static IChatGui ChatGui { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/wymm";

    public readonly WindowSystem WindowSystem = new(Plugin.Name);
    private MainWindow MainWindow { get; init; }
    private UniversalisService UniversalisService { get; init; }
    private CollectableService CollectableService { get; init; }
    public InventoryService InventoryService { get; init; }
    private SolverService SolverService { get; init; }
    public RecipeService RecipeService { get; init; }
    private TabService TabService { get; init; }

    public Plugin()
    {
        UniversalisService = new UniversalisService();
        CollectableService = new CollectableService();
        InventoryService = new InventoryService();
        RecipeService = new RecipeService(UniversalisService, CollectableService);
        SolverService = new SolverService(l => Log.Info(l), l => Log.Error(l), RecipeService);
        TabService = new TabService(RecipeService, InventoryService, SolverService);
        MainWindow = new MainWindow(TabService, InventoryService);

        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(
            CommandName,
            new CommandInfo(OnCommand) { HelpMessage = "toggles the window open/closed" }
        );

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        // Adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the main ui
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        ClientState.Login += OnLogin;
        ClientState.Logout += OnLogout;
    }

    private void OnLogout(int type, int code)
    {
        "Logged OUT!".Log();
        InventoryService.Clear();
    }

    private void OnLogin()
    {
        "Logged IN!".Log();
        InventoryService.Init();
    }

    public void Dispose()
    {
        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        ClientState.Login -= OnLogin;

        WindowSystem.RemoveAllWindows();

        MainWindow.Dispose();
        UniversalisService.Dispose();
        SolverService.Reset();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        // In response to the slash command, toggle the display status of our main ui
        MainWindow.Toggle();
    }

    public void ToggleMainUi() => MainWindow.Toggle();
}
