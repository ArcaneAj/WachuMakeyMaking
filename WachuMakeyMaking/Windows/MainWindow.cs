using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Interface.Windowing;
using WachuMakeyMaking.Services;

namespace WachuMakeyMaking.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly TabService tabService;
    private readonly InventoryService inventoryService;

    public MainWindow(TabService tabService, InventoryService inventoryService)
        : base($"{Plugin.Name}?##{Plugin.Name}ID", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.tabService = tabService;
        this.inventoryService = inventoryService;

        // Subscribe to inventory changes
        Plugin.GameInventory.InventoryChanged += OnInventoryChanged;
    }

    public void Dispose()
    {
        // Unsubscribe from inventory changes
        Plugin.GameInventory.InventoryChanged -= OnInventoryChanged;
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        if (
            events.Any(e =>
                e.Type == GameInventoryEvent.Added
                || e.Type == GameInventoryEvent.Removed
                || e.Type == GameInventoryEvent.Changed
            )
        )
        {
            this.inventoryService.Init();
        }
    }

    public override void Draw()
    {
        this.tabService.Draw();
    }
}
