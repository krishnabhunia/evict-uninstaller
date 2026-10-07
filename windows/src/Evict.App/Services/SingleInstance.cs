using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App.Services;

/// <summary>
/// Second instances (Explorer context menu, command line) hand their arguments to the running instance
/// over a named pipe and exit, so there is always exactly one Evict window.
/// </summary>
public static class SingleInstance
{
    private const string PipeName = "EvictUninstaller.Args.v1";
    private static CancellationTokenSource? _cts;
    private static Action<string[]>? _onArgs;

    /// <summary>Tries to forward the arguments to an already running instance. Returns true on success.</summary>
    public static bool TryForward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2500);
            var bytes = Encoding.UTF8.GetBytes(CommandLineOptions.Pack(args.Length == 0 ? new[] { "--activate" } : args));
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Starts listening; <paramref name="onArgs"/> is invoked on the UI thread for every message.</summary>
    public static void StartServer(Action<string[]> onArgs)
    {
        Stop();
        _onArgs = onArgs;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = CreateServer();
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    using var ms = new MemoryStream();
                    await server.CopyToAsync(ms, ct).ConfigureAwait(false);
                    var payload = Encoding.UTF8.GetString(ms.ToArray());
                    var args = CommandLineOptions.Unpack(payload);
                    Application.Current?.Dispatcher.BeginInvoke(() => onArgs(args));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Warn("Single-instance pipe: " + ex.Message);
                    try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { break; }
                }
            }
        }, ct);
    }

    public static void Stop() => _cts?.Cancel();

    public static void RestartServer()
    {
        if (_onArgs != null) StartServer(_onArgs);
    }

    /// <summary>
    /// The pipe grants this user read/write explicitly: with the default security, a copy without administrator rights
    /// (Explorer's "Uninstall with Evict", Setup's --exit) could not reach an Evict running as administrator.
    /// </summary>
    private static NamedPipeServerStream CreateServer()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        }
        catch (Exception ex) when (ex is not IOException)
        {
            return new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }
    }
}
