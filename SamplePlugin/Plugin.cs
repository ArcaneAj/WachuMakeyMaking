using Dalamud.Game.Command;
using Dalamud.Game.Inventory;
using Dalamud.IoC;
using Dalamud.Plugin;
using System.IO;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using SamplePlugin.Windows;
using SamplePlugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace SamplePlugin;

public sealed class Plugin : IDalamudPlugin
{
    public static readonly string Name = "WachuMakeyMaking";
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/wymm";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new(Plugin.Name);
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private UniversalisService UniversalisService { get; init; }
    private CollectableService CollectableService { get; init; }
    private RecipeCacheService RecipeCacheService { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        UniversalisService = new UniversalisService(this);
        CollectableService = new CollectableService(this);
        RecipeCacheService = new RecipeCacheService(this, UniversalisService, CollectableService);
        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this, RecipeCacheService);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "\"/wymm\" can be used to toggle the window open/closed"
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        // This adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
    }

    public void Dispose()
    {
        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        UniversalisService.Dispose();
        RecipeCacheService.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        // In response to the slash command, toggle the display status of our main ui
        MainWindow.Toggle();
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
