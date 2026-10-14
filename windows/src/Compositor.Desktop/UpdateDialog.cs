using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Compositor.Core.IO;

namespace Compositor.Desktop;

/// <summary>
/// Shows the Windows community build's own version information. The upstream macOS feed does not describe
/// this distribution, so it must not be used to claim this build is current or offer a macOS installer.
/// </summary>
internal sealed class UpdateDialog : DialogWindow
{
    private static readonly IBrush Ink = Skin.LabelBrush;

    private UpdateDialog(string title, string message, string? page)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var close = new Button { Content = L10n.T("Close"), IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (page is not null)
        {
            var open = new Button { Content = L10n.T("View Source…") };
            open.Click += (_, _) => Open(page);
            buttons.Children.Add(open);
        }
        buttons.Children.Add(close);
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Ink },
                buttons,
            },
        };
        Opened += (_, _) => close.Focus();
    }

    /// <summary>The release page opened in whatever the machine uses for links, and nothing when it cannot be.</summary>
    private static void Open(string page)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // A machine with no browser set up is not worth failing the dialog over: the address is in the box.
        }
    }

    public static async Task Ask(Window owner, string title, string message, string? page = null)
    {
        await new UpdateDialog(title, message, page).ShowDialog(owner);
    }

    /// <summary>No network check is made until this distribution has a dedicated Windows release feed.</summary>
    public static Task ShowWindowsReleaseInfo(Window owner) => Ask(owner,
        L10n.T("Windows Version Information"),
        L10n.F("Compositor {0}\nWindows community build\n\nAutomatic updates for this Windows build are not yet available. The upstream macOS releases cannot update this application.", Running));

    /// <summary>The version this build is: the one in the project file, read back off the assembly.</summary>
    public static AppVersion Running
    {
        get
        {
            var displayVersion = AppDiagnostics.Version.Split('+')[0];
            if (AppVersion.Parse(displayVersion) is { } preview) return preview;
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? new AppVersion(0, 0, 0, "") : new AppVersion(version.Major, version.Minor, version.Build, "");
        }
    }
}
