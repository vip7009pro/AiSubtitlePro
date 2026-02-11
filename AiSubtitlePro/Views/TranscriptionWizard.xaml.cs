using AiSubtitlePro.Core.Models;
using AiSubtitlePro.Infrastructure.AI;
using Microsoft.Win32;
using System.IO;
using System.Linq;
using System.Management;
using System.Windows;
using System.Windows.Threading;

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
    public TimeSpan Elapsed { get; private set; }

    private readonly System.Diagnostics.Stopwatch _transcribeStopwatch = new();

    private DispatcherTimer? _elapsedTimer;
    private string _progressStatus = "";

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

        Loaded += (_, __) =>
        {
            PopulateComputeOptions();
            PopulateGpuList();
        };
    }

    private void PopulateComputeOptions()
    {
        try
        {
            var supported = _whisperEngine.GetSupportedBackends().ToHashSet();

            CpuRadio.IsEnabled = true;
            CudaRadio.IsEnabled = supported.Contains(WhisperComputeBackend.Cuda);
            VulkanRadio.IsEnabled = supported.Contains(WhisperComputeBackend.Vulkan);

            // If CPU is not selected but that backend isn't available, force CPU.
            if (CudaRadio.IsChecked == true && !CudaRadio.IsEnabled)
                CpuRadio.IsChecked = true;
            if (VulkanRadio.IsChecked == true && !VulkanRadio.IsEnabled)
                CpuRadio.IsChecked = true;
        }
        catch
        {
            // If anything goes wrong, keep CPU as fallback.
            CpuRadio.IsChecked = true;
            CudaRadio.IsEnabled = false;
            VulkanRadio.IsEnabled = false;
        }
    }

    private void PopulateGpuList()
    {
        try
        {
            GpuList.Items.Clear();

            var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (var mo in searcher.Get())
            {
                var name = mo["Name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    GpuList.Items.Add(name);
            }

            if (GpuList.Items.Count == 0)
                GpuList.Items.Add("(No GPU detected)");

            if (GpuList.Items.Count > 0)
                GpuList.SelectedIndex = 0;
        }
        catch
        {
            try
            {
                GpuList.Items.Clear();
                GpuList.Items.Add("(GPU list unavailable)");
                GpuList.SelectedIndex = 0;
            }
            catch
            {
            }
        }
    }

    private WhisperComputeBackend GetSelectedBackend()
    {
        if (CudaRadio.IsChecked == true && CudaRadio.IsEnabled)
            return WhisperComputeBackend.Cuda;
        if (VulkanRadio.IsChecked == true && VulkanRadio.IsEnabled)
            return WhisperComputeBackend.Vulkan;
        return WhisperComputeBackend.Cpu;
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
            case 2: // Compute selection
                // No strict validation needed; we will fall back to CPU if unsupported.
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

        _transcribeStopwatch.Restart();
        StartElapsedTimer();

        try
        {
            LogText.Text = "Loading Whisper model...\n";
            _progressStatus = "Loading model...";
            ProgressText.Text = $"{_progressStatus} (elapsed {FormatElapsed(_transcribeStopwatch.Elapsed)})";
            ProgressBar.IsIndeterminate = true;

            _whisperEngine.Backend = GetSelectedBackend();
            LogText.Text += $"Compute: {_whisperEngine.Backend}\n";

            await _whisperEngine.LoadModelAsync(GetSelectedModel(), _cts.Token);

            ProgressBar.IsIndeterminate = false;
            LogText.Text += "Model loaded successfully.\n";
            LogText.Text += $"Starting transcription of: {Path.GetFileName(FilePathBox.Text)}\n";
            _progressStatus = "Transcribing...";

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
            Elapsed = _transcribeStopwatch.Elapsed;

            LogText.Text += $"\nTranscription complete! Generated {Result.Lines.Count} subtitle lines.\n";
            _progressStatus = "Complete!";
            ProgressText.Text = $"{_progressStatus} (elapsed {FormatElapsed(_transcribeStopwatch.Elapsed)})";
            ProgressBar.Value = 100;

            NextButton.IsEnabled = true;
            NextButton.Content = "Finish";
        }
        catch (OperationCanceledException)
        {
            LogText.Text += "\nTranscription cancelled.";
            Elapsed = _transcribeStopwatch.Elapsed;
            _progressStatus = "Cancelled";
            ProgressText.Text = $"{_progressStatus} (elapsed {FormatElapsed(_transcribeStopwatch.Elapsed)})";
        }
        catch (Exception ex)
        {
            LogText.Text += $"\nError: {ex.Message}";
            Elapsed = _transcribeStopwatch.Elapsed;
            _progressStatus = "Error occurred";
            ProgressText.Text = $"{_progressStatus} (elapsed {FormatElapsed(_transcribeStopwatch.Elapsed)})";
            MessageBox.Show($"Transcription failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StopElapsedTimer();
            BackButton.IsEnabled = true;
        }
    }

    private void StartElapsedTimer()
    {
        StopElapsedTimer();

        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _elapsedTimer.Tick += (_, __) =>
        {
            if (_transcribeStopwatch.IsRunning)
                ProgressText.Text = $"{_progressStatus} (elapsed {FormatElapsed(_transcribeStopwatch.Elapsed)})";
        };
        _elapsedTimer.Start();
    }

    private void StopElapsedTimer()
    {
        try
        {
            if (_elapsedTimer != null)
            {
                _elapsedTimer.Stop();
                _elapsedTimer = null;
            }
        }
        catch
        {
        }
    }

    private void OnProgressChanged(object? sender, TranscriptionProgress e)
    {
        Dispatcher.Invoke(() =>
        {
            ProgressBar.Value = e.ProgressPercent;
            _progressStatus = e.Status;
            ProgressText.Text = $"{_progressStatus} (elapsed {FormatElapsed(_transcribeStopwatch.Elapsed)})";

            if (e.CurrentSegment != null)
            {
                LogText.Text += $"[{e.CurrentSegment.Start:hh\\:mm\\:ss}] {e.CurrentSegment.Text}\n";
            }
        });
    }

    private static string FormatElapsed(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
        return $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        StopElapsedTimer();
        _whisperEngine.Dispose();
        base.OnClosed(e);
    }
}
