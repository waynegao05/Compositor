using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Compositor.Core.IO;

namespace Compositor.Desktop;

internal sealed class RecoveryDialog : DialogWindow
{
    private readonly ListBox _list = new();
    private readonly Button _restore = new() { Content = L10n.T("Recover to a new folder…"), IsEnabled = false };
    private readonly Button _cancel = new() { Content = L10n.T("Cancel"), IsCancel = true };
    private ProjectRecoveryCandidate? _result;

    private RecoveryDialog(IReadOnlyList<ProjectRecoveryCandidate> candidates)
    {
        Title = L10n.T("Recover interrupted saves");
        Width = 720;
        Height = 480;
        MinWidth = 480;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var summary = new TextBlock
        {
            Text = L10n.T("Choose a saved copy to recover. The original project and recovery files will be kept. Choose a new destination in the next step."),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        foreach (var candidate in candidates)
        {
            var kind = L10n.T(candidate.Kind == ProjectRecoveryKind.PendingSave ? "Interrupted save" : "Previous saved version");
            _list.Items.Add(new ListBoxItem
            {
                Tag = candidate,
                Content = new StackPanel
                {
                    Spacing = 4,
                    Margin = new Thickness(4),
                    Children =
                    {
                        new TextBlock { Text = L10n.F("{0} · {1}", Path.GetFileName(candidate.TargetPath), kind), FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = L10n.F("{0} × {1} pixels · {2}", candidate.Width, candidate.Height, candidate.CreatedUtc.ToLocalTime().ToString("g")) },
                        new TextBlock { Text = candidate.TargetPath, TextWrapping = TextWrapping.Wrap },
                    },
                },
            });
        }
        _list.SelectionChanged += (_, _) => _restore.IsEnabled = _list.SelectedItem is ListBoxItem;
        _restore.Click += (_, _) =>
        {
            if ((_list.SelectedItem as ListBoxItem)?.Tag is not ProjectRecoveryCandidate candidate) return;
            _result = candidate;
            Close();
        };
        _cancel.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { _cancel, _restore },
        };
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(_list, 1);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(summary);
        grid.Children.Add(_list);
        grid.Children.Add(buttons);
        Content = grid;
        Opened += (_, _) => _cancel.Focus();
    }

    internal static async Task<ProjectRecoveryCandidate?> Ask(Window owner, IReadOnlyList<ProjectRecoveryCandidate> candidates)
    {
        var dialog = new RecoveryDialog(candidates);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    internal static string SelfCheck()
    {
        var candidate = new ProjectRecoveryCandidate(Guid.NewGuid(), Path.GetFullPath("中文 工程.comp"),
            Path.GetFullPath("候选副本.tmp"), DateTimeOffset.UtcNow, ProjectRecoveryKind.PendingSave, Guid.NewGuid(), 320, 240);
        var owner = new Window();
        owner.Show();
        try
        {
            var canceled = new RecoveryDialog([candidate]);
            var cancelTask = canceled.ShowDialog(owner);
            Dispatcher.UIThread.RunJobs();
            Require(!canceled._restore.IsEnabled && canceled._result is null, "recovery starts with implicit approval");
            canceled._list.SelectedIndex = 0;
            Require(canceled._restore.IsEnabled, "selecting a recovery candidate did not enable recovery");
            canceled._cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Require(cancelTask.IsCompleted && canceled._result is null, "canceling selected recovery approved it");

            var chosen = new RecoveryDialog([candidate]);
            var chosenTask = chosen.ShowDialog(owner);
            chosen._list.SelectedIndex = 0;
            chosen._restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Require(chosenTask.IsCompleted && chosen._result == candidate, "recovery did not return the explicit selection");

            var dismissed = new RecoveryDialog([candidate with { Kind = ProjectRecoveryKind.PreviousSave }]);
            var dismissedTask = dismissed.ShowDialog(owner);
            dismissed._list.SelectedIndex = 0;
            dismissed.Close();
            Dispatcher.UIThread.RunJobs();
            Require(dismissedTask.IsCompleted && dismissed._result is null, "dialog X approved recovery");
            return "recovery dialog: selection required; Cancel and window X leave recovery files untouched; explicit choice returned";
        }
        finally { owner.Close(); }

        static void Require(bool condition, string error)
        {
            if (!condition) throw new InvalidOperationException(error);
        }
    }
}
