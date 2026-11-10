using System.Windows;
using Microsoft.Win32;

namespace EmulatorWorkspace.Desktop.Services;

public enum ThemeMode
{
    System,
    Dark,
    Light,
}

/// <summary>
/// Swaps the brush dictionary at runtime. High contrast always wins over the
/// user preference and uses system colors so custom accents never reduce
/// legibility.
/// </summary>
public static class ThemeManager
{
    private const string ThemesPrefix = "Themes/";

    public static ThemeMode Mode { get; private set; } = ThemeMode.System;

    public static bool IsDark { get; private set; } = true;

    public static event Action? Changed;

    public static void Initialize()
    {
        Apply(ThemeMode.System);
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
