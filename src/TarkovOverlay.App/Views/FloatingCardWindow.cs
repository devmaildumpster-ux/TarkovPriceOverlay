using System.Windows;
using System.Windows.Input;
using TarkovOverlay.App.Overlay;
using TarkovOverlay.Core.Presentation;

namespace TarkovOverlay.App.Views;

/// <summary>Movable, always-on-top detail card (drag anywhere; Esc or double-click closes).</summary>
public sealed class FloatingCardWindow : Window
{
    public FloatingCardWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize;
        Title = "Tarkov item card";
        MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Close(); else DragMove(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        ToolTip = "Drag to move · double-click or Esc to close";
    }

    public void Show(CardModel model)
    {
        Content = CardRenderer.Build(model, 1.5, interactive: true);
        if (!IsVisible) Show();
    }
}
