using AiSubtitlePro.Controls;
using AiSubtitlePro.ViewModels;
using System.Windows;
using Forms = System.Windows.Forms;
using AiSubtitlePro.Core.Models;

namespace AiSubtitlePro;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? ViewModel => DataContext as MainViewModel;

    public MainWindow()
    {
        InitializeComponent();

        // Wire up waveform seek to video player
        WaveformControl.PositionSeek += OnWaveformPositionSeek;

        // Sync video position with ViewModel
        VideoPlayer.PositionChanged += VideoPlayer_PositionChanged;
        VideoPlayer.MediaLoaded += VideoPlayer_MediaLoaded;
        VideoPlayer.VideoClicked += VideoPlayer_VideoClicked;
    }

    private void VideoPlayer_VideoClicked(object? sender, (int X, int Y) e)
    {
        var vm = ViewModel;
        if (vm?.SelectedLine == null || vm.CurrentDocument == null) return;

        vm.SelectedLine.PosX = e.X;
        vm.SelectedLine.PosY = e.Y;
        vm.CurrentDocument.IsDirty = true;
        vm.StatusMessage = $"Subtitle position set: ({e.X}, {e.Y})";

        vm.RefreshSubtitlePreview();
    }

    private void PickPrimaryColor_Click(object sender, RoutedEventArgs e)
    {
        PickAssColor(color =>
        {
            if (ViewModel == null) return;
            ViewModel.SelectedStylePrimaryAssColor = SubtitleStyle.ColorToAss(color);
        });
    }

    private void PickOutlineColor_Click(object sender, RoutedEventArgs e)
    {
        PickAssColor(color =>
        {
            if (ViewModel == null) return;
            ViewModel.SelectedStyleOutlineAssColor = SubtitleStyle.ColorToAss(color);
        });
    }

    private void PickBackColor_Click(object sender, RoutedEventArgs e)
    {
        PickAssColor(color =>
        {
            if (ViewModel == null) return;
            ViewModel.SelectedStyleBackAssColor = SubtitleStyle.ColorToAss(color);
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

    private void VideoPlayer_PositionChanged(object? sender, TimeSpan position)
    {
        if (ViewModel != null)
        {
            ViewModel.CurrentPosition = position;
            ViewModel.IsPlaying = VideoPlayer.IsPlaying;
        }
    }

    private void VideoPlayer_MediaLoaded(object? sender, EventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.MediaDuration = VideoPlayer.Duration;
        }
    }

    private void OnWaveformPositionSeek(object? sender, TimeSpan position)
    {
        VideoPlayer.SeekTo(position);
        if (ViewModel != null) ViewModel.CurrentPosition = position;
    }

    private void SubtitleGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Only seek if the selection change was user-initiated (grid has focus)
        // and we have a selected item
        if (ViewModel?.SelectedLine != null && SubtitleGrid.IsKeyboardFocusWithin)
        {
            VideoPlayer.SeekTo(ViewModel.SelectedLine.Start);
            ViewModel.CurrentPosition = ViewModel.SelectedLine.Start;
        }
    }

    private void SubtitleGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
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