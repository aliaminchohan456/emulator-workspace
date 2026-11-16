namespace EmulatorWorkspace.Desktop.Services;

/// <summary>
/// The operator's theme choice. <see cref="System"/> follows the Windows app
/// theme on every launch (the shipped default); Dark and Light are explicit
/// picks. High Contrast is not a choice: it is a Windows policy that overrides
/// any of the three, which is why <see cref="ThemeSettingsStore"/> resolves a
/// stored "HighContrast" back to <see cref="System"/> instead of pinning it.
///
/// Deliberately free of WPF types so the settings store and its tests compile
/// without the desktop assembly.
/// </summary>
public enum ThemeMode
{
    System,
    Dark,
    Light,
}
