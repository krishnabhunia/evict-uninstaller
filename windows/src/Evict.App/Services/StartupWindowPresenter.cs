using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Evict.Core.Util;

namespace Evict.App.Services;

/// <summary>Prepare a real WPF frame before exposing the main HWND. No fixed-duration splash delay.</summary>
internal static class StartupWindowPresenter
{
    public static async Task ShowAndWaitForFrameAsync(Window window, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRendered(object? sender, EventArgs args) => rendered.TrySetResult();
        window.ContentRendered += OnRendered;
        try
        {
            window.Show();
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        }
        finally { window.ContentRendered -= OnRendered; }
    }

    public static async Task RevealWhenReadyAsync(Window window, CancellationToken ct)
    {
        var opacity = window.Opacity;
        var taskbar = window.ShowInTaskbar;
        var activate = window.ShowActivated;
        var hitTest = window.IsHitTestVisible;
        var hwnd = IntPtr.Zero;
        var cloaked = false;
        var ready = false;
        window.Opacity = 0;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.IsHitTestVisible = false;
        try
        {
            // EnsureHandle creates the native window without showing it. DWM cloak additionally
            // conceals custom chrome while WPF prepares the content; opacity is the fallback.
            hwnd = new WindowInteropHelper(window).EnsureHandle();
            cloaked = SetCloak(hwnd, true);
            await ShowAndWaitForFrameAsync(window, ct);
            window.Opacity = opacity;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task.WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            ready = true;
        }
        finally
        {
            if (!ready) window.Hide();
            window.Opacity = opacity;
            window.ShowActivated = activate;
            window.IsHitTestVisible = hitTest;
            var uncloaked = !cloaked || SetCloak(hwnd, false);
            if (ready && !uncloaked)
            {
                // Never dismiss the splash while leaving the only usable window invisible.
                window.Hide();
                throw new InvalidOperationException("Windows could not reveal the prepared application window.");
            }
            window.ShowInTaskbar = taskbar;
        }
    }

    private static bool SetCloak(IntPtr hwnd, bool hide)
    {
        try
        {
            var value = hide ? 1 : 0;
            var result = DwmSetWindowAttribute(hwnd, 13 /* DWMWA_CLOAK */, ref value, sizeof(int));
            if (result != 0) Log.Warn($"Startup window cloak unavailable: 0x{result:X8}");
            return result == 0;
        }
        catch (Exception ex) { Log.Warn("Startup window cloak unavailable: " + ex.Message); return false; }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
