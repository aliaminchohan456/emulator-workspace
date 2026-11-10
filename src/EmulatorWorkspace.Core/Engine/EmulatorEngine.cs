using System.Diagnostics;
using System.Globalization;
using System.Text;
using EmulatorWorkspace.Core.Instances;
using EmulatorWorkspace.Core.Workspace;

namespace EmulatorWorkspace.Core.Engine;

/// <summary>
/// Owns engine process lifetime. The engine is started by a small host process
/// (<c>wsctl engine-host</c>) that keeps the child's stdio drained into a log
/// file; this avoids both a deadlock on a full pipe and an inherited console
/// that would otherwise die with the launching shell.
/// </summary>
public sealed class EmulatorEngine
{
    private readonly SdkLocator _sdk;
    private readonly WorkspacePaths _paths;
    private readonly InstanceStore _store;
    private readonly AdbClient _adb;

    public EmulatorEngine(SdkLocator sdk, WorkspacePaths paths, InstanceStore store)
    {
        _sdk = sdk;
        _paths = paths;
        _store = store;
        _adb = new AdbClient(sdk.AdbExe);
    }

    public AdbClient Adb => _adb;

    public async Task<int> RunHostAsync(string instanceName, CancellationToken cancellationToken = default)
    {
        var record = _store.Get(instanceName);
        var logPath = _paths.InstanceLogPath(instanceName);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var args = EngineArguments.Build(
            record,
            headless: record.Headless,
            gpuMode: record.GpuMode ?? EngineArguments.DefaultGpuMode,
            coldBoot: true);

        var startInfo = new ProcessStartInfo(_sdk.EmulatorExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = _sdk.SdkRoot,
        };
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["ANDROID_AVD_HOME"] = record.AvdHome;
        startInfo.Environment["ANDROID_SDK_ROOT"] = _sdk.SdkRoot;

        using var logWriter = new StreamWriter(new FileStream(
            logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true,
        };

        await logWriter.WriteLineAsync(
            $"=== {DateTimeOffset.UtcNow:O} launching {_sdk.EmulatorExe} ===").ConfigureAwait(false);
        await logWriter.WriteLineAsync(StartInfoRenderer.Render(_sdk.EmulatorExe, args)).ConfigureAwait(false);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the Android Emulator.");
        }

        var startTime = ProcessIdentity.CaptureStartTime(process.Id);
        _store.Update(instanceName, r =>
        {
            r.State = InstanceState.Starting;
            r.EnginePid = process.Id;
            r.EngineStartTimeUtc = startTime;
            r.EnginePath = _sdk.EmulatorExe;
            r.EngineArguments = StartInfoRenderer.Render(_sdk.EmulatorExe, args);
            r.EngineVersion = record.EngineVersion;
            r.LastError = null;
            r.LastExitCode = null;
        });

        var pumpOut = PumpAsync(process.StandardOutput, logWriter, "stdout");
        var pumpErr = PumpAsync(process.StandardError, logWriter, "stderr");

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Host cancellation must not orphan the engine: stop it explicitly.
            await StopAsync(_store.Get(instanceName), force: false).ConfigureAwait(false);
        }

        await Task.WhenAll(pumpOut, pumpErr).ConfigureAwait(false);

        _store.Update(instanceName, r =>
        {
            r.LastExitCode = process.ExitCode;
            r.State = process.ExitCode == 0 ? InstanceState.Stopped : InstanceState.Failed;
            if (process.ExitCode != 0)
            {
                r.LastError = $"Engine exited with code {process.ExitCode}. See {logPath}.";
            }

            r.EnginePid = null;
            r.EngineStartTimeUtc = null;
            r.HostPid = null;
            r.HostStartTimeUtc = null;
        });

        return process.ExitCode;
    }

    public bool IsEngineAlive(InstanceRecord record) =>
        ProcessIdentity.IsSameProcess(record.EnginePid, record.EngineStartTimeUtc);

    public bool IsHostAlive(InstanceRecord record) =>
        ProcessIdentity.IsSameProcess(record.HostPid, record.HostStartTimeUtc);

    /// <summary>
    /// Terminates an engine host whose engine has already exited. This is the
    /// recovery path for a crashed engine, an interrupted stop, or a host that
    /// outlived a rebuilt binary.
    /// </summary>
    public void ReconcileHost(InstanceRecord record)
    {
        if (IsEngineAlive(record) || !IsHostAlive(record))
        {
            return;
        }

        try
        {
            using var host = Process.GetProcessById(record.HostPid!.Value);
            host.Kill(entireProcessTree: false);
            host.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        _store.Update(record.Name, r =>
        {
            r.HostPid = null;
            r.HostStartTimeUtc = null;
            if (!IsEngineAlive(r))
            {
                r.State = InstanceState.Stopped;
                r.EnginePid = null;
                r.EngineStartTimeUtc = null;
            }
        });
    }

    public async Task<EngineStatus> GetStatusAsync(
        InstanceRecord record,
        CancellationToken cancellationToken = default)
    {
        var alive = IsEngineAlive(record);
        string? adbState = null;
        string? bootCompleted = null;
        string? fingerprint = null;
        string? abi = null;
        string? sdkLevel = null;

        if (alive)
        {
            try
            {
                adbState = await _adb.GetStateAsync(record.AdbSerial, cancellationToken).ConfigureAwait(false);
                if (adbState.Contains("device", StringComparison.Ordinal))
                {
                    bootCompleted = await _adb.GetPropAsync(record.AdbSerial, "sys.boot_completed", cancellationToken)
                        .ConfigureAwait(false);
                    fingerprint = await _adb.GetPropAsync(record.AdbSerial, "ro.build.fingerprint", cancellationToken)
                        .ConfigureAwait(false);
                    abi = await _adb.GetPropAsync(record.AdbSerial, "ro.product.cpu.abi", cancellationToken)
                        .ConfigureAwait(false);
                    sdkLevel = await _adb.GetPropAsync(record.AdbSerial, "ro.build.version.sdk", cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
            {
                adbState = "unavailable: " + ex.Message;
            }
        }

        return new EngineStatus(
            record.Name,
            record.AdbSerial,
            record.ConsolePort,
            record.GrpcPort,
            record.State,
            alive,
            record.EnginePid,
            adbState,
            bootCompleted,
            fingerprint,
            abi,
            sdkLevel,
            record.LastError);
    }

    public async Task StopAsync(InstanceRecord record, bool force, CancellationToken cancellationToken = default)
    {
        if (!IsEngineAlive(record))
        {
            _store.Update(record.Name, r =>
            {
                r.State = InstanceState.Stopped;
                r.EnginePid = null;
                r.EngineStartTimeUtc = null;
            });
            return;
        }

        _store.Update(record.Name, r => r.State = InstanceState.Stopping);

        try
        {
            if (!force)
            {
                await _adb.EmulatorConsoleAsync(record.AdbSerial, new[] { "kill" }, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            // Fall through to the identity-checked kill below.
            _store.Update(record.Name, r => r.LastError = "Graceful stop failed: " + ex.Message);
        }

        var deadline = DateTimeOffset.UtcNow.AddSeconds(force ? 5 : 30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!IsEngineAlive(record))
            {
                break;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        if (IsEngineAlive(record) && force)
        {
            using var process = Process.GetProcessById(record.EnginePid!.Value);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        _store.Update(record.Name, r =>
        {
            if (!IsEngineAlive(r))
            {
                r.State = InstanceState.Stopped;
                r.EnginePid = null;
                r.EngineStartTimeUtc = null;
            }
        });

        // The engine host exits on its own once the engine does, but a failed or
        // interrupted run can leave it behind. Reap only a verified identity.
        ReconcileHost(_store.Get(record.Name));
    }

    public Task<string> SaveSnapshotAsync(InstanceRecord record, string tag, CancellationToken cancellationToken = default) =>
        _adb.EmulatorConsoleAsync(record.AdbSerial, new[] { "avd", "snapshot", "save", tag }, cancellationToken);

    public Task<string> LoadSnapshotAsync(InstanceRecord record, string tag, CancellationToken cancellationToken = default) =>
        _adb.EmulatorConsoleAsync(record.AdbSerial, new[] { "avd", "snapshot", "load", tag }, cancellationToken);

    private static async Task PumpAsync(StreamReader reader, TextWriter writer, string channel)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            try
            {
                await writer.WriteLineAsync($"[{channel}] {line}").ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The log file was removed by an operator; keep draining the
                // pipe so the engine cannot block on a full buffer.
            }
        }
    }

    private static class StartInfoRenderer
    {
        public static string Render(string fileName, IEnumerable<string> arguments)
        {
            var builder = new StringBuilder(Quote(fileName));
            foreach (var argument in arguments)
            {
                builder.Append(' ').Append(Quote(argument));
            }

            return builder.ToString();
        }

        private static string Quote(string value) =>
            value.Contains(' ', StringComparison.Ordinal) ? "\"" + value + "\"" : value;
    }
}

public sealed record EngineStatus(
    string Name,
    string AdbSerial,
    int ConsolePort,
    int GrpcPort,
    InstanceState State,
    bool EngineAlive,
    int? EnginePid,
    string? AdbState,
    string? BootCompleted,
    string? Fingerprint,
    string? Abi,
    string? SdkLevel,
    string? LastError)
{
    public bool IsBooted => BootCompleted?.Trim() == "1";
}
