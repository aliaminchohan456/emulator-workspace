using System.IO;
using System.Windows;

namespace EmulatorWorkspace.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The workspace root is resolved before anything is logged, so the
        // desktop log always lands in the workspace it belongs to.
        for (var index = 0; index < e.Args.Length - 1; index++)
        {
            if (e.Args[index].Equals("--root", StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable("EMULATOR_WORKSPACE_HOME", Path.GetFullPath(e.Args[index + 1]));
            }
        }

        Services.DesktopLog.Write("startup");
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

        if (e.Args.Contains("--agent-sessions-probe", StringComparer.OrdinalIgnoreCase))
        {
            var output = Path.Combine(
                Environment.GetEnvironmentVariable("EMULATOR_WORKSPACE_HOME") ?? ".",
                "agent-sessions-probe.json");
            for (var index = 0; index < e.Args.Length - 1; index++)
            {
                if (e.Args[index].Equals("--out", StringComparison.OrdinalIgnoreCase))
                {
                    output = Path.GetFullPath(e.Args[index + 1]);
                }
            }

            var probeWindow = new AgentSessionsWindow();
            MainWindow = probeWindow;
            probeWindow.Loaded += async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await File.WriteAllTextAsync(output, probeWindow.ProbeSummary());
                Shutdown(0);
            };
            probeWindow.Show();
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

        var probeCount = 0;
        string? probeOut = null;
        for (var index = 0; index < e.Args.Length; index++)
        {
            if (e.Args[index].Equals("--latency-probe", StringComparison.OrdinalIgnoreCase)
                && index + 1 < e.Args.Length)
            {
                int.TryParse(e.Args[index + 1], out probeCount);
            }
            else if (e.Args[index].Equals("--out", StringComparison.OrdinalIgnoreCase)
                     && index + 1 < e.Args.Length)
            {
                probeOut = Path.GetFullPath(e.Args[index + 1]);
            }
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        if (probeCount > 0)
        {
            window.Loaded += async (_, _) =>
            {
                var output = probeOut ?? Path.Combine(
                    Environment.GetEnvironmentVariable("EMULATOR_WORKSPACE_HOME") ?? ".",
                    "latency-probe.json");
                var code = await window.RunLatencyProbeAsync(probeCount, output);
                Shutdown(code);
            };
        }
    }
}
