using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace CodexModelManager.Services;

public static class LocalPortPolicy
{
    public const int DefaultNativeEnginePort = 10100;
    public const int DefaultUnifiedGatewayPort = 10110;
    public const int CliProxyPortStart = 18000;
    public const int CliProxyPortEnd = 19999;

    public static bool IsUserPort(int port) => port is >= 1024 and <= 65535;

    public static bool IsCliProxyPortAllowed(int port, IEnumerable<int>? reservedPorts = null)
    {
        if (port is < CliProxyPortStart or > CliProxyPortEnd) return false;
        var reserved = reservedPorts is null
            ? DefaultReservedPorts()
            : new HashSet<int>(reservedPorts);
        return !reserved.Contains(port);
    }

    public static int FindAvailableCliProxyPort(
        string identity,
        IEnumerable<int> usedPorts,
        IEnumerable<int>? reservedPorts = null)
    {
        var used = new HashSet<int>(usedPorts);
        var reserved = reservedPorts is null
            ? DefaultReservedPorts()
            : new HashSet<int>(reservedPorts);
        var listening = GetListeningPorts();
        var size = CliProxyPortEnd - CliProxyPortStart + 1;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity ?? string.Empty));
        var offset = (int)(BitConverter.ToUInt32(digest, 0) % (uint)size);

        for (var index = 0; index < size; index++)
        {
            var candidate = CliProxyPortStart + ((offset + index) % size);
            if (used.Contains(candidate) || reserved.Contains(candidate) || listening.Contains(candidate))
                continue;
            if (CanBindLoopback(candidate)) return candidate;
        }

        throw new InvalidOperationException(
            $"{CliProxyPortStart}-{CliProxyPortEnd} 范围内没有可用的本机 CLIProxy 端口。");
    }

    public static bool IsListening(int port) => GetListeningPorts().Contains(port);

    private static HashSet<int> DefaultReservedPorts() =>
        new() { DefaultNativeEnginePort, DefaultUnifiedGatewayPort };

    private static HashSet<int> GetListeningPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(endpoint => endpoint.Port)
                .ToHashSet();
        }
        catch
        {
            // Failing closed here would make every clean installation unusable.
            // The exclusive bind probe below remains authoritative for each candidate.
            return new HashSet<int>();
        }
    }

    private static bool CanBindLoopback(int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                ExclusiveAddressUse = true
            };
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
