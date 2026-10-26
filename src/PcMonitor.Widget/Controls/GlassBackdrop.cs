using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PcMonitor.Widget.Controls;

/// <summary>
/// Thin desktop acrylic that stays "active" even when the window is not focused - an always-on-top widget is
/// almost never focused, and the stock backdrops would turn into a flat fallback colour.
/// </summary>
public sealed class GlassBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _config;

    public Color Tint { get; set; } = Color.FromArgb(0xFF, 0x14, 0x16, 0x1C);
    public float TintOpacity { get; set; } = 0.35f;
    public float LuminosityOpacity { get; set; } = 0.55f;
    public Color Fallback { get; set; } = Color.FromArgb(0xE6, 0x1C, 0x1E, 0x26);

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _config = new SystemBackdropConfiguration { IsInputActive = true, Theme = SystemBackdropTheme.Dark };
        _controller = new DesktopAcrylicController
        {
            Kind = DesktopAcrylicKind.Thin,
            TintColor = Tint,
            TintOpacity = TintOpacity,
            LuminosityOpacity = LuminosityOpacity,
            FallbackColor = Fallback,
        };
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_config);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
        _config = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }
}
