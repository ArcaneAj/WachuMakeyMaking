using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using WachuMakeyMaking.Services;

namespace WachuMakeyMaking.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly TabService tabService;

    public MainWindow(TabService tabService)
        : base($"{Plugin.Name}?##{Plugin.Name}ID", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.tabService = tabService;
    }

    public void Dispose() { }

    public override void Draw()
    {
        this.tabService.Draw();
    }
}
