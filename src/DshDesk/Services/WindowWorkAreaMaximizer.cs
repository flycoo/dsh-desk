using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DshDesk.Services;

internal sealed class WindowWorkAreaMaximizer : IDisposable
{
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    private readonly Window _window;
    private HwndSource? _source;
    private bool _disposed;

    internal WindowWorkAreaMaximizer(Window window)
    {
        _window = window;
        _window.SourceInitialized += Window_OnSourceInitialized;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.SourceInitialized -= Window_OnSourceInitialized;
        _source?.RemoveHook(WindowProc);
        _source = null;
    }

    internal static WindowMaximizeBounds CalculateBounds(
        int monitorLeft,
        int monitorTop,
        int workLeft,
        int workTop,
        int workRight,
        int workBottom) => new(
            workLeft - monitorLeft,
            workTop - monitorTop,
            workRight - workLeft,
            workBottom - workTop);

    internal static bool TryGetBoundsForWindow(IntPtr windowHandle, out WindowMaximizeBounds bounds)
    {
        bounds = default;
        var monitorHandle = MonitorFromWindow(windowHandle, MonitorDefaultToNearest);
        if (monitorHandle == IntPtr.Zero)
        {
            return false;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return false;
        }

        bounds = CalculateBounds(
            monitorInfo.Monitor.Left,
            monitorInfo.Monitor.Top,
            monitorInfo.WorkArea.Left,
            monitorInfo.WorkArea.Top,
            monitorInfo.WorkArea.Right,
            monitorInfo.WorkArea.Bottom);
        return true;
    }

    private void Window_OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(_window).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowProc);
    }

    private static IntPtr WindowProc(
        IntPtr windowHandle,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmGetMinMaxInfo || lParam == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (!TryGetBoundsForWindow(windowHandle, out var bounds))
        {
            return IntPtr.Zero;
        }

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMaxInfo.MaxPosition.X = bounds.X;
        minMaxInfo.MaxPosition.Y = bounds.Y;
        minMaxInfo.MaxSize.X = bounds.Width;
        minMaxInfo.MaxSize.Y = bounds.Height;
        Marshal.StructureToPtr(minMaxInfo, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        internal NativePoint Reserved;
        internal NativePoint MaxSize;
        internal NativePoint MaxPosition;
        internal NativePoint MinTrackSize;
        internal NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal NativeRectangle Monitor;
        internal NativeRectangle WorkArea;
        internal uint Flags;
    }
}

internal readonly record struct WindowMaximizeBounds(int X, int Y, int Width, int Height);
