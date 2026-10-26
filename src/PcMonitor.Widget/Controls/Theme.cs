using Microsoft.UI.Xaml.Media;
using PcMonitor.Core;
using Windows.UI;

namespace PcMonitor.Widget.Controls;

/// <summary>Colours and text helpers shared by every view so each resource keeps one identity colour.</summary>
public static class Theme
{
    public static readonly Color Cpu = Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF);
    public static readonly Color Ram = Color.FromArgb(0xFF, 0xB4, 0x8C, 0xFF);
    public static readonly Color Gpu = Color.FromArgb(0xFF, 0x5E, 0xE6, 0xA8);
    public static readonly Color Net = Color.FromArgb(0xFF, 0xFF, 0xB4, 0x54);
    public static readonly Color Disk = Color.FromArgb(0xFF, 0xFF, 0x7A, 0x90);
    public static readonly Color Warn = Color.FromArgb(0xFF, 0xFF, 0xB0, 0x20);
    public static readonly Color Danger = Color.FromArgb(0xFF, 0xFF, 0x5C, 0x5C);
    public static readonly Color Good = Color.FromArgb(0xFF, 0x5E, 0xE6, 0xA8);

    public static SolidColorBrush Brush(Color c) => new(c);
    public static SolidColorBrush Faint { get; } = new(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    public static SolidColorBrush Strong { get; } = new(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    public static SolidColorBrush Hairline { get; } = new(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

    public static string Describe(AppFlags f)
    {
        var parts = new List<string>();
        if (f.HasFlag(AppFlags.InvalidSignature)) parts.Add("signature is invalid");
        else if (f.HasFlag(AppFlags.Unsigned)) parts.Add("unsigned");
        if (f.HasFlag(AppFlags.UserWritablePath)) parts.Add("runs from a user-writable folder");
        if (f.HasFlag(AppFlags.HiddenHeavyUsage)) parts.Add("heavy use with no visible window");
        if (f.HasFlag(AppFlags.IdleGpuUsage)) parts.Add("GPU busy while you are away");
        if (f.HasFlag(AppFlags.ManyRemoteHosts)) parts.Add("talks to many remote hosts");
        return string.Join("; ", parts);
    }

    /// <summary>Behaviour flags are alarming; static ones (unsigned, odd folder) are just worth a look.</summary>
    public static bool IsAlarming(AppFlags f) =>
        (f & (AppFlags.HiddenHeavyUsage | AppFlags.IdleGpuUsage | AppFlags.ManyRemoteHosts | AppFlags.InvalidSignature)) != 0;
}
