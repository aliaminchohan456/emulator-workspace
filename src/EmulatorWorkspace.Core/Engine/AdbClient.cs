using System.Diagnostics;
using System.Text.RegularExpressions;

namespace EmulatorWorkspace.Core.Engine;

/// <summary>
/// ADB access that is always bound to one exact emulator serial. There is no
/// "first device" fallback and no global server control: a wrong or ambiguous
/// target fails loudly instead of silently touching another device.
/// </summary>
public sealed class AdbClient
{
    private static readonly Regex SerialPattern = new(@"^emulator-[0-9]{4,5}$", RegexOptions.Compiled);

    private readonly string _adbPath;

    public AdbClient(string adbPath)
    {
        _adbPath = adbPath;
    }

    public string AdbPath => _adbPath;

    public static string ValidateSerial(string serial)
    {
        if (!SerialPattern.IsMatch(serial))
        {
            throw new ArgumentException(
                $"'{serial}' is not an Android Emulator serial. Only managed emulator transports are addressable.",
                nameof(serial));
        }

        return serial;
    }

    public async Task<ProcessResult> RunAsync(
        string serial,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        var args = new List<string> { "-s", serial };
        args.AddRange(arguments);

        return await ProcessRunner.RunAsync(
            _adbPath,
            args,
            timeout: timeout ?? TimeSpan.FromSeconds(60),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ShellAsync(
        string serial,
        IEnumerable<string> command,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "shell" };
        args.AddRange(command);
        var result = await RunAsync(serial, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.StandardOutput.Trim();
    }

    /// <summary>
    /// Read-only listing of every transport the pinned server can see. It never
    /// changes server state and is used to prove which transport a managed
    /// serial resolves to; no global ADB control is performed.
    /// </summary>
    public async Task<string> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            _adbPath,
            new[] { "devices", "-l" },
            timeout: TimeSpan.FromSeconds(20),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.StandardOutput + result.StandardError;
    }

    public async Task<string> GetPropAsync(string serial, string key, CancellationToken cancellationToken = default) =>
        await ShellAsync(serial, new[] { "getprop", key }, cancellationToken).ConfigureAwait(false);

    public async Task<string> GetStateAsync(string serial, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(serial, new[] { "get-state" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return (result.StandardOutput + result.StandardError).Trim();
    }

    public async Task<string> GetTransportAsync(string serial, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(serial, new[] { "get-serialno" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return (result.StandardOutput + result.StandardError).Trim();
    }

    /// <summary>
    /// Waits for the guest to report boot completion, not merely for the
    /// transport to appear. Returns the elapsed time to readiness.
    /// </summary>
    public async Task<TimeSpan?> WaitForBootCompletedAsync(
        string serial,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var interval = pollInterval ?? TimeSpan.FromSeconds(2);

        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var state = await GetStateAsync(serial, cancellationToken).ConfigureAwait(false);
                if (state.Contains("device", StringComparison.Ordinal))
                {
                    var bootCompleted = await GetPropAsync(serial, "sys.boot_completed", cancellationToken)
                        .ConfigureAwait(false);
                    if (bootCompleted.Trim() == "1")
                    {
                        return stopwatch.Elapsed;
                    }
                }
            }
            catch (TimeoutException)
            {
                // Transient: the guest is still starting its ADB daemon.
            }
            catch (InvalidOperationException)
            {
                // adb can fail while the emulator is still bringing up the
                // transport; keep polling until the deadline.
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public async Task TapAsync(string serial, int x, int y, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(
            serial,
            new[] { "shell", "input", "tap", x.ToString(), y.ToString() },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess($"Tap at {x},{y}");
    }

    public async Task<byte[]> ScreencapPngAsync(string serial, CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        return await RunRawAsync(
            serial,
            new[] { "exec-out", "screencap", "-p" },
            TimeSpan.FromSeconds(90),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> EmulatorConsoleAsync(
        string serial,
        IEnumerable<string> command,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "emu" };
        args.AddRange(command);
        var result = await RunAsync(serial, args, TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
        result.EnsureSuccess($"Emulator console command '{string.Join(' ', command)}'");
        return (result.StandardOutput + result.StandardError).Trim();
    }

    private async Task<byte[]> RunRawAsync(
        string serial,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ValidateSerial(serial);
        var startInfo = new ProcessStartInfo(_adbPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-s");
        startInfo.ArgumentList.Add(serial);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start adb.");
        }

        using var buffer = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            await copy.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException($"adb did not finish within {timeout.TotalSeconds:N0}s.");
        }

        var text = await error.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"adb exited with {process.ExitCode}: {text.Trim()}");
        }

        return buffer.ToArray();
    }
}
