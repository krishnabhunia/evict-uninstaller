using System.Net.Sockets;

namespace Evict.Core.Services;

/// <summary>Identifies a denied socket connection without assuming which security product blocked it.</summary>
public static class UpdateNetworkDiagnostics
{
    public const string FirewallHelpUrl = "https://www.bitdefender.com/consumer/support/answer/13425/";

    public static bool IsSocketAccessDenied(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (current is SocketException socket &&
                (socket.SocketErrorCode == SocketError.AccessDenied || socket.NativeErrorCode == 10013))
                return true;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            }
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
        return false;
    }

    public static string PermissionDeniedMessage(string executablePath) =>
        "Windows denied Evict's internet connection (socket error 10013). " +
        "Check Bitdefender, Windows Firewall or your network policy for an application block. " +
        "Allow outbound HTTPS for this exact Evict executable, then retry: " + executablePath;
}
