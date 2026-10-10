using System;
using System.Runtime.InteropServices;
using QuickMail.Services;

namespace QuickMail.Helpers;

/// <summary>Bounded recovery of an unbalanced native cursor counter on the activating UI thread.</summary>
internal static class CursorVisibilityRecovery
{
    internal const int MaxCorrections = 32;

    public static void OnWindowActivated(IntPtr window)
    {
        if (window == IntPtr.Zero || GetForegroundWindow() != window) return;
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info))
        {
            LogService.Debug($"Cursor recovery: GetCursorInfo failed; error={Marshal.GetLastWin32Error()}");
            return;
        }
        // Do not interfere with Windows deliberately suppressing the cursor for touch/pen.
        if ((info.Flags & 2) != 0) return;

        // The global cursor may belong to another window under the pointer after Alt+Tab.
        // Probe THIS thread's counter, undoing the probe if it was already nonnegative.
        var corrections = RestoreCounter(ShowCursor);
        if (corrections == 0)
        {
            if (info.Flags == 0)
                LogService.Log($"Cursor recovery: MainWindow activated; global cursor hidden but UI-thread counter already visible; cursorHandle={info.Cursor}");
            return;
        }
        var after = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        var readAfter = GetCursorInfo(ref after);
        LogService.Log($"Cursor recovery: MainWindow activated; corrections={corrections}; " +
            $"beforeFlags={info.Flags}; afterFlags={(readAfter ? after.Flags.ToString() : "unknown")}; " +
            $"cursorHandle={info.Cursor}; limitReached={corrections == MaxCorrections}");
        // Do not set an Arrow or clear OverrideCursor: retain text, wait and resize cursors.
    }

    internal static int RestoreCounter(Func<bool, int> showCursor)
    {
        var count = showCursor(true);
        if (count > 0)
        {
            showCursor(false);
            return 0;
        }
        var corrections = 1;
        while (count < 0 && corrections < MaxCorrections)
        {
            count = showCursor(true);
            corrections++;
        }
        return corrections;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Cursor;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);
}
