using AiSubtitlePro.Core.Models;
using AiSubtitlePro.Infrastructure.AI;
using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace AiSubtitlePro.Views;

/// <summary>
/// Transcription Wizard for Whisper-based speech recognition
/// </summary>
public partial class TranscriptionWizard : Window
{
    private readonly WhisperEngine _whisperEngine;
    private CancellationTokenSource? _cts;

    private readonly string? _fixedMediaPath;
    private readonly TimeSpan? _startAbs;
    private readonly TimeSpan? _duration;
    
    public SubtitleDocument? Result { get; private set; }
    public string RuntimeUsed { get; private set; } = "";

    public TranscriptionWizard(string? mediaFilePath = null, TimeSpan? startAbs = null, TimeSpan? duration = null)
    {
        InitializeComponent();
        _whisperEngine = new WhisperEngine();
        _whisperEngine.ProgressChanged += OnProgressChanged;

        _fixedMediaPath = string.IsNullOrWhiteSpace(mediaFilePath) ? null : mediaFilePath;
        _startAbs = startAbs;
        _duration = duration;

        if (_fixedMediaPath != null)
        {
            FilePathBox.Text = _fixedMediaPath;
            FilePathBox.IsReadOnly = true;
        }
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        if (_fixedMediaPath != null)
            return;

        var dialog = new OpenFileDialog
        {
            Filter = "Media Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm;*.mp3;*.wav;*.flac;*.aac;*.m4a|" +
                    "Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm|" +
                    "Audio Files|*.mp3;*.wav;*.flac;*.aac;*.m4a|" +
                    "All Files|*.*",
            Title = "Select Media File for Transcription"
        };

        if (dialog.ShowDialog() == true)
        {
            FilePathBox.Text = dialog.FileName;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (WizardTabs.SelectedIndex > 0)
        {
            WizardTabs.SelectedIndex--;
        }
        UpdateButtons();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (WizardTabs.SelectedIndex < WizardTabs.Items.Count - 1)
        {
            // Validate current step
            if (!ValidateCurrentStep())
                return;

            WizardTabs.SelectedIndex++;
            UpdateButtons();

            // Start transcription on last step
            if (WizardTabs.SelectedIndex == WizardTabs.Items.Count - 1)
            {
                await StartTranscriptionAsync();
            }
        }
        else
        {
            // Finish
            DialogResult = true;
            Close();
        }
    }

    private bool ValidateCurrentStep()
    {
        switch (WizardTabs.SelectedIndex)
        {
            case 0: // File selection
                if (string.IsNullOrEmpty(FilePathBox.Text) || !File.Exists(FilePathBox.Text))
                {
                    MessageBox.Show("Please select a valid media file.", "Validation Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
                break;
            case 1: // Model selection
                var selectedModel = GetSelectedModel();
                if (!_whisperEngine.IsModelAvailable(selectedModel))
                {
                    var result = MessageBox.Show(
                        $"The {selectedModel} model is not found.\n" +
                        "Would you like to download it now?",
                        "Model Missing",
                        MessageBoxButton.YesNo, MessageBoxImage.Question);
                    
                    if (result == MessageBoxResult.Yes)
                    {
                        var downloadDialog = new ModelDownloadDialog(selectedModel);
                        downloadDialog.Owner = this;
                        if (downloadDialog.ShowDialog() != true)
                        {
                            return false; // Download failed or cancelled
                        }
                        // Verify again just in case
                        if (!_whisperEngine.IsModelAvailable(selectedModel))
                        {
                            MessageBox.Show("Model file verification failed after download.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            return false;
                        }
                    }
                    else
                    {
                        return false;
                    }
                }
                break;
        }
        return true;
    }

    private WhisperModelSize GetSelectedModel()
    {
        if (TinyModel.IsChecked == true) return WhisperModelSize.Tiny;
        if (BaseModel.IsChecked == true) return WhisperModelSize.Base;
        if (SmallModel.IsChecked == true) return WhisperModelSize.Small;
        if (MediumModel.IsChecked == true) return WhisperModelSize.Medium;
        if (LargeModel.IsChecked == true) return WhisperModelSize.Large;
        return WhisperModelSize.Base;
    }

    private string GetSelectedLanguage()
    {
        if (AutoDetectLang.IsChecked == true)
            return "auto";

        return (LanguageCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "auto";
    }

    private void UpdateButtons()
    {
        BackButton.IsEnabled = WizardTabs.SelectedIndex > 0;
        
        if (WizardTabs.SelectedIndex == WizardTabs.Items.Count - 1)
        {
            NextButton.Content = "Finish";
        }
        else
        {
            NextButton.Content = "Next →";
        }
    }

    private async Task StartTranscriptionAsync()
    {
        _cts = new CancellationTokenSource();
        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;

        try
        {
            LogText.Text = "Loading Whisper model...\n";
            ProgressText.Text = "Loading model...";
            ProgressBar.IsIndeterminate = true;

            await _whisperEngine.LoadModelAsync(GetSelectedModel(), _cts.Token);

            ProgressBar.IsIndeterminate = false;
            LogText.Text += "Model loaded successfully.\n";
            LogText.Text += $"Starting transcription of: {Path.GetFileName(FilePathBox.Text)}\n";

            if (_fixedMediaPath != null && _startAbs.HasValue && _duration.HasValue)
            {
                Result = await _whisperEngine.TranscribeAsync(
                    _fixedMediaPath,
                    startAbs: _startAbs.Value,
                    duration: _duration.Value,
                    language: GetSelectedLanguage(),
                    cancellationToken: _cts.Token);
            }
            else
            {
                Result = await _whisperEngine.TranscribeAsync(
                    FilePathBox.Text,
                    GetSelectedLanguage(),
                    _cts.Token);
            }

            RuntimeUsed = WhisperEngine.DetectRuntimeUsed();

            LogText.Text += $"\nTranscription complete! Generated {Result.Lines.Count} subtitle lines.\n";
            ProgressText.Text = "Complete!";
            ProgressBar.Value = 100;

            NextButton.IsEnabled = true;
            NextButton.Content = "Finish";
        }
        catch (OperationCanceledException)
        {
            LogText.Text += "\nTranscription cancelled.";
            ProgressText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            LogText.Text += $"\nError: {ex.Message}";
            ProgressText.Text = "Error occurred";
            MessageBox.Show($"Transcription failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BackButton.IsEnabled = true;
        }
    }

    private void OnProgressChanged(object? sender, TranscriptionProgress e)
    {
        Dispatcher.Invoke(() =>
        {
            ProgressBar.Value = e.ProgressPercent;
            ProgressText.Text = e.Status;

            if (e.CurrentSegment != null)
            {
                LogText.Text += $"[{e.CurrentSegment.Start:hh\\:mm\\:ss}] {e.CurrentSegment.Text}\n";
            }
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _whisperEngine.Dispose();
        base.OnClosed(e);
    }
}
