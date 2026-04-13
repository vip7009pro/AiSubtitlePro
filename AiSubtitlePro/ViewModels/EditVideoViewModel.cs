using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiSubtitlePro.Services;

namespace AiSubtitlePro.ViewModels;

public partial class EditVideoViewModel : ObservableObject
{
    private readonly VideoObfuscator _obfuscator;
    private readonly Dispatcher _uiDispatcher;
    private CancellationTokenSource? _cancellationTokenSource;

    [ObservableProperty]
    private string inputVideoPath = string.Empty;

    [ObservableProperty]
    private string outputVideoPath = string.Empty;

    [ObservableProperty]
    private bool isProcessing = false;

    [ObservableProperty]
    private int progressValue = 0;

    [ObservableProperty]
    private string progressText = "Ready";

    [ObservableProperty]
    private ObservableCollection<string> logMessages = new();

    // Processing Options Properties
    [ObservableProperty]
    private bool removeMetadata = true;

    [ObservableProperty]
    private bool changeAspectRatio = true;

    [ObservableProperty]
    private string aspectRatio = "16:9";

    [ObservableProperty]
    private bool horizontalFlip = true;

    [ObservableProperty]
    private bool adjustSpeed = true;

    [ObservableProperty]
    private double speedMultiplier = 0.98;

    [ObservableProperty]
    private bool adjustColors = true;

    [ObservableProperty]
    private double brightness = 0.05;

    [ObservableProperty]
    private double contrast = 1.05;

    [ObservableProperty]
    private double saturation = 1.1;

    [ObservableProperty]
    private bool shiftPitch = true;

    [ObservableProperty]
    private double pitchShift = -0.5;

    [ObservableProperty]
    private bool replaceMusic = false;

    [ObservableProperty]
    private string musicFilePath = string.Empty;

    [ObservableProperty]
    private bool reencodeVideo = true;

    [ObservableProperty]
    private string codec = "libx265";

    [ObservableProperty]
    private int crfValue = 22;

    [ObservableProperty]
    private int gopSize = 250;

    [ObservableProperty]
    private bool addWatermark = false;

    [ObservableProperty]
    private string watermarkText = string.Empty;

    public ObservableCollection<string> AspectRatioOptions { get; } = new()
    {
        "16:9",
        "1:1",
        "4:5",
        "9:16"
    };

    public ObservableCollection<string> CodecOptions { get; } = new()
    {
        "libx265",
        "libx264"
    };

    public EditVideoViewModel()
        : this("ffmpeg")
    {
    }

    public EditVideoViewModel(string ffmpegPath)
    {
        _uiDispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _obfuscator = new VideoObfuscator(ffmpegPath);
        _obfuscator.ProgressChanged += OnProgressChanged;
        _obfuscator.LogMessage += OnLogMessage;

        // Set default output path
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        OutputVideoPath = Path.Combine(desktopPath, "video_processed.mp4");

        AddLogMessage("Edit Video initialized. Ready to process video files.");
    }

    [RelayCommand]
    public void SelectInputVideo()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Video files (*.mp4;*.avi;*.mov;*.mkv)|*.mp4;*.avi;*.mov;*.mkv|All files (*.*)|*.*",
            Title = "Select Input Video"
        };

        if (dialog.ShowDialog() == true)
        {
            InputVideoPath = dialog.FileName;
            AddLogMessage($"Selected input: {Path.GetFileName(InputVideoPath)}");
        }
    }

    [RelayCommand]
    public void SelectOutputVideo()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "MP4 Video (*.mp4)|*.mp4|All files (*.*)|*.*",
            Title = "Select Output Video Location",
            FileName = "video_processed.mp4"
        };

        if (dialog.ShowDialog() == true)
        {
            OutputVideoPath = dialog.FileName;
            AddLogMessage($"Selected output: {Path.GetFileName(OutputVideoPath)}");
        }
    }

    [RelayCommand]
    public void SelectMusicFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Audio files (*.mp3;*.wav;*.aac;*.flac)|*.mp3;*.wav;*.aac;*.flac|All files (*.*)|*.*",
            Title = "Select Background Music"
        };

        if (dialog.ShowDialog() == true)
        {
            MusicFilePath = dialog.FileName;
            AddLogMessage($"Selected music: {Path.GetFileName(MusicFilePath)}");
        }
    }

    [RelayCommand]
    public async Task ProcessVideoAsync()
    {
        if (string.IsNullOrEmpty(InputVideoPath) || !File.Exists(InputVideoPath))
        {
            AddLogMessage("[ERROR] Please select a valid input video file");
            return;
        }

        if (string.IsNullOrEmpty(OutputVideoPath))
        {
            AddLogMessage("[ERROR] Please specify an output video path");
            return;
        }

        try
        {
            IsProcessing = true;
            ProgressValue = 0;
            ProgressText = "Starting video processing...";
            ClearLog();

            _cancellationTokenSource = new CancellationTokenSource();

            var options = new VideoObfuscator.ProcessingOptions
            {
                RemoveMetadata = RemoveMetadata,
                ChangeAspectRatio = ChangeAspectRatio && AspectRatio != "16:9",
                AspectRatio = AspectRatio,
                HorizontalFlip = HorizontalFlip,
                AdjustSpeed = AdjustSpeed,
                SpeedMultiplier = SpeedMultiplier,
                AdjustColors = AdjustColors,
                Brightness = Brightness,
                Contrast = Contrast,
                Saturation = Saturation,
                ShiftPitch = ShiftPitch,
                PitchShift = PitchShift,
                ReplaceMusic = ReplaceMusic && !string.IsNullOrEmpty(MusicFilePath),
                MusicfilePath = MusicFilePath,
                ReencodeVideo = ReencodeVideo,
                Codec = Codec,
                CrfValue = CrfValue,
                GopSize = GopSize,
                AddWatermark = AddWatermark && !string.IsNullOrEmpty(WatermarkText),
                WatermarkText = WatermarkText
            };

            var progress = new Progress<int>(value =>
            {
                ProgressValue = value;
            });

            var success = await Task.Run(() =>
                _obfuscator.ProcessVideoAsync(
                    InputVideoPath,
                    OutputVideoPath,
                    options,
                    progress,
                    _cancellationTokenSource.Token),
                _cancellationTokenSource.Token);

            if (success)
            {
                ProgressText = "✓ Video processing completed successfully!";
                ProgressValue = 100;
                AddLogMessage("[SUCCESS] Video processing completed! Output saved to: " + OutputVideoPath);
            }
            else
            {
                ProgressText = "✗ Video processing failed";
                AddLogMessage("[ERROR] Video processing failed. Please check logs above.");
            }
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Processing cancelled";
            AddLogMessage("[INFO] Video processing has been cancelled");
        }
        catch (Exception ex)
        {
            ProgressText = "Error occurred";
            AddLogMessage($"[ERROR] Unexpected error: {ex.Message}");
        }
        finally
        {
            IsProcessing = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    [RelayCommand]
    public void CancelProcessing()
    {
        if (_cancellationTokenSource != null && !_cancellationTokenSource.Token.IsCancellationRequested)
        {
            _cancellationTokenSource.Cancel();
            AddLogMessage("[INFO] Cancellation requested...");
        }
    }

    [RelayCommand]
    public void PreviewVideo()
    {
        if (string.IsNullOrEmpty(OutputVideoPath) || !File.Exists(OutputVideoPath))
        {
            AddLogMessage("[ERROR] Output video does not exist yet. Please process the video first.");
            return;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = OutputVideoPath,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
            AddLogMessage("Opening video in default player...");
        }
        catch (Exception ex)
        {
            AddLogMessage($"[ERROR] Failed to open video: {ex.Message}");
        }
    }

    [RelayCommand]
    public void ResetSettings()
    {
        RemoveMetadata = true;
        ChangeAspectRatio = true;
        AspectRatio = "16:9";
        HorizontalFlip = true;
        AdjustSpeed = true;
        SpeedMultiplier = 0.98;
        AdjustColors = true;
        Brightness = 0.05;
        Contrast = 1.05;
        Saturation = 1.1;
        ShiftPitch = true;
        PitchShift = -0.5;
        ReplaceMusic = false;
        MusicFilePath = string.Empty;
        ReencodeVideo = true;
        Codec = "libx265";
        CrfValue = 22;
        GopSize = 250;
        AddWatermark = false;
        WatermarkText = string.Empty;

        AddLogMessage("Settings reset to defaults");
    }

    [RelayCommand]
    public void EnableAllSteps()
    {
        RemoveMetadata = true;
        ChangeAspectRatio = true;
        HorizontalFlip = true;
        AdjustSpeed = true;
        AdjustColors = true;
        ShiftPitch = true;
        ReplaceMusic = !string.IsNullOrEmpty(MusicFilePath);
        ReencodeVideo = true;
        AddWatermark = !string.IsNullOrEmpty(WatermarkText);

        AddLogMessage("All available steps enabled");
    }

    [RelayCommand]
    public void DisableAllSteps()
    {
        RemoveMetadata = false;
        ChangeAspectRatio = false;
        HorizontalFlip = false;
        AdjustSpeed = false;
        AdjustColors = false;
        ShiftPitch = false;
        ReplaceMusic = false;
        ReencodeVideo = false;
        AddWatermark = false;

        AddLogMessage("All steps disabled");
    }

    [RelayCommand]
    public void ClearLog()
    {
        RunOnUiThread(() => LogMessages.Clear());
    }

    private void OnProgressChanged(int stepNumber, string stepName, int totalSteps)
    {
        RunOnUiThread(() =>
        {
            ProgressValue = (int)((stepNumber / (double)totalSteps) * 100);
            ProgressText = $"Processing: {stepName} ({stepNumber}/{totalSteps})";
        });
    }

    private void OnLogMessage(string message)
    {
        AddLogMessage(message);
    }

    private void AddLogMessage(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var formattedMessage = $"[{timestamp}] {message}";

        RunOnUiThread(() =>
        {
            LogMessages.Add(formattedMessage);

            // Keep only last 500 messages to avoid memory issues
            while (LogMessages.Count > 500)
            {
                LogMessages.RemoveAt(0);
            }
        });
    }

    private void RunOnUiThread(Action action)
    {
        if (_uiDispatcher.CheckAccess())
        {
            action();
            return;
        }

        _uiDispatcher.Invoke(action);
    }
}
