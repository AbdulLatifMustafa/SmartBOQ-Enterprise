using System.Windows;
using System.Windows.Media;

namespace SmartBOQ.App.Services;

/// <summary>
/// Dynamic theme coordinator providing instant switching between high-contrast
/// Dark Mode and crisp Light Mode with zero flickering and responsive palettes.
/// </summary>
public static class ThemeManager
{
    public static bool IsDarkMode { get; private set; } = true;

    public static void ApplyTheme(bool darkMode)
    {
        IsDarkMode = darkMode;
        var res = System.Windows.Application.Current?.Resources;
        if (res == null) return;

        void SetOrUpdateBrush(string key, Color color)
        {
            if (res[key] is SolidColorBrush brush && !brush.IsFrozen)
            {
                brush.Color = color;
            }
            else
            {
                res[key] = new SolidColorBrush(color);
            }
        }

        if (darkMode)
        {
            // Dark Mode - Deep Slate, Crystal White Text, Unified Enterprise Blue
            SetOrUpdateBrush("BrushBgDark", Color.FromRgb(11, 17, 32));          // #0B1120
            SetOrUpdateBrush("BrushBgApp", Color.FromRgb(11, 17, 32));           // #0B1120
            SetOrUpdateBrush("BrushBgCard", Color.FromRgb(22, 31, 48));          // #161F30
            SetOrUpdateBrush("BrushBgCardHover", Color.FromRgb(34, 47, 70));     // #222F46
            SetOrUpdateBrush("BrushBorder", Color.FromRgb(42, 56, 79));          // #2A384F
            SetOrUpdateBrush("BrushPrimary", Color.FromRgb(37, 99, 235));        // #2563EB - Enterprise Blue
            SetOrUpdateBrush("BrushPrimaryHover", Color.FromRgb(29, 78, 216));   // #1D4ED8 - Blue 700
            SetOrUpdateBrush("BrushSuccess", Color.FromRgb(16, 185, 129));       // #10B981
            SetOrUpdateBrush("BrushWarning", Color.FromRgb(245, 158, 11));       // #F59E0B
            SetOrUpdateBrush("BrushDanger", Color.FromRgb(239, 68, 68));         // #EF4444
            SetOrUpdateBrush("BrushInfo", Color.FromRgb(59, 130, 246));          // #3B82F6
            SetOrUpdateBrush("BrushTextPrimary", Color.FromRgb(255, 255, 255));  // Pure crisp white
            SetOrUpdateBrush("BrushTextSecondary", Color.FromRgb(148, 163, 184)); // Muted slate #94A3B8
            SetOrUpdateBrush("BrushInputBg", Color.FromRgb(14, 22, 38));         // #0E1626
            SetOrUpdateBrush("BrushRowBg", Color.FromRgb(22, 31, 48));           // #161F30
            SetOrUpdateBrush("BrushRowAltBg", Color.FromRgb(17, 25, 39));        // #111927
            SetOrUpdateBrush("BrushRowHover", Color.FromArgb(18, 255, 255, 255));        // ~7% Frosted Glass Hover (Zero Blue)
            SetOrUpdateBrush("BrushSelectedRow", Color.FromArgb(34, 255, 255, 255));     // ~13.5% Neutral Glass Selection (Zero Blue)
            SetOrUpdateBrush("BrushHeaderBg", Color.FromRgb(14, 22, 38));        // #0E1626
            SetOrUpdateBrush("BrushScrollThumb", Color.FromArgb(48, 255, 255, 255));      // ~19% White
            SetOrUpdateBrush("BrushScrollThumbHover", Color.FromArgb(85, 255, 255, 255)); // ~33% White
            SetOrUpdateBrush("BrushScrollThumbDrag", Color.FromArgb(128, 255, 255, 255)); // ~50% White
        }
        else
        {
            // Light Mode - Fresh Modern Clean Slate, Deep Charcoal Text, Unified Enterprise Blue
            SetOrUpdateBrush("BrushBgDark", Color.FromRgb(248, 250, 252));       // #F8FAFC
            SetOrUpdateBrush("BrushBgApp", Color.FromRgb(248, 250, 252));        // #F8FAFC
            SetOrUpdateBrush("BrushBgCard", Color.FromRgb(255, 255, 255));       // #FFFFFF
            SetOrUpdateBrush("BrushBgCardHover", Color.FromRgb(241, 245, 249));  // #F1F5F9
            SetOrUpdateBrush("BrushBorder", Color.FromRgb(226, 232, 240));       // #E2E8F0
            SetOrUpdateBrush("BrushPrimary", Color.FromRgb(37, 99, 235));        // #2563EB - Enterprise Blue
            SetOrUpdateBrush("BrushPrimaryHover", Color.FromRgb(29, 78, 216));   // #1D4ED8 - Blue 700
            SetOrUpdateBrush("BrushSuccess", Color.FromRgb(16, 185, 129));       // #10B981
            SetOrUpdateBrush("BrushWarning", Color.FromRgb(217, 119, 6));        // #D97706
            SetOrUpdateBrush("BrushDanger", Color.FromRgb(220, 38, 38));         // #DC2626
            SetOrUpdateBrush("BrushInfo", Color.FromRgb(37, 99, 235));           // #2563EB
            SetOrUpdateBrush("BrushTextPrimary", Color.FromRgb(15, 23, 42));     // #0F172A
            SetOrUpdateBrush("BrushTextSecondary", Color.FromRgb(100, 116, 139));  // #64748B
            SetOrUpdateBrush("BrushInputBg", Color.FromRgb(255, 255, 255));      // #FFFFFF
            SetOrUpdateBrush("BrushRowBg", Color.FromRgb(255, 255, 255));        // #FFFFFF
            SetOrUpdateBrush("BrushRowAltBg", Color.FromRgb(248, 250, 252));     // #F8FAFC
            SetOrUpdateBrush("BrushRowHover", Color.FromArgb(14, 0, 0, 0));              // ~5.5% Subtle Glass Hover (Zero Blue)
            SetOrUpdateBrush("BrushSelectedRow", Color.FromArgb(24, 0, 0, 0));           // ~9.5% Soft Neutral Glass Selection (Zero Blue)
            SetOrUpdateBrush("BrushHeaderBg", Color.FromRgb(241, 245, 249));     // #F1F5F9
            SetOrUpdateBrush("BrushScrollThumb", Color.FromArgb(55, 0, 0, 0));            // ~22% Black
            SetOrUpdateBrush("BrushScrollThumbHover", Color.FromArgb(95, 0, 0, 0));       // ~37% Black
            SetOrUpdateBrush("BrushScrollThumbDrag", Color.FromArgb(140, 0, 0, 0));       // ~55% Black
        }
    }
}
