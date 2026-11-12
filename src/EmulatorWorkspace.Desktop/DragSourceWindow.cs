using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EmulatorWorkspace.Desktop;

/// <summary>
/// A small window that offers one file for a real drag-and-drop. It is used
/// by the click-through harness to drop an APK onto the desktop window with
/// the mouse, exactly as an operator would drag a file from Explorer.
/// </summary>
public partial class DragSourceWindow : Window
{
    private readonly string _path;
    private readonly Button _handle;

    public DragSourceWindow(string path)
    {
        _path = path;
        Title = "Drag source";
        Width = 420;
        Height = 140;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 40;
        Top = 40;
        Background = (Brush)FindResource("PanelBg");

        var label = new TextBlock
        {
            Text = Path.GetFileName(path),
            Margin = new Thickness(12),
            FontWeight = FontWeights.SemiBold,
        };

        _handle = new Button
        {
            Content = label,
            Background = (Brush)FindResource("LogBg"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Margin = new Thickness(12),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
        };
        _handle.MouseLeftButtonDown += OnMouseLeftButtonDown;
        AutomationProperties.SetName(_handle, "Drag source handle");

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(_handle);
        Content = grid;

        Loaded += (_, _) =>
        {
            // The harness waits for this file before it starts the drag.
            var ready = Path.Combine(
                Environment.GetEnvironmentVariable("EMULATOR_WORKSPACE_HOME") ?? ".",
                "drag-ready.txt");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ready)!);
                File.WriteAllText(ready, path);
            }
            catch (Exception)
            {
            }
        };
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var data = new DataObject(DataFormats.FileDrop, new[] { _path });
        DragDrop.DoDragDrop(_handle, data, DragDropEffects.Copy);
        e.Handled = true;
    }
}
