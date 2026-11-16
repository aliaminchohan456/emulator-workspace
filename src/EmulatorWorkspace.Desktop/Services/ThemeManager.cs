using System.Windows;
using Microsoft.Win32;

namespace EmulatorWorkspace.Desktop.Services;

/// <summary>
/// Swaps the brush dictionary at runtime. High contrast always wins over the
/// user preference and uses system colors so custom accents never reduce
/// legibility.
/// </summary>
public static class ThemeManager
{
    private const string ThemesPrefix = "Themes/";

    private static bool _initialized;

    public static ThemeMode Mode { get; private set; } = ThemeMode.System;

    public static bool IsDark { get; private set; } = true;

    public static event Action? Changed;

    /// <summary>
    /// Applies the theme and watches for High Contrast changes.
    /// Idempotent so secondary windows (the Agent Sessions probe path opens
    /// one without a MainWindow) can call it again without resetting an
    /// explicit user theme choice or stacking system-state handlers.
    ///
    /// UX-17: <paramref name="initial"/> is the choice read from
    /// settings.json before the first window exists, so the first frame is
    /// already in the saved theme (no dark-to-light flash). The default stays
    /// <see cref="ThemeMode.System"/>, which is what a missing settings file
    /// resolves to.
    /// </summary>
    public static void Initialize(ThemeMode initial = ThemeMode.System)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        Apply(initial);
        SystemParameters.StaticPropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SystemParameters.HighContrast)
                or nameof(SystemParameters.WindowGlassColor))
            {
                Apply(Mode);
            }
        };
    }

    public static void Apply(ThemeMode mode)
    {
        Mode = mode;
        var highContrast = SystemParameters.HighContrast;
        var dark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => !SystemUsesLightTheme(),
        };

        IsDark = dark;

        var source = highContrast
            ? "Themes/HighContrast.xaml"
            : dark ? "Themes/Dark.xaml" : "Themes/Light.xaml";

        var resources = Application.Current.Resources;
        var existing = resources.MergedDictionaries
            .FirstOrDefault(d => d.Source?.OriginalString.Contains(ThemesPrefix, StringComparison.OrdinalIgnoreCase) == true
                                 && !d.Source.OriginalString.Contains("Common", StringComparison.OrdinalIgnoreCase));

        var replacement = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
        if (existing is not null)
        {
            var index = resources.MergedDictionaries.IndexOf(existing);
            resources.MergedDictionaries.RemoveAt(index);
            resources.MergedDictionaries.Insert(index, replacement);
        }
        else
        {
            resources.MergedDictionaries.Add(replacement);
        }

        Changed?.Invoke();
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int intValue && intValue != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
