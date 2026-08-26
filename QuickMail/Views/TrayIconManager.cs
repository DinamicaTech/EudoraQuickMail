using System;
using QuickMail.Services;

namespace QuickMail.Views;

/// <summary>
/// Owns the application's permanent system-tray <c>NotifyIcon</c>. WPF has no native tray icon,
/// so this is the one place WinForms is used (its types are always fully qualified — see the
/// UseWindowsForms note in QuickMail.csproj). New mail switches the icon to a badged variant until
/// the user opens QuickMail. The callbacks fire on the UI thread (the NotifyIcon is created on it).
/// </summary>
internal sealed class TrayIconManager : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _normalIcon;
    private readonly System.Drawing.Icon _newMailIcon;
    private readonly System.IO.Stream? _newMailIconStream;
    private int _pendingNewMessages;

    public TrayIconManager(Action onOpen, Action onExit)
    {
        _normalIcon  = LoadAppIcon();
        _newMailIcon = LoadNewMailIcon(_normalIcon, out _newMailIconStream);
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text    = "Eudora QuickMail",
            Icon    = _normalIcon,
            Visible = false,
        };
        // Restore on the primary activation: a single left-click, the keyboard Enter/Space default
        // action (the shell surfaces it as a left MouseClick), and the traditional double-click.
        // Right-click is left to the context menu below, so it never restores. Without the single
        // MouseClick handler, keyboard users who focus the icon and press Enter got no response —
        // only double-click worked.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button != System.Windows.Forms.MouseButtons.Left) return;
            ClearNewMailIndicator();
            onOpen();
        };
        _icon.DoubleClick += (_, _) =>
        {
            ClearNewMailIndicator();
            onOpen();
        };

        var menu     = new System.Windows.Forms.ContextMenuStrip();
        var openItem = new System.Windows.Forms.ToolStripMenuItem("&Open Eudora QuickMail");
        openItem.Click += (_, _) => onOpen();
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("E&xit Eudora QuickMail");
        exitItem.Click += (_, _) => onExit();
        menu.Items.Add(openItem);
        menu.Items.Add(exitItem);
        _icon.ContextMenuStrip = menu;
    }

    public void Show() => _icon.Visible = true;
    public void Hide() => _icon.Visible = false;

    /// <summary>Marks the tray icon with a visible red badge until QuickMail is opened.</summary>
    public void SignalNewMail(string accountLabel, int count)
    {
        if (count <= 0) return;
        _pendingNewMessages += count;
        // Re-registering forces Explorer to repaint the icon. Merely assigning Icon can leave the
        // old image cached on some Windows 10/11 shells even though the tooltip has changed.
        var wasVisible = _icon.Visible;
        if (wasVisible) _icon.Visible = false;
        _icon.Icon = _newMailIcon;
        _icon.Text = _pendingNewMessages == 1
            ? "Eudora QuickMail — 1 new message"
            : $"Eudora QuickMail — {_pendingNewMessages:N0} new messages";
        _icon.Visible = true;
        LogService.Debug($"Tray icon: signalled {count:N0} new message(s) for {accountLabel}.");
    }

    public void ClearNewMailIndicator()
    {
        _pendingNewMessages = 0;
        _icon.Icon = _normalIcon;
        _icon.Text = "Eudora QuickMail";
    }

    // The app ships no dedicated .ico, so use the executable's own icon; fall back to the generic
    // application icon if it can't be read. Picks up a real app icon automatically once one is set.
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
        }
        catch (Exception ex)
        {
            LogService.Debug($"TrayIconManager: could not load app icon: {ex.Message}");
        }
        return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }

    private static System.Drawing.Icon LoadNewMailIcon(System.Drawing.Icon fallbackSource,
        out System.IO.Stream? resourceStream)
    {
        resourceStream = null;
        try
        {
            var resource = System.Windows.Application.GetResourceStream(new Uri(
                "pack://application:,,,/QuickMail;component/Assets/App/QuickMailReceived.ico",
                UriKind.Absolute));
            if (resource?.Stream is { } stream)
            {
                // System.Drawing.Icon retains its source stream, so keep the WPF resource stream
                // alive for exactly as long as the NotifyIcon owns the icon.
                resourceStream = stream;
                return new System.Drawing.Icon(stream);
            }
        }
        catch (Exception ex)
        {
            resourceStream?.Dispose();
            resourceStream = null;
            LogService.Debug($"TrayIconManager: could not load received-mail icon: {ex.Message}");
        }

        // A missing/corrupt optional resource must never prevent QuickMail from starting.
        return CreateBadgedNewMailIcon(fallbackSource);
    }

    private static System.Drawing.Icon CreateBadgedNewMailIcon(System.Drawing.Icon source)
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.Clear(System.Drawing.Color.Transparent);
            graphics.DrawIcon(source, new System.Drawing.Rectangle(0, 0, 32, 32));
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var outline = new System.Drawing.Pen(System.Drawing.Color.White, 2f);
            using var badge = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(220, 32, 32));
            graphics.FillEllipse(badge, 18, 18, 13, 13);
            graphics.DrawEllipse(outline, 18, 18, 13, 13);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)borrowed.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false; // remove from the tray immediately
        _icon.Dispose();
        _newMailIcon.Dispose();
        _newMailIconStream?.Dispose();
        _normalIcon.Dispose();
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr handle);
    }
}
