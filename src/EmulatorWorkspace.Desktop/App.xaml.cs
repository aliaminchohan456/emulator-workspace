using System.IO;
using System.Windows;
using System.Windows.Automation;

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

        // UX-17: the saved theme is applied before the first window is built, so
        // the first frame is already in the operator's theme instead of flashing
        // the dark default and then swapping dictionaries. A missing, corrupt or
        // unknown file is one desktop.log line and the shipped default (follow
        // Windows); it can never stop the app from starting.
        var themePath = Services.ThemeSettingsStore.DefaultPath;
        var themeMode = Services.ThemeSettingsStore.Load(themePath, Services.DesktopLog.Write);
        Services.ThemeManager.Initialize(themeMode);
        // UX-16 item B: every exit path leaves one line that says why. The
        // notes are taken while the app still runs; OnExit writes the line.
        SessionEnding += (_, args) => Services.ExitBreadcrumb.Note(
            args.ReasonSessionEnding == ReasonSessionEnding.Logoff
                ? "session ending: logoff"
                : "session ending: system shutdown",
            shutdown: true);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception;
            Services.ExitBreadcrumb.Note(
                exception is null ? "unhandled appdomain exception" : "unhandled appdomain exception " + Services.ExitBreadcrumb.Describe(exception),
                fault: true);
            Services.ExitBreadcrumb.Write(-1);
        };
        TaskScheduler.UnobservedTaskException += (_, args) => Services.ExitBreadcrumb.Note(
            "unobserved task exception " + Services.ExitBreadcrumb.Describe(args.Exception),
            fault: true);
        DispatcherUnhandledException += (_, args) =>
        {
            Services.DesktopLog.Write("UNHANDLED " + args.Exception);
            Services.ExitBreadcrumb.Note(
                "unhandled dispatcher exception " + Services.ExitBreadcrumb.Describe(args.Exception),
                fault: true);
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

            // UX-13: the separate window is gone, so the probe opens the page in
            // the shell and reads the same collections the window published.
            var probeWindow = new MainWindow();
            MainWindow = probeWindow;
            probeWindow.Loaded += async (_, _) =>
            {
                probeWindow.ShowAgentsPage();
                await Task.Delay(TimeSpan.FromSeconds(5));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await File.WriteAllTextAsync(output, probeWindow.AgentSessionsProbeSummary());
                Services.ExitBreadcrumb.Note("explicit shutdown: agent sessions probe complete", shutdown: true);
                Shutdown(0);
            };
            probeWindow.Show();
            return;
        }

        if (e.Args.Contains("--drag-source", StringComparer.OrdinalIgnoreCase))
        {
            // The drag source lets an external harness perform a real file
            // drop on the desktop: the window starts a WPF drag-and-drop of
            // the file as soon as the pointer presses it.
            var apk = "";
            for (var index = 0; index < e.Args.Length - 1; index++)
            {
                if (e.Args[index].Equals("--drag-source", StringComparison.OrdinalIgnoreCase))
                {
                    apk = Path.GetFullPath(e.Args[index + 1]);
                }
            }

            var source = new DragSourceWindow(apk);
            MainWindow = source;
            source.Show();
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
        var halfCapture = false;
        string? probeOut = null;
        for (var index = 0; index < e.Args.Length; index++)
        {
            if (e.Args[index].Equals("--latency-probe", StringComparison.OrdinalIgnoreCase)
                && index + 1 < e.Args.Length)
            {
                int.TryParse(e.Args[index + 1], out probeCount);
            }
            else if (e.Args[index].Equals("--probe-half-capture", StringComparison.OrdinalIgnoreCase))
            {
                halfCapture = true;
            }
            else if (e.Args[index].Equals("--out", StringComparison.OrdinalIgnoreCase)
                     && index + 1 < e.Args.Length)
            {
                probeOut = Path.GetFullPath(e.Args[index + 1]);
            }
        }

        var window = new MainWindow(probeCount > 0 && halfCapture);
        MainWindow = window;

        var dropFile = "";
        for (var index = 0; index < e.Args.Length - 1; index++)
        {
            if (e.Args[index].Equals("--drop-file", StringComparison.OrdinalIgnoreCase))
            {
                dropFile = Path.GetFullPath(e.Args[index + 1]);
            }
        }

        // UX-03 evidence hook: renders the APK drag affordance through the same
        // view-model call the drag handlers use, so the surface can be captured
        // on a host where an always-on-top window intercepts real shell drags.
        // It never installs anything and never fakes a drop.
        var dropPreview = "";
        for (var index = 0; index < e.Args.Length - 1; index++)
        {
            if (e.Args[index].Equals("--drop-preview", StringComparison.OrdinalIgnoreCase))
            {
                dropPreview = Path.GetFullPath(e.Args[index + 1]);
            }
        }

        if (dropPreview.Length > 0)
        {
            var previewTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(2),
            };
            previewTimer.Tick += (_, _) => window.ViewModel.ShowDropTarget(new[] { dropPreview });
            window.Loaded += (_, _) =>
            {
                window.ViewModel.ShowDropTarget(new[] { dropPreview });
                previewTimer.Start();
            };
        }

        if (dropFile.Length > 0)
        {
            // The file is dropped on the window once a guest is running and
            // selected, exactly like a drop from the shell onto a live guest.
            var delivered = false;
            var timer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background);
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.Tick += (_, _) =>
            {
                if (delivered)
                {
                    timer.Stop();
                    return;
                }

                var device = window.ViewModel.SelectedDevice;
                if (device is null || !device.IsRunning || !window.ViewModel.HasFrame)
                {
                    return;
                }

                delivered = true;
                timer.Stop();
                _ = window.DeliverFileDropAsync(new[] { dropFile });
                Services.DesktopLog.Write("dropped " + dropFile);
            };
            timer.Start();
        }

        if (probeCount > 0)
        {
            window.Loaded += async (_, _) =>
            {
                var output = probeOut ?? Path.Combine(
                    Environment.GetEnvironmentVariable("EMULATOR_WORKSPACE_HOME") ?? ".",
                    "latency-probe.json");
                var code = await window.RunLatencyProbeAsync(probeCount, output);
                Services.ExitBreadcrumb.Note("explicit shutdown: latency probe complete", shutdown: true);
                Shutdown(code);
            };
        }

        // UX-04 evidence hook: opens the device library through the same handler
        // the command-bar button and the palette entry use, and optionally one
        // of its dialogs for capture. It creates, clones and deletes nothing by
        // itself.
        var libraryDialog = "";
        for (var index = 0; index < e.Args.Length - 1; index++)
        {
            if (e.Args[index].Equals("--library-open", StringComparison.OrdinalIgnoreCase))
            {
                libraryDialog = e.Args[index + 1];
            }
        }

        if (e.Args.Contains("--open-library", StringComparer.OrdinalIgnoreCase) || libraryDialog.Length > 0)
        {
            window.Loaded += async (_, _) =>
            {
                var library = window.OpenDeviceLibrary();
                if (libraryDialog.Length > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    await library.RunEvidenceHookAsync(libraryDialog);
                }
            };
        }

        // UX-13 evidence hook: opens the Agents page through the same handler the
        // rail item and Ctrl+4 run, so a capture needs no synthetic click.
        if (e.Args.Contains("--open-agents", StringComparer.OrdinalIgnoreCase))
        {
            window.Loaded += (_, _) => window.ShowAgentsPage();
        }

        // UX-13 evidence hook: selects a real managed guest by name once the
        // supervisor's list has loaded, so a capture can show the page's device
        // context without a synthetic library click. It never creates or starts
        // anything.
        var selectDevice = "";
        for (var index = 0; index < e.Args.Length - 1; index++)
        {
            if (e.Args[index].Equals("--select-device", StringComparison.OrdinalIgnoreCase))
            {
                selectDevice = e.Args[index + 1];
            }
        }

        if (selectDevice.Length > 0)
        {
            window.Loaded += async (_, _) =>
            {
                for (var attempt = 0; attempt < 60; attempt++)
                {
                    if (window.ViewModel.SelectDevice(selectDevice))
                    {
                        return;
                    }

                    await Task.Delay(500);
                }
            };
        }

        window.Show();
    }

    /// <summary>
    /// UX-16 item B: the last point this app controls before the process ends.
    /// The reason was noted by whichever path is ending the session; the line
    /// is written and flushed here, once.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Services.ExitBreadcrumb.Write(e.ApplicationExitCode);
        base.OnExit(e);
    }
}
