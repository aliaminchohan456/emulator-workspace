using System.IO;
using System.Windows;

namespace EmulatorWorkspace.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Services.DesktopLog.Write("startup");

        for (var index = 0; index < e.Args.Length - 1; index++)
        {
            if (e.Args[index].Equals("--root", StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("EMULATOR_WORKSPACE_HOME", Path.GetFullPath(e.Args[index + 1]));
            }
        }
        DispatcherUnhandledException += (_, args) =>
        {
            Services.DesktopLog.Write("UNHANDLED " + args.Exception);
            MessageBox.Show(args.Exception.Message, "Emulator Workspace", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        if (e.Args.Contains("--vm", StringComparer.OrdinalIgnoreCase))
        {
            var vmProbe = new ViewModels.MainViewModel();
            var probe = new Window
            {
                Title = "vm",
                Width = 320,
                Height = 200,
                Content = new System.Windows.Controls.TextBlock { Text = vmProbe.WorkspaceRoot },
            };
            MainWindow = probe;
            probe.Show();
            return;
        }

        if (e.Args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
        {
            var smoke = new Window
            {
                Title = "smoke",
                Width = 320,
                Height = 200,
                Content = new System.Windows.Controls.TextBlock { Text = "ok" },
            };
            MainWindow = smoke;
            smoke.Show();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
