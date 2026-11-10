using System.Net;
using System.Net.Sockets;

namespace EmulatorWorkspace.Core.Instances;

/// <summary>
/// Allocates unique, free port pairs for instances. The emulator console port
/// must be even and ADB binds console+1, so a pair is reserved as a unit.
/// </summary>
public static class PortAllocator
{
    public const int FirstConsolePort = 5554;
    public const int LastConsolePort = 5584;

    public static (int ConsolePort, int GrpcPort) Allocate(IEnumerable<InstanceRecord> existing, int firstGrpcPort = 8554)
    {
        var usedConsole = existing.Select(r => r.ConsolePort).ToHashSet();
        var usedGrpc = existing.Select(r => r.GrpcPort).ToHashSet();

        for (var console = FirstConsolePort; console <= LastConsolePort; console += 2)
        {
            if (usedConsole.Contains(console) || !IsPortFree(console) || !IsPortFree(console + 1))
            {
                continue;
            }

            for (var grpc = firstGrpcPort; grpc < firstGrpcPort + 200; grpc++)
            {
                if (usedGrpc.Contains(grpc) || !IsPortFree(grpc))
                {
                    continue;
                }

                return (console, grpc);
            }
        }

        throw new InvalidOperationException(
            $"No free emulator console/ADB/controller port triple is available between {FirstConsolePort} and {LastConsolePort}.");
    }

    public static bool IsPortFree(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            try
            {
                listener.Stop();
            }
            catch (SocketException)
            {
                // Nothing useful to do: the port is already considered in use.
            }
        }
    }
}
