using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DshDesk.Models;
using DrawingRectangle = System.Drawing.Rectangle;
using Forms = System.Windows.Forms;

namespace DshDesk.Services;

internal static class WindowPlacementService
{
    private const int ShowNormal = 1;
    private const int ShowMaximized = 3;
    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 820;
    private const int MinimumWidth = 820;
    private const int MinimumHeight = 560;

    internal static void Restore(Window window, WindowPlacementSettings? saved)
    {
        if (saved is null)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var workAreas = Forms.Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray();
        var fallback = Forms.Screen.PrimaryScreen?.WorkingArea ?? new DrawingRectangle(0, 0, DefaultWidth, DefaultHeight);
        var normalized = Normalize(saved, workAreas, fallback);
        var placement = new WindowPlacement
        {
            Length = Marshal.SizeOf<WindowPlacement>(),
            ShowCmd = normalized.Maximized ? ShowMaximized : ShowNormal,
            NormalPosition = new NativeRectangle
            {
                Left = normalized.Left,
                Top = normalized.Top,
                Right = normalized.Right,
                Bottom = normalized.Bottom
            }
        };
        SetWindowPlacement(handle, ref placement);
    }

    internal static WindowPlacementSettings? Capture(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(handle, ref placement))
        {
            return null;
        }

        var bounds = placement.NormalPosition;
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
        {
            return null;
        }

        return new WindowPlacementSettings
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Right = bounds.Right,
            Bottom = bounds.Bottom,
            Maximized = placement.ShowCmd == ShowMaximized ||
                        placement.ShowCmd == 2 && (placement.Flags & 0x0002) != 0
        };
    }

    internal static WindowPlacementSettings Normalize(
        WindowPlacementSettings saved,
        IReadOnlyList<DrawingRectangle> workAreas,
        DrawingRectangle fallback)
    {
        var width = saved.Right - saved.Left;
        var height = saved.Bottom - saved.Top;
        if (width <= 0) width = DefaultWidth;
        if (height <= 0) height = DefaultHeight;

        var savedRectangle = new DrawingRectangle(saved.Left, saved.Top, width, height);
        var target = workAreas
            .Select(area => new { Area = area, Intersection = DrawingRectangle.Intersect(area, savedRectangle) })
            .OrderByDescending(candidate => (long)candidate.Intersection.Width * candidate.Intersection.Height)
            .FirstOrDefault(candidate => candidate.Intersection.Width >= 64 && candidate.Intersection.Height >= 64)
            ?.Area ?? fallback;

        width = Math.Min(Math.Max(width, Math.Min(MinimumWidth, target.Width)), target.Width);
        height = Math.Min(Math.Max(height, Math.Min(MinimumHeight, target.Height)), target.Height);
        var left = Math.Clamp(saved.Left, target.Left, target.Right - width);
        var top = Math.Clamp(saved.Top, target.Top, target.Bottom - height);

        return new WindowPlacementSettings
        {
            Left = left,
            Top = top,
            Right = left + width,
            Bottom = top + height,
            Maximized = saved.Maximized
        };
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr windowHandle, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr windowHandle, ref WindowPlacement placement);

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
