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
            // Dark theme palette
            SetOrUpdateBrush("BrushBgDark", Color.FromRgb(18, 19, 22));          // #121316
            SetOrUpdateBrush("BrushBgApp", Color.FromRgb(18, 19, 22));           // #121316
            SetOrUpdateBrush("BrushBgCard", Color.FromRgb(26, 28, 35));          // #1A1C23
            SetOrUpdateBrush("BrushBgCardHover", Color.FromRgb(35, 38, 49));     // #232631
            SetOrUpdateBrush("BrushBorder", Color.FromRgb(45, 50, 62));          // #2D323E
            SetOrUpdateBrush("BrushPrimary", Color.FromRgb(56, 72, 99));         // #384863
            SetOrUpdateBrush("BrushPrimaryHover", Color.FromRgb(71, 90, 123));   // #475A7B
            SetOrUpdateBrush("BrushSuccess", Color.FromRgb(52, 211, 153));       // #34D399
            SetOrUpdateBrush("BrushWarning", Color.FromRgb(245, 158, 11));       // #F59E0B
            SetOrUpdateBrush("BrushDanger", Color.FromRgb(239, 68, 68));         // #EF4444
            SetOrUpdateBrush("BrushInfo", Color.FromRgb(56, 189, 248));          // #38BDF8
            SetOrUpdateBrush("BrushTextPrimary", Color.FromRgb(241, 243, 247));  // #F1F3F7
            SetOrUpdateBrush("BrushTextSecondary", Color.FromRgb(158, 165, 180));// #9EA5B4
            SetOrUpdateBrush("BrushInputBg", Color.FromRgb(21, 23, 29));         // #15171D
            SetOrUpdateBrush("BrushRowBg", Color.FromRgb(26, 28, 35));           // #1A1C23
            SetOrUpdateBrush("BrushRowAltBg", Color.FromRgb(22, 24, 30));        // #16181E
            SetOrUpdateBrush("BrushRowHover", Color.FromRgb(35, 39, 51));        // #232733
            SetOrUpdateBrush("BrushSelectedRow", Color.FromRgb(39, 51, 71));     // #273347
            SetOrUpdateBrush("BrushHeaderBg", Color.FromRgb(21, 23, 29));        // #15171D
            SetOrUpdateBrush("BrushScrollThumb", Color.FromArgb(48, 255, 255, 255));
            SetOrUpdateBrush("BrushScrollThumbHover", Color.FromArgb(85, 255, 255, 255));
            SetOrUpdateBrush("BrushScrollThumbDrag", Color.FromArgb(128, 255, 255, 255));
        }
        else
        {
            // Light theme palette
            SetOrUpdateBrush("BrushBgDark", Color.FromRgb(248, 250, 252));       // #F8FAFC
            SetOrUpdateBrush("BrushBgApp", Color.FromRgb(248, 250, 252));        // #F8FAFC
            SetOrUpdateBrush("BrushBgCard", Color.FromRgb(255, 255, 255));       // #FFFFFF
            SetOrUpdateBrush("BrushBgCardHover", Color.FromRgb(241, 245, 249));  // #F1F5F9
            SetOrUpdateBrush("BrushBorder", Color.FromRgb(226, 232, 240));       // #E2E8F0
            SetOrUpdateBrush("BrushPrimary", Color.FromRgb(71, 85, 105));        // #475569
            SetOrUpdateBrush("BrushPrimaryHover", Color.FromRgb(51, 65, 85));    // #334155
            SetOrUpdateBrush("BrushSuccess", Color.FromRgb(87, 117, 96));        // #577560
            SetOrUpdateBrush("BrushWarning", Color.FromRgb(217, 119, 6));        // #D97706
            SetOrUpdateBrush("BrushDanger", Color.FromRgb(220, 38, 38));         // #DC2626
            SetOrUpdateBrush("BrushInfo", Color.FromRgb(71, 85, 105));          // #475569
            SetOrUpdateBrush("BrushTextPrimary", Color.FromRgb(15, 23, 42));     // #0F172A
            SetOrUpdateBrush("BrushTextSecondary", Color.FromRgb(100, 116, 139)); // #64748B
            SetOrUpdateBrush("BrushInputBg", Color.FromRgb(255, 255, 255));      // #FFFFFF
            SetOrUpdateBrush("BrushRowBg", Color.FromRgb(255, 255, 255));        // #FFFFFF
            SetOrUpdateBrush("BrushRowAltBg", Color.FromRgb(248, 250, 252));     // #F8FAFC
            SetOrUpdateBrush("BrushRowHover", Color.FromArgb(14, 0, 0, 0));
            SetOrUpdateBrush("BrushSelectedRow", Color.FromArgb(40, 71, 85, 105));
            SetOrUpdateBrush("BrushHeaderBg", Color.FromRgb(241, 245, 249));     // #F1F5F9
            SetOrUpdateBrush("BrushScrollThumb", Color.FromArgb(55, 0, 0, 0));
            SetOrUpdateBrush("BrushScrollThumbHover", Color.FromArgb(95, 0, 0, 0));
            SetOrUpdateBrush("BrushScrollThumbDrag", Color.FromArgb(140, 0, 0, 0));
        }
    }
}
