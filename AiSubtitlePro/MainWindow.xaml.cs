using AiSubtitlePro.Controls;
using AiSubtitlePro.ViewModels;
using System.Windows;
using Forms = System.Windows.Forms;
using AiSubtitlePro.Core.Models;
using AiSubtitlePro.Views;
using CommunityToolkit.Mvvm.Input;
using System.Linq;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Input;
using System.IO;

namespace AiSubtitlePro;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private bool _closeConfirmed;

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

        Closing += MainWindow_Closing;
    }

    private static readonly string[] SubtitleExtensions = [".ass", ".ssa", ".srt", ".vtt"];
    private static readonly string[] MediaExtensions = [
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm",
        ".mp3", ".wav", ".flac", ".aac", ".m4a"
    ];

    private void MainWindow_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return;

        var file = files.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f));
        if (string.IsNullOrWhiteSpace(file))
            return;

        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (SubtitleExtensions.Contains(ext) || MediaExtensions.Contains(ext))
            e.Effects = DragDropEffects.Copy;
    }

    private async void MainWindow_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return;

        var vm = ViewModel;
        if (vm == null)
            return;

        var existingFiles = files.Where(f => !string.IsNullOrWhiteSpace(f) && File.Exists(f)).ToList();
        if (existingFiles.Count == 0)
            return;

        var media = existingFiles.FirstOrDefault(f => MediaExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
        var subtitle = existingFiles.FirstOrDefault(f => SubtitleExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));

        try
        {
            if (!string.IsNullOrWhiteSpace(media))
            {
                await vm.OpenMediaFromPathAsync(media);

                if (!string.IsNullOrWhiteSpace(subtitle))
                {
                    await HandleSubtitleDropAsync(vm, subtitle);
                }

                return;
            }

            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                await HandleSubtitleDropAsync(vm, subtitle);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không thể mở file:\n{ex.Message}", "Open", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static async Task HandleSubtitleDropAsync(MainViewModel vm, string subtitlePath)
    {
        var hasAnyLines = vm.CurrentDocument?.Lines?.Count > 0;

        if (hasAnyLines)
        {
            var result = MessageBox.Show(
                "Bạn có muốn đè subtitle hiện tại bằng file subtitle vừa kéo vào không?",
                "Đè subtitle",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;
        }

        await vm.OpenSubtitleFromPathAsync(subtitlePath);
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeConfirmed)
            return;

        var vm = ViewModel;
        var doc = vm?.CurrentDocument;
        if (doc == null || !doc.IsDirty)
            return;

        var result = MessageBox.Show(
            "Subtitles have been modified. Do you want to save before closing?",
            "Unsaved changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (result == MessageBoxResult.No)
        {
            _closeConfirmed = true;
            return;
        }

        // Yes: attempt to save. If user cancels Save As, document will remain dirty -> abort closing.
        try
        {
            if (vm?.SaveCommand is IAsyncRelayCommand asyncSave)
                await asyncSave.ExecuteAsync(null);
            else if (vm?.SaveCommand?.CanExecute(null) == true)
                vm.SaveCommand.Execute(null);
        }
        catch
        {
        }

        if (doc.IsDirty)
        {
            e.Cancel = true;
            return;
        }

        _closeConfirmed = true;
    }

    private static readonly Regex NumericRegex = new(@"^[0-9]*([.][0-9]*)?$", RegexOptions.Compiled);

    private static bool IsValidNumericCandidate(string currentText, string insertText)
    {
        var candidate = (currentText ?? string.Empty) + (insertText ?? string.Empty);
        return NumericRegex.IsMatch(candidate);
    }

    private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not TextBox tb)
            return;

        e.Handled = !IsValidNumericCandidate(tb.Text, e.Text);
    }

    private void NumericTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox tb)
            return;

        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            e.CancelCommand();
            return;
        }

        var text = e.DataObject.GetData(DataFormats.UnicodeText) as string;
        if (string.IsNullOrWhiteSpace(text))
        {
            e.CancelCommand();
            return;
        }

        if (!IsValidNumericCandidate(tb.Text, text.Trim()))
            e.CancelCommand();
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

                // Keep focus in grid and select the newly created row so Enter can be repeated.
                SubtitleGrid.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        var newLine = ViewModel.SelectedLine;
                        if (newLine != null)
                        {
                            SubtitleGrid.SelectedItem = newLine;
                            SubtitleGrid.ScrollIntoView(newLine);
                        }
                        SubtitleGrid.Focus();
                    }
                    catch
                    {
                    }
                });
            }
            else
            {
                // Not last line: Move to next
                SubtitleGrid.SelectedIndex = index + 1;
                SubtitleGrid.ScrollIntoView(SubtitleGrid.SelectedItem);
            }
        }
    }

    private void SubtitleEditBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm == null) return;

        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0
            && (e.Key == System.Windows.Input.Key.Up || e.Key == System.Windows.Input.Key.Down))
        {
            e.Handled = true;

            var current = vm.SelectedLine;
            if (current == null)
                return;

            var index = vm.DisplayedLines.IndexOf(current);
            if (index < 0)
                return;

            var nextIndex = e.Key == System.Windows.Input.Key.Up ? index - 1 : index + 1;
            if (nextIndex < 0) nextIndex = 0;
            if (nextIndex >= vm.DisplayedLines.Count) nextIndex = vm.DisplayedLines.Count - 1;

            if (nextIndex != index)
                vm.SelectedLine = vm.DisplayedLines[nextIndex];

            if (vm.SelectedLine != null)
            {
                var abs = vm.ToMediaTime(vm.SelectedLine.Start);
                VideoPlayer.SeekTo(abs);
                vm.CurrentPosition = vm.SelectedLine.Start;
            }

            SubtitleGrid.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (vm.SelectedLine != null)
                    {
                        SubtitleGrid.SelectedItem = vm.SelectedLine;
                        SubtitleGrid.ScrollIntoView(vm.SelectedLine);
                    }

                    SubtitleEditBox.Focus();
                    SubtitleEditBox.CaretIndex = SubtitleEditBox.Text?.Length ?? 0;
                }
                catch
                {
                }
            });

            return;
        }

        if (e.Key == System.Windows.Input.Key.Enter
            && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            // Ctrl+Enter: move to next subtitle and keep editing in the text box.
            e.Handled = true;

            var current = vm.SelectedLine;
            if (current == null)
                return;

            var index = vm.DisplayedLines.IndexOf(current);
            if (index < 0)
                return;

            if (index == vm.DisplayedLines.Count - 1)
            {
                // Last line: create a new line and select it
                if (vm.AddLineCommand.CanExecute(null))
                    vm.AddLineCommand.Execute(null);
            }
            else
            {
                // Move to next line
                vm.SelectedLine = vm.DisplayedLines[index + 1];
            }

            SubtitleGrid.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (vm.SelectedLine != null)
                    {
                        SubtitleGrid.SelectedItem = vm.SelectedLine;
                        SubtitleGrid.ScrollIntoView(vm.SelectedLine);
                    }

                    SubtitleEditBox.Focus();
                    SubtitleEditBox.CaretIndex = SubtitleEditBox.Text?.Length ?? 0;
                }
                catch
                {
                }
            });
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        VideoPlayer?.Dispose();
        base.OnClosed(e);
    }
}