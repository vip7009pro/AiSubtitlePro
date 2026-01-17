using AiSubtitlePro.Controls;
using AiSubtitlePro.ViewModels;
using System.Windows;
using Forms = System.Windows.Forms;
using AiSubtitlePro.Core.Models;
using AiSubtitlePro.Views;
using System.Linq;

namespace AiSubtitlePro;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private string? _subtitleEditTextBefore;

    public MainWindow()
    {
        InitializeComponent();

        // Wire up waveform seek to video player
        WaveformControl.PositionSeek += OnWaveformPositionSeek;

        // Sync video position with ViewModel
        VideoPlayer.PositionChanged += VideoPlayer_PositionChanged;
        VideoPlayer.MediaLoaded += VideoPlayer_MediaLoaded;
        VideoPlayer.VideoClicked += VideoPlayer_VideoClicked;

        AddHandler(System.Windows.Input.Keyboard.PreviewKeyDownEvent,
            new System.Windows.Input.KeyEventHandler(MainWindow_PreviewKeyDown),
            true);

        SubtitleEditBox.GotKeyboardFocus += (_, __) =>
        {
            _subtitleEditTextBefore = ViewModel?.SelectedLine?.Text;
        };
        SubtitleEditBox.LostKeyboardFocus += (_, __) =>
        {
            var vm = ViewModel;
            if (vm == null) return;
            vm.CommitSelectedLineTextEdit(_subtitleEditTextBefore, vm.SelectedLine?.Text);
            _subtitleEditTextBefore = null;
        };

        SubtitleGrid.SelectionChanged += SubtitleGrid_SelectionChanged;
        SubtitleGrid.PreviewKeyDown += SubtitleGrid_PreviewKeyDown;
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null) return;

        // Frame-step seek with Left/Right arrows (when not typing in a text input).
        if (e.Key == System.Windows.Input.Key.Left || e.Key == System.Windows.Input.Key.Right)
        {
            if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox
                || System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase
                || System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.ComboBox)
            {
                // Let text editing/navigation work normally.
            }
            else
            {
                VideoPlayer.SeekToFrame(forward: e.Key == System.Windows.Input.Key.Right);
                e.Handled = true;
                return;
            }
        }

        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0)
            return;

        if (e.Key == System.Windows.Input.Key.P)
        {
            if (VideoPlayer.IsPlaying) VideoPlayer.Pause();
            else VideoPlayer.Play();
            e.Handled = true;
            return;
        }

        if (vm.SelectedLine == null) return;

        if (e.Key == System.Windows.Input.Key.D1)
        {
            var abs = vm.ToMediaTime(vm.SelectedLine.Start);
            VideoPlayer.SeekTo(abs);
            vm.CurrentPosition = vm.SelectedLine.Start;
            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.D2)
        {
            var abs = vm.ToMediaTime(vm.SelectedLine.End);
            VideoPlayer.SeekTo(abs);
            vm.CurrentPosition = vm.SelectedLine.End;
            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.D3)
        {
            var start = vm.CurrentPosition;
            if (start < TimeSpan.Zero) start = TimeSpan.Zero;
            if (vm.MediaDuration > TimeSpan.Zero && start > vm.MediaDuration) start = vm.MediaDuration;
            vm.SelectedLine.Start = start;
            vm.CurrentDocument!.IsDirty = true;
            vm.RefreshSubtitlePreview();
            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.D4)
        {
            var end = vm.CurrentPosition;
            if (end < TimeSpan.Zero) end = TimeSpan.Zero;
            if (vm.MediaDuration > TimeSpan.Zero && end > vm.MediaDuration) end = vm.MediaDuration;
            if (end < vm.SelectedLine.Start) end = vm.SelectedLine.Start;
            vm.SelectedLine.End = end;
            vm.CurrentDocument!.IsDirty = true;
            vm.RefreshSubtitlePreview();
            e.Handled = true;
            return;
        }
    }

    private void VideoPlayer_VideoClicked(object? sender, (int X, int Y) e)
    {
        var vm = ViewModel;
        if (vm?.SelectedLine == null || vm.CurrentDocument == null) return;

        vm.SetSelectedLinePosition(e.X, e.Y);
        vm.StatusMessage = $"Subtitle position set (double-click): ({e.X}, {e.Y})";
    }

    private void PickPrimaryColor_Click(object sender, RoutedEventArgs e)
    {
        PickAssColor(color =>
        {
            if (ViewModel == null) return;
            ViewModel.SelectedStylePrimaryAssColor = SubtitleStyle.ColorToAss(color);
            ViewModel.RefreshSubtitlePreview();
        });
    }

    private void PickOutlineColor_Click(object sender, RoutedEventArgs e)
    {
        PickAssColor(color =>
        {
            if (ViewModel == null) return;
            ViewModel.SelectedStyleOutlineAssColor = SubtitleStyle.ColorToAss(color);
            ViewModel.RefreshSubtitlePreview();
        });
    }

    private void PickBackColor_Click(object sender, RoutedEventArgs e)
    {
        PickAssColor(color =>
        {
            if (ViewModel == null) return;
            ViewModel.SelectedStyleBackAssColor = SubtitleStyle.ColorToAss(color);
            ViewModel.RefreshSubtitlePreview();
        });
    }

    private static void PickAssColor(Action<System.Drawing.Color> setter)
    {
        using var dlg = new Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true
        };

        if (dlg.ShowDialog() == Forms.DialogResult.OK)
        {
            setter(dlg.Color);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Application.Current.Shutdown();
    }

    private void OpenRouterApiKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenRouterApiKeyDialog();
        dlg.Owner = this;
        dlg.ShowDialog();
    }

    private void VideoPlayer_PositionChanged(object? sender, TimeSpan position)
    {
        if (ViewModel != null)
        {
            ViewModel.CurrentPosition = ViewModel.ToTimelineTime(position);
            ViewModel.IsPlaying = VideoPlayer.IsPlaying;
        }
    }

    private void VideoPlayer_MediaLoaded(object? sender, EventArgs e)
    {
        if (ViewModel != null)
        {
            if (VideoPlayer.Duration <= TimeSpan.Zero)
            {
                VideoPlayer.SeekTo(TimeSpan.Zero);
            }

            ViewModel.MediaDurationAbs = VideoPlayer.Duration;
            ViewModel.MediaDuration = ViewModel.GetTimelineDuration(ViewModel.MediaDurationAbs);
        }
    }

    private void OnWaveformPositionSeek(object? sender, TimeSpan position)
    {
        if (ViewModel == null)
        {
            VideoPlayer.SeekTo(position);
            return;
        }

        var abs = ViewModel.ToMediaTime(position);
        VideoPlayer.SeekTo(abs);
        ViewModel.CurrentPosition = position;
    }

    private void SubtitleGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Keep model selection flags in sync with DataGrid selection so keyboard selection (Ctrl+A)
        // is respected by ViewModel delete/shift operations.
        try
        {
            if (ViewModel?.DisplayedLines != null)
            {
                foreach (var removed in e.RemovedItems.OfType<SubtitleLine>())
                    removed.IsSelected = false;
                foreach (var added in e.AddedItems.OfType<SubtitleLine>())
                    added.IsSelected = true;
            }
        }
        catch
        {
        }

        // Only seek if the selection change was user-initiated (grid has focus)
        // and we have a selected item
        if (ViewModel?.SelectedLine != null && SubtitleGrid.IsKeyboardFocusWithin)
        {
            var abs = ViewModel.ToMediaTime(ViewModel.SelectedLine.Start);
            VideoPlayer.SeekTo(abs);
            ViewModel.CurrentPosition = ViewModel.SelectedLine.Start;
        }
    }

    private void SubtitleGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Ctrl+A should select all rows in the grid.
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0
            && e.Key == System.Windows.Input.Key.A)
        {
            SubtitleGrid.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.Delete && ViewModel != null)
        {
            if (ViewModel.DeleteSelectedLinesCommand.CanExecute(null))
            {
                ViewModel.DeleteSelectedLinesCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }
        
        if (e.Key == System.Windows.Input.Key.Enter && ViewModel != null)
        {
            // Allow Ctrl+Enter to bubble up (handled by InputBindings)
            if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
            {
                return;
            }

            e.Handled = true;
            var currentLine = ViewModel.SelectedLine;
            if (currentLine == null) return;

            var index = ViewModel.DisplayedLines.IndexOf(currentLine);
            if (index == ViewModel.DisplayedLines.Count - 1)
            {
                // Last line: Create new line
                ViewModel.AddLineCommand.Execute(null);
                
                // Focus the Edit Box to allow immediate typing
                SubtitleEditBox.Focus();
                SubtitleEditBox.CaretIndex = SubtitleEditBox.Text.Length;
            }
            else
            {
                // Not last line: Move to next
                SubtitleGrid.SelectedIndex = index + 1;
                SubtitleGrid.ScrollIntoView(SubtitleGrid.SelectedItem);
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        VideoPlayer?.Dispose();
        base.OnClosed(e);
    }
}