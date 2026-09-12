using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Slate;

internal static class SlateTheme
{
    internal const double RadiusSmall = 4;
    internal const double RadiusMedium = 8;
    internal const double RadiusLarge = 10;

    internal static readonly string[] AccentNames = ["Slate", "Cobalt", "Moss", "Plum", "Clay", "Amber", "System"];

    internal sealed record Palette(
        bool Dark,
        // Semantic Surfaces
        Color SurfaceBase,
        Color SurfaceRaised,
        Color SurfaceInteractive,
        Color SurfaceInteractiveHover,
        Color SurfacePressed,
        Color SurfaceSelected,
        Color Sidebar,
        // Semantic Accents
        Color AccentPrimary,
        Color AccentHover,
        Color AccentPressed,
        Color AccentSoft,
        Color AccentSoftHover,
        Color AccentBorder,
        Color OnAccent,
        // Semantic Text
        Color TextPrimary,
        Color TextSecondary,
        Color TextMuted,
        // Semantic State & Structure
        Color FocusRing,
        Color Divider,
        Color BorderStrong,
        Color Chrome)
    {
        // Aliases for backward compatibility and convenience
        internal Color Page => SurfaceBase;
        internal Color Surface => SurfaceRaised;
        internal Color SurfaceHover => SurfaceInteractiveHover;
        internal Color Accent => AccentPrimary;
        internal Color Text => TextPrimary;
        internal Color Border => Divider;

        // Semantic surface brushes
        internal SolidColorBrush SurfaceBaseBrush => new(SurfaceBase);
        internal SolidColorBrush SurfaceRaisedBrush => new(SurfaceRaised);
        internal SolidColorBrush SurfaceInteractiveBrush => new(SurfaceInteractive);
        internal SolidColorBrush SurfaceInteractiveHoverBrush => new(SurfaceInteractiveHover);
        internal SolidColorBrush SurfacePressedBrush => new(SurfacePressed);
        internal SolidColorBrush SurfaceSelectedBrush => new(SurfaceSelected);
        internal SolidColorBrush SidebarBrush => new(Sidebar);

        // Semantic accent brushes
        internal SolidColorBrush AccentPrimaryBrush => new(AccentPrimary);
        internal SolidColorBrush AccentHoverBrush => new(AccentHover);
        internal SolidColorBrush AccentPressedBrush => new(AccentPressed);
        internal SolidColorBrush AccentSoftBrush => new(AccentSoft);
        internal SolidColorBrush AccentSoftHoverBrush => new(AccentSoftHover);
        internal SolidColorBrush AccentBorderBrush => new(AccentBorder);
        internal SolidColorBrush OnAccentBrush => new(OnAccent);

        // Semantic text brushes
        internal SolidColorBrush TextPrimaryBrush => new(TextPrimary);
        internal SolidColorBrush TextSecondaryBrush => new(TextSecondary);
        internal SolidColorBrush TextMutedBrush => new(TextMuted);

        // Semantic state & structure brushes
        internal SolidColorBrush FocusRingBrush => new(FocusRing);
        internal SolidColorBrush DividerBrush => new(Divider);
        internal SolidColorBrush BorderStrongBrush => new(BorderStrong);
        internal SolidColorBrush ChromeBrush => new(Chrome);

        // Legacy brush aliases
        internal SolidColorBrush PageBrush => SurfaceBaseBrush;
        internal SolidColorBrush SurfaceBrush => SurfaceRaisedBrush;
        internal SolidColorBrush SurfaceHoverBrush => SurfaceInteractiveHoverBrush;
        internal SolidColorBrush AccentBrush => AccentPrimaryBrush;
        internal SolidColorBrush TextBrush => TextPrimaryBrush;
        internal SolidColorBrush BorderBrush => DividerBrush;
    }

    private sealed record ThemeDefinition(
        Color Page,
        Color Chrome,
        Color Sidebar,
        Color SurfaceRaised,
        Color SurfaceInteractive,
        Color SurfaceHover,
        Color SurfacePressed,
        Color Divider,
        Color BorderStrong,
        Color TextPrimary,
        Color TextSecondary,
        Color TextMuted,
        Color AccentPrimary);

    private readonly record struct HueVector(double R, double G, double B);

    private sealed record NeutralPalette(
        Color Page,
        Color Chrome,
        Color Sidebar,
        Color SurfaceRaised,
        Color SurfaceInteractive,
        Color SurfaceHover,
        Color SurfacePressed,
        Color Divider,
        Color BorderStrong,
        Color TextPrimary,
        Color TextSecondary,
        Color TextMuted,
        Color AccentPrimary);

    private static NeutralPalette GetNeutralReference(bool dark) => dark
        ? new(
            Rgb(23, 24, 27), Rgb(27, 28, 31), Rgb(25, 26, 29),
            Rgb(32, 34, 38), Rgb(37, 39, 44), Rgb(45, 48, 54), Rgb(40, 43, 49),
            Rgb(54, 58, 66), Rgb(74, 79, 89),
            Rgb(244, 245, 247), Rgb(176, 182, 192), Rgb(126, 132, 140),
            Rgb(91, 112, 151))
        : new(
            Rgb(247, 248, 250), Rgb(240, 242, 245), Rgb(237, 239, 242),
            Rgb(252, 252, 253), Rgb(244, 245, 247), Rgb(230, 233, 238), Rgb(221, 225, 231),
            Rgb(206, 210, 218), Rgb(176, 184, 196),
            Rgb(31, 33, 37), Rgb(86, 92, 102), Rgb(124, 131, 143),
            Rgb(79, 103, 143));

    private static (HueVector Hue, Color Accent) GetThemeParameters(string accentName, bool dark)
    {
        if (accentName == "System")
        {
            var sys = SystemAccent();
            double mean = (sys.R + sys.G + sys.B) / 3.0;
            double dr = Math.Clamp((sys.R - mean) / 48.0, -1.5, 1.5);
            double dg = Math.Clamp((sys.G - mean) / 48.0, -1.5, 1.5);
            double db = Math.Clamp((sys.B - mean) / 48.0, -1.5, 1.5);
            return (new HueVector(dr, dg, db), sys);
        }

        if (dark)
        {
            return accentName switch
            {
                "Cobalt" => (new(-0.4,  0.0,  1.4), Rgb(82, 135, 245)),
                "Moss"   => (new(-0.6,  0.7, -0.6), Rgb(78, 171, 115)),
                "Plum"   => (new( 0.6, -0.3,  0.9), Rgb(180, 105, 207)),
                "Clay"   => (new( 1.1, -0.2, -0.6), Rgb(217, 114, 76)),
                "Amber"  => (new( 0.9,  0.3, -1.5), Rgb(222, 156, 44)),
                _        => (new( 0.0,  0.0,  0.0), Rgb(91, 112, 151)) // Slate
            };
        }
        else
        {
            return accentName switch
            {
                "Cobalt" => (new(-0.6, -0.2,  0.8), Rgb(37, 99, 235)),
                "Moss"   => (new(-0.5,  0.5, -0.6), Rgb(45, 125, 79)),
                "Plum"   => (new( 0.5, -0.6,  0.6), Rgb(142, 61, 174)),
                "Clay"   => (new( 0.8, -0.4, -0.7), Rgb(180, 79, 46)),
                "Amber"  => (new( 0.6,  0.2, -1.2), Rgb(168, 109, 18)),
                _        => (new( 0.0,  0.0,  0.0), Rgb(79, 103, 143)) // Slate
            };
        }
    }

    private static Color ApplyChroma(Color baseColor, HueVector hue, double weight)
    {
        if (hue.R == 0 && hue.G == 0 && hue.B == 0) return baseColor;
        byte r = (byte)Math.Clamp(Math.Round(baseColor.R + hue.R * weight), 0, 255);
        byte g = (byte)Math.Clamp(Math.Round(baseColor.G + hue.G * weight), 0, 255);
        byte b = (byte)Math.Clamp(Math.Round(baseColor.B + hue.B * weight), 0, 255);
        return Color.FromArgb(baseColor.A, r, g, b);
    }

    private static ThemeDefinition GetThemeDefinition(string accentName, bool dark)
    {
        var neutral = GetNeutralReference(dark);
        var (hue, accent) = GetThemeParameters(accentName, dark);

        // Structural surfaces carry temperature; interaction surfaces add a little
        // more chroma. Selection receives a separate tonal accent below.
        double wPage = 2.0;
        double wChrome = 4.0;
        double wSidebar = 4.5;
        double wRaised = 3.5;
        double wInteractive = 5.0;
        double wHover = 7.0;
        double wPressed = 8.0;
        double wDivider = 3.5;
        double wBorderStrong = 5.5;
        double wTextSec = dark ? 4.0 : 5.0;

        return new ThemeDefinition(
            ApplyChroma(neutral.Page, hue, wPage),
            ApplyChroma(neutral.Chrome, hue, wChrome),
            ApplyChroma(neutral.Sidebar, hue, wSidebar),
            ApplyChroma(neutral.SurfaceRaised, hue, wRaised),
            ApplyChroma(neutral.SurfaceInteractive, hue, wInteractive),
            ApplyChroma(neutral.SurfaceHover, hue, wHover),
            ApplyChroma(neutral.SurfacePressed, hue, wPressed),
            ApplyChroma(neutral.Divider, hue, wDivider),
            ApplyChroma(neutral.BorderStrong, hue, wBorderStrong),
            neutral.TextPrimary,
            ApplyChroma(neutral.TextSecondary, hue, wTextSec),
            neutral.TextMuted,
            accent);
    }

    internal static Palette Create(string accentName, bool dark)
    {
        var def = GetThemeDefinition(accentName, dark);

        var page = def.Page;
        var white = Rgb(255, 255, 255);
        var black = Rgb(0, 0, 0);
        var toward = dark ? white : black;

        var accent = def.AccentPrimary;
        if (dark) accent = Mix(accent, white, 0.16);
        accent = EnsureContrast(accent, page, 4.5, toward);

        var hover = Mix(accent, dark ? white : black, dark ? 0.10 : 0.08);
        var pressed = Mix(accent, dark ? black : white, 0.15);
        var accentBorder = Mix(accent, dark ? white : black, dark ? 0.20 : 0.18);

        var accentSoft = Mix(def.SurfaceInteractive, accent, dark ? 0.16 : 0.10);
        var accentSoftHover = Mix(def.SurfaceHover, accent, dark ? 0.20 : 0.14);

        // Labels occur on controls and selected rows as well as the page. Check
        // every opaque background so a theme never makes those labels disappear.
        Color[] textSurfaces = [page, def.Chrome, def.Sidebar, def.SurfaceRaised,
            def.SurfaceInteractive, def.SurfaceHover, def.SurfacePressed, accentSoft, accentSoftHover];
        var text = EnsureSurfaceContrast(def.TextPrimary, textSurfaces, 7.0, toward);
        var textSecondary = EnsureSurfaceContrast(def.TextSecondary, textSurfaces, 4.5, toward);
        var textMuted = EnsureSurfaceContrast(def.TextMuted, textSurfaces, 4.5, toward);
        accent = EnsureSurfaceContrast(accent, textSurfaces, 4.5, toward);
        var onAccent = ContrastRatio(white, accent) >= ContrastRatio(black, accent) ? white : black;
        hover = EnsureContrast(hover, onAccent, 4.5, onAccent == white ? black : white);
        pressed = EnsureContrast(pressed, onAccent, 4.5, onAccent == white ? black : white);

        return new Palette(
            dark,
            page,
            def.SurfaceRaised,
            def.SurfaceInteractive,
            def.SurfaceHover,
            def.SurfacePressed,
            accentSoft,
            def.Sidebar,
            accent,
            hover,
            pressed,
            accentSoft,
            accentSoftHover,
            accentBorder,
            onAccent,
            text,
            textSecondary,
            textMuted,
            accent,
            def.Divider,
            def.BorderStrong,
            def.Chrome);
    }

    internal static void ApplyResources(ResourceDictionary resources, Palette palette)
    {
        void Set(string key, Color value) => resources[key] = new SolidColorBrush(value);

        // Semantic Slate tokens
        Set("SlateSurfaceBaseBrush", palette.SurfaceBase);
        Set("SlateSurfaceRaisedBrush", palette.SurfaceRaised);
        Set("SlateSurfaceInteractiveBrush", palette.SurfaceInteractive);
        Set("SlateSurfaceInteractiveHoverBrush", palette.SurfaceInteractiveHover);
        Set("SlateSurfacePressedBrush", palette.SurfacePressed);
        Set("SlateSurfaceSelectedBrush", palette.SurfaceSelected);
        Set("SlateSidebarBrush", palette.Sidebar);

        Set("SlateAccentPrimaryBrush", palette.AccentPrimary);
        Set("SlateAccentHoverBrush", palette.AccentHover);
        Set("SlateAccentPressedBrush", palette.AccentPressed);
        Set("SlateAccentSoftBrush", palette.AccentSoft);
        Set("SlateAccentSoftHoverBrush", palette.AccentSoftHover);
        Set("SlateAccentBorderBrush", palette.AccentBorder);
        Set("SlateOnAccentBrush", palette.OnAccent);

        Set("SlateTextPrimaryBrush", palette.TextPrimary);
        Set("SlateTextSecondaryBrush", palette.TextSecondary);
        Set("SlateTextMutedBrush", palette.TextMuted);

        Set("SlateFocusRingBrush", palette.FocusRing);
        Set("SlateDividerBrush", palette.Divider);
        Set("SlateBorderStrongBrush", palette.BorderStrong);
        Set("SlateChromeBrush", palette.Chrome);

        // Legacy Slate tokens
        Set("SlatePageBrush", palette.Page);
        Set("SlateSurfaceBrush", palette.Surface);
        Set("SlateSurfaceHoverBrush", palette.SurfaceHover);
        Set("SlateSurfacePressedBrush", palette.SurfacePressed);
        Set("SlateBorderBrush", palette.Border);
        Set("SlateTextBrush", palette.Text);
        Set("SlateAccentBrush", palette.Accent);

        // WinUI's native states consume these resources, giving buttons, inputs, lists,
        // toggles, selection, and keyboard focus the same restrained accent language.
        Set("AccentFillColorDefaultBrush", palette.AccentPrimary);
        Set("AccentFillColorSecondaryBrush", palette.AccentHover);
        Set("AccentFillColorTertiaryBrush", palette.AccentPressed);
        Set("AccentTextFillColorPrimaryBrush", palette.AccentPrimary);
        Set("AccentTextFillColorSecondaryBrush", palette.AccentHover);
        Set("FocusStrokeColorOuterBrush", palette.FocusRing);
        Set("FocusStrokeColorInnerBrush", palette.SurfaceBase);
        Set("TextFillColorPrimaryBrush", palette.TextPrimary);
        Set("TextFillColorSecondaryBrush", palette.TextSecondary);
        Set("TextFillColorTertiaryBrush", palette.TextMuted);
        Set("TextOnAccentFillColorPrimaryBrush", palette.OnAccent);
        Set("ControlFillColorDefaultBrush", palette.SurfaceInteractive);
        Set("ControlFillColorSecondaryBrush", palette.SurfaceInteractiveHover);
        Set("ControlFillColorTertiaryBrush", palette.SurfacePressed);
        Set("ControlStrokeColorDefaultBrush", palette.Divider);
        Set("ControlStrokeColorSecondaryBrush", palette.BorderStrong);
        Set("ButtonBackgroundPointerOver", palette.SurfaceInteractiveHover);
        Set("ButtonBackgroundPressed", palette.SurfacePressed);
        Set("ButtonBorderBrushPointerOver", palette.BorderStrong);
        Set("ListViewItemBackgroundSelected", palette.SurfaceSelected);
        Set("ListViewItemBackgroundSelectedPointerOver", palette.AccentSoftHover);
        Set("ListViewItemBackgroundPointerOver", palette.SurfaceInteractiveHover);
        Set("ListViewItemBackgroundSelectedPressed", palette.SurfacePressed);
        Set("ListViewItemForegroundSelected", palette.TextPrimary);
        Set("ListViewItemForegroundSelectedPointerOver", palette.TextPrimary);
        Set("LayerFillColorDefaultBrush", palette.SurfaceRaised);
        Set("SolidBackgroundFillColorBaseBrush", palette.SurfaceBase);
        Set("ContentDialogBackground", palette.SurfaceRaised);
        Set("MenuFlyoutPresenterBackground", palette.SurfaceRaised);
        Set("FlyoutPresenterBackground", palette.SurfaceRaised);
    }

    private static Color SystemAccent()
    {
        try { return new UISettings().GetColorValue(UIColorType.Accent); }
        catch { return Rgb(91, 112, 151); }
    }

    internal static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    private static Color Mix(Color first, Color second, double amount)
    {
        byte Blend(byte a, byte b) => (byte)Math.Clamp(Math.Round(a + (b - a) * amount), 0, 255);
        return Rgb(Blend(first.R, second.R), Blend(first.G, second.G), Blend(first.B, second.B));
    }

    internal static double ContrastRatio(Color first, Color second)
    {
        var a = RelativeLuminance(first);
        var b = RelativeLuminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static Color EnsureContrast(Color foreground, Color background, double minimum, Color toward)
    {
        for (double amount = .04; ContrastRatio(foreground, background) < minimum && amount <= 1; amount += .04)
            foreground = Mix(foreground, toward, amount);
        return foreground;
    }

    private static Color EnsureSurfaceContrast(Color foreground, Color[] backgrounds, double minimum, Color toward)
    {
        foreach (var background in backgrounds)
            foreground = EnsureContrast(foreground, background, minimum, toward);
        return foreground;
    }

    internal static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            double c = value / 255d;
            return c <= .04045 ? c / 12.92 : Math.Pow((c + .055) / 1.055, 2.4);
        }
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }
}
