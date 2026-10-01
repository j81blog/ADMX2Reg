using System.Windows;
using Wpf.Ui.Appearance;

namespace ADMX2Reg.Services;

public static class ThemeService {
    public static void ApplyInitial(ThemeChoice mode) {
        switch (mode) {
            case ThemeChoice.Light:
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
                break;
            case ThemeChoice.Dark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme();
                break;
        }
    }

    /// <summary>Applies the theme live. System mode follows Windows through the watcher.</summary>
    public static void Apply(ThemeChoice mode, Window main) {
        if (mode == ThemeChoice.System) {
            ApplicationThemeManager.ApplySystemTheme();
            SystemThemeWatcher.Watch(main);
        } else {
            SystemThemeWatcher.UnWatch(main);
            ApplicationThemeManager.Apply(mode == ThemeChoice.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
        }
    }
}
