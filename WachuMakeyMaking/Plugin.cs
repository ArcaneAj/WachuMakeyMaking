using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
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
        CollectableService = new CollectableService();
        InventoryService = new InventoryService();
        UniversalisService = new UniversalisService(CollectableService);
        RecipeService = new RecipeService(UniversalisService);
        SolverService = new SolverService(l => Log.Info(l), l => Log.Error(l), RecipeService);
        TabService = new TabService(RecipeService, InventoryService, SolverService);
        MainWindow = new MainWindow(TabService);

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
        Plugin.GameInventory.InventoryChanged += OnInventoryChanged;
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
        GameInventory.InventoryChanged -= OnInventoryChanged;
        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        ClientState.Login -= OnLogin;
        ClientState.Logout -= OnLogout;

        WindowSystem.RemoveAllWindows();

        MainWindow.Dispose();
        UniversalisService.Dispose();
        SolverService.Reset();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        if (
            events.Any(e =>
                e.Type == GameInventoryEvent.Added
                || e.Type == GameInventoryEvent.Removed
                || e.Type == GameInventoryEvent.Changed
                || e.Type == GameInventoryEvent.Moved //If we drag from player to retainer this is all that's fired, and it only has the destination's ContainerType
            )
        )
        {
            InventoryService.Update(
                [.. events.Select(x => x.Item.ContainerType)],
                events.Any(e => e.Type == GameInventoryEvent.Moved)
            );
        }
    }

    private void OnCommand(string command, string args)
    {
        // In response to the slash command, toggle the display status of our main ui
        MainWindow.Toggle();
    }

    public void ToggleMainUi() => MainWindow.Toggle();
}
