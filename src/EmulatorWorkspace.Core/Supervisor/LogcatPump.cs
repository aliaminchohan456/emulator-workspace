using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using EmulatorWorkspace.Core.Engine;

namespace EmulatorWorkspace.Core.Supervisor;

public sealed record LogEntry(
    string Level,
    string Tag,
    string Message,
    string Raw,
    DateTimeOffset ReceivedUtc);

/// <summary>
/// Streams one guest's Logcat through a bounded queue. A burst can drop the
/// oldest lines instead of growing memory or stalling the UI; the dropped count
/// is reported with each batch boundary.
/// </summary>
public sealed class LogcatPump : IAsyncDisposable
{
    private static readonly Regex ThreadTime = new(
        @"^(?<date>\d\d-\d\d)\s+(?<time>\d\d:\d\d:\d\d\.\d+)\s+(?<pid>\d+)\s+(?<tid>\d+)\s+(?<level>[VDIWEFA])\s+(?<tag>.*?)\s*:\s(?<message>.*)$",
        RegexOptions.Compiled);

    private readonly string _serial;
    private readonly string _adbPath;
    private readonly Func<LogEntry, Task> _onEntry;
    private readonly Func<string, Task> _onClosed;
    private readonly Channel<LogEntry> _channel;
    private CancellationTokenSource? _cts;
    private Process? _process;
    private Task? _reader;
    private Task? _emitter;
    private long _dropped;

    public LogcatPump(
        string serial,
        string adbPath,
        Func<LogEntry, Task> onEntry,
        Func<string, Task> onClosed)
    {
        _serial = serial;
        _adbPath = adbPath;
        _onEntry = onEntry;
        _onClosed = onClosed;
        _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(2000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var startInfo = new ProcessStartInfo(_adbPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-s", _serial, "logcat", "-v", "threadtime" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        _process = Process.Start(startInfo)
                   ?? throw new InvalidOperationException("Could not start adb logcat.");
        _reader = Task.Run(() => ReadAsync(_process, _cts.Token));
        _emitter = Task.Run(() => EmitAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task ReadAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                var entry = Parse(line);
                if (!_channel.Writer.TryWrite(entry))
                {
                    Interlocked.Increment(ref _dropped);
                }
            }

            _ = await stderr.ConfigureAwait(false);
            await _onClosed("logcat stream ended").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await _onClosed(ex.Message).ConfigureAwait(false);
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    private async Task EmitAsync(CancellationToken cancellationToken)
    {
        var dropped = 0L;
        var windowStart = DateTimeOffset.UtcNow;
        var emitted = 0;
        await foreach (var entry in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var current = Interlocked.Read(ref _dropped);
            if (current != dropped)
            {
                dropped = current;
            }

            if ((DateTimeOffset.UtcNow - windowStart).TotalSeconds >= 1)
            {
                windowStart = DateTimeOffset.UtcNow;
                emitted = 0;
            }

            if (emitted >= 150)
            {
                Interlocked.Increment(ref _dropped);
                continue;
            }

            emitted++;
            try
            {
                await _onEntry(entry).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    private static LogEntry Parse(string line)
    {
        var match = ThreadTime.Match(line);
        if (!match.Success)
        {
            return new LogEntry("I", "raw", line, line, DateTimeOffset.UtcNow);
        }

        return new LogEntry(
            match.Groups["level"].Value,
            match.Groups["tag"].Value.Trim(),
            match.Groups["message"].Value,
            line,
            DateTimeOffset.UtcNow);
    }

    public async Task StopAsync()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        foreach (var task in new[] { _reader, _emitter })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _process?.Dispose();
        _process = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
