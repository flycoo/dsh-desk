using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DshDesk.Services;

internal static class WindowDragRestore
{
    internal static WindowRestorePosition CalculateRestorePosition(
        int cursorScreenX,
        int cursorScreenY,
        double pointerOffsetX,
        double pointerOffsetY,
        double titleBarWidth,
        int normalWindowWidth,
        double dpiScaleX,
        double dpiScaleY)
    {
        // Keep the grab point under the cursor after restoring: preserve the
        // horizontal grab fraction across the title bar and the vertical offset
        // from its top, exactly like native drag-to-restore on a caption.
        var horizontalFraction = titleBarWidth > 0 ? pointerOffsetX / titleBarWidth : 0.0;
        var left = (cursorScreenX - horizontalFraction * normalWindowWidth) / dpiScaleX;
        var top = (cursorScreenY - pointerOffsetY * dpiScaleY) / dpiScaleY;
        return new WindowRestorePosition(left, top);
    }

    internal static bool TryGetRestorePosition(
        Window window,
        FrameworkElement titleBar,
        System.Windows.Point pointerOffset,
        out WindowRestorePosition position)
    {
        position = default;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        if (!GetCursorPos(out var cursor))
        {
            return false;
        }

        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(handle, ref placement))
        {
            return false;
        }

        var normal = placement.NormalPosition;
        var dpi = VisualTreeHelper.GetDpi(window);
        position = CalculateRestorePosition(
            cursor.X,
            cursor.Y,
            pointerOffset.X,
            pointerOffset.Y,
            titleBar.ActualWidth,
            normal.Right - normal.Left,
            dpi.DpiScaleX,
            dpi.DpiScaleY);
        return true;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr windowHandle, ref WindowPlacement placement);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        internal int Length;
        internal int Flags;
        internal int ShowCmd;
        internal NativePoint MinPosition;
        internal NativePoint MaxPosition;
        internal NativeRectangle NormalPosition;
    }
}

internal readonly record struct WindowRestorePosition(double Left, double Top);
