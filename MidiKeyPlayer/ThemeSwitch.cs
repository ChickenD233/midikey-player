using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace MidiKeyPlayer;

internal static class ThemeSwitch
{
    public const int Auto = 0;
    public const int Light = 1;
    public const int Dark = 2;

    public static readonly string[] Names = { "自动（跟随系统）", "浅色（白）", "深色（黑）" };

    public static int Clamp(int mode) => mode is >= Auto and <= Dark ? mode : Auto;

    public static ThemeVariant VariantOf(int mode) => Clamp(mode) switch
    {
        Light => ThemeVariant.Light,
        Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    public static void Apply(int mode)
    {
        if (Application.Current is { } app) app.RequestedThemeVariant = VariantOf(mode);
    }

    public static IBrush? BrushOf(string key)
    {
        var app = Application.Current;
        if (app == null) return null;
        if (app.TryFindResource(key, app.ActualThemeVariant, out var themed) && themed is IBrush tb)
            return tb;
        if (app.TryFindResource(key, out var any) && any is IBrush ab)
            return ab;
        return null;
    }
}
