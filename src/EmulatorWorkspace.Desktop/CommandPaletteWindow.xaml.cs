using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EmulatorWorkspace.Desktop;

/// <summary>
/// Keyboard command palette (Ctrl+K). It lists the operator actions of the
/// desktop and runs them through the same commands the toolbar uses, so the
/// palette cannot reach anything the UI does not already expose.
/// </summary>
public partial class CommandPaletteWindow : Window
{
    private readonly ViewModels.MainViewModel _viewModel;
    private readonly List<ViewModels.PaletteEntryViewModel> _all;

    public CommandPaletteWindow(ViewModels.MainViewModel viewModel)
    {
        InitializeComponent();
        Services.ThemeManager.Initialize();
        Services.Chrome.Initialize();
        Services.Chrome.Apply(this);
        _viewModel = viewModel;
        _all = viewModel.Palette.ToList();
        Results.ItemsSource = _all;
        Filter.TextChanged += (_, _) => Apply();
        Filter.KeyDown += OnFilterKeyDown;
        Results.MouseDoubleClick += (_, _) => RunSelected();
        Loaded += (_, _) => Filter.Focus();
    }

    private void Apply()
    {
        var text = Filter.Text.Trim();
        Results.ItemsSource = text.Length == 0
            ? _all
            : _all.Where(entry =>
                entry.Label.Contains(text, StringComparison.OrdinalIgnoreCase)
                || entry.Hint.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RunSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void RunSelected()
    {
        if (Results.SelectedItem is ViewModels.PaletteEntryViewModel entry)
        {
            entry.Invoke();
            Close();
        }
    }
}
