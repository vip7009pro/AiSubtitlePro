using AiSubtitlePro.Core.Models;
using AiSubtitlePro.Core.Parsers;
using AiSubtitlePro.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

using AiSubtitlePro.Infrastructure.Media;
using System.Globalization;
using System.Text;

namespace AiSubtitlePro.ViewModels;

/// <summary>
/// Main ViewModel for the application
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly AssParser _assParser = new();
    private readonly SrtParser _srtParser = new();
    private readonly VttParser _vttParser = new();
    private readonly FFmpegService _ffmpegService = new();

    [ObservableProperty]
    private SubtitleDocument? _currentDocument;

    [ObservableProperty]
    private SubtitleLine? _selectedLine;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private TimeSpan _currentPosition = TimeSpan.Zero;

    [ObservableProperty]
    private TimeSpan _mediaDuration = TimeSpan.Zero;

    [ObservableProperty]
    private string? _mediaFilePath;

    [ObservableProperty]
    private string? _waveformAudioPath;

    [ObservableProperty]
    private double _volume = 1.0;

    [ObservableProperty]
    private bool _isDarkTheme = true;

    /// <summary>
    /// Lines currently displayed in the grid (for filtering/search)
    /// </summary>
    public ObservableCollection<SubtitleLine> DisplayedLines { get; } = new();

    /// <summary>
    /// Recently opened files
    /// </summary>
    public ObservableCollection<string> RecentFiles { get; } = new();

    private SubtitleLine? _selectedLineHook;

    private bool _isSyncingSelectedStyle;

    [ObservableProperty]
    private string? _activeSubtitleText;

    [ObservableProperty]
    private string? _activeSubtitleAss;

    [ObservableProperty]
    private double _selectedStyleFontSize;

    [ObservableProperty]
    private string _selectedStylePrimaryAssColor = "&H00FFFFFF";

    [ObservableProperty]
    private string _selectedStyleOutlineAssColor = "&H00000000";

    [ObservableProperty]
    private string _selectedStyleBackAssColor = "&H80000000";

    [ObservableProperty]
    private double _selectedStyleOutline = 2;

    [ObservableProperty]
    private bool _selectedStyleBoxEnabled;

    public MainViewModel()
    {
        // Create new document on startup
        NewDocument();
    }

    [RelayCommand]
    private void PlayPause()
    {
        IsPlaying = !IsPlaying;
    }

    [RelayCommand]
    private void ApplyStyleToAll()
    {
        if (CurrentDocument == null || SelectedLine == null) return;
        
        var styleName = SelectedLine.StyleName;
        foreach (var line in CurrentDocument.Lines)
        {
            line.StyleName = styleName;
        }
        CurrentDocument.IsDirty = true;
        StatusMessage = $"Applied style '{styleName}' to all lines";
    }

    partial void OnCurrentPositionChanged(TimeSpan value)
    {
        if (CurrentDocument != null)
        {
            // Stable overlap rule (Aegisub-style): Start inclusive, End exclusive.
            // This avoids flickering/alternating visibility when scrubbing on boundaries.
            var activeLines = CurrentDocument.Lines
                .Where(l =>
                {
                    if (l.End <= l.Start)
                    {
                        // Degenerate/invalid line: treat as a point event with a tiny tolerance.
                        var tol = TimeSpan.FromMilliseconds(1);
                        return value >= (l.Start - tol) && value <= (l.End + tol);
                    }

                    return value >= l.Start && value < l.End;
                })
                .ToList();

            ActiveSubtitleText = activeLines.Count > 0 
                ? string.Join("\n", activeLines.Select(l => l.Text)) 
                : string.Empty;

            ActiveSubtitleAss = BuildAssForPreview(CurrentDocument, activeLines);
        }
    }

    partial void OnSelectedLineChanged(SubtitleLine? value)
    {
        if (_selectedLineHook != null)
            _selectedLineHook.PropertyChanged -= SelectedLine_PropertyChanged;

        _selectedLineHook = value;
        if (_selectedLineHook != null)
            _selectedLineHook.PropertyChanged += SelectedLine_PropertyChanged;

        SyncSelectedStyleFromLine();
        RefreshSubtitlePreview();
    }

    private void SelectedLine_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Ensure edits (text/style name/pos/timing) reflect immediately on preview
        if (e.PropertyName == nameof(SubtitleLine.Text)
            || e.PropertyName == nameof(SubtitleLine.StyleName)
            || e.PropertyName == nameof(SubtitleLine.PosX)
            || e.PropertyName == nameof(SubtitleLine.PosY)
            || e.PropertyName == nameof(SubtitleLine.Start)
            || e.PropertyName == nameof(SubtitleLine.End))
        {
            RefreshSubtitlePreview();
        }
    }

    public void RefreshSubtitlePreview()
    {
        OnCurrentPositionChanged(CurrentPosition);
    }

    partial void OnSelectedStyleFontSizeChanged(double value)
    {
        if (_isSyncingSelectedStyle) return;
        UpdateSelectedStyle(s => s.FontSize = value);
    }

    partial void OnSelectedStylePrimaryAssColorChanged(string value)
    {
        if (_isSyncingSelectedStyle) return;
        UpdateSelectedStyle(s => s.PrimaryColor = SubtitleStyle.AssToColor(value));
    }

    partial void OnSelectedStyleOutlineAssColorChanged(string value)
    {
        if (_isSyncingSelectedStyle) return;
        UpdateSelectedStyle(s => s.OutlineColor = SubtitleStyle.AssToColor(value));
    }

    partial void OnSelectedStyleBackAssColorChanged(string value)
    {
        if (_isSyncingSelectedStyle) return;
        UpdateSelectedStyle(s => s.BackColor = SubtitleStyle.AssToColor(value));
    }

    partial void OnSelectedStyleOutlineChanged(double value)
    {
        if (_isSyncingSelectedStyle) return;
        UpdateSelectedStyle(s => s.Outline = value);
    }

    partial void OnSelectedStyleBoxEnabledChanged(bool value)
    {
        if (_isSyncingSelectedStyle) return;
        UpdateSelectedStyle(s => s.BorderStyle = value ? 3 : 1);
    }

    private void SyncSelectedStyleFromLine()
    {
        var doc = CurrentDocument;
        var line = SelectedLine;
        if (doc == null || line == null) return;

        var style = doc.GetStyle(line.StyleName);
        _isSyncingSelectedStyle = true;
        try
        {
            SelectedStyleFontSize = style.FontSize;
            SelectedStyleOutline = style.Outline;
            SelectedStylePrimaryAssColor = SubtitleStyle.ColorToAss(style.PrimaryColor);
            SelectedStyleOutlineAssColor = SubtitleStyle.ColorToAss(style.OutlineColor);
            SelectedStyleBackAssColor = SubtitleStyle.ColorToAss(style.BackColor);
            SelectedStyleBoxEnabled = style.BorderStyle == 3;
        }
        finally
        {
            _isSyncingSelectedStyle = false;
        }
    }

    private void UpdateSelectedStyle(Action<SubtitleStyle> mutator)
    {
        var doc = CurrentDocument;
        var line = SelectedLine;
        if (doc == null || line == null) return;

        var style = doc.Styles.FirstOrDefault(s => s.Name == line.StyleName);
        if (style == null)
        {
            style = new SubtitleStyle { Name = line.StyleName };
            doc.Styles.Add(style);
        }

        mutator(style);
        doc.IsDirty = true;

        // Refresh preview for current position
        OnCurrentPositionChanged(CurrentPosition);
    }

    private static string BuildAssForPreview(SubtitleDocument doc, List<SubtitleLine> activeLines)
    {
        if (activeLines.Count == 0)
            return string.Empty;

        var stylesToInclude = activeLines
            .Select(l => l.StyleName)
            .Distinct(StringComparer.Ordinal)
            .Select(doc.GetStyle)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine($"PlayResX: {doc.ScriptInfo.PlayResX}");
        sb.AppendLine($"PlayResY: {doc.ScriptInfo.PlayResY}");
        sb.AppendLine("WrapStyle: 0");
        sb.AppendLine("ScaledBorderAndShadow: yes");
        sb.AppendLine();

        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        foreach (var st in stylesToInclude)
        {
            // Force box mode if desired by style.BorderStyle
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "Style: {0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17},{18},{19},{20},{21},{22}",
                st.Name,
                st.FontName,
                st.FontSize,
                SubtitleStyle.ColorToAss(st.PrimaryColor),
                SubtitleStyle.ColorToAss(st.SecondaryColor),
                SubtitleStyle.ColorToAss(st.OutlineColor),
                SubtitleStyle.ColorToAss(st.BackColor),
                st.Bold ? -1 : 0,
                st.Italic ? -1 : 0,
                st.Underline ? -1 : 0,
                st.StrikeOut ? -1 : 0,
                st.ScaleX,
                st.ScaleY,
                st.Spacing,
                st.Angle,
                st.BorderStyle,
                st.Outline,
                st.Shadow,
                st.Alignment,
                st.MarginL,
                st.MarginR,
                st.MarginV,
                st.Encoding));
        }

        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        foreach (var l in activeLines)
        {
            var text = (l.Text ?? string.Empty)
                .Replace("\r\n", "\\N")
                .Replace("\n", "\\N")
                .Replace("\r", "\\N");

            var overrideTags = string.Empty;
            if (l.PosX.HasValue && l.PosY.HasValue)
            {
                overrideTags = $"{{\\pos({l.PosX.Value},{l.PosY.Value})}}";
            }

            // For preview, we keep dialogue always-on.
            sb.AppendLine($"Dialogue: {l.Layer},0:00:00.00,9:59:59.99,{l.StyleName},{l.Actor},{l.MarginL},{l.MarginR},{l.MarginV},{l.Effect},{overrideTags}{text}");
        }

        return sb.ToString();
    }

    #region File Commands

    [RelayCommand]
    private void NewDocument()
    {
        if (!ConfirmDiscardChanges()) return;

        CurrentDocument = SubtitleDocument.CreateNew();
        RefreshDisplayedLines();
        StatusMessage = "New document created";
    }

    [RelayCommand]
    private async Task OpenSubtitle()
    {
        if (!ConfirmDiscardChanges()) return;

        var dialog = new OpenFileDialog
        {
            Filter = "All Subtitle Files|*.ass;*.ssa;*.srt;*.vtt|" +
                    "ASS/SSA Files|*.ass;*.ssa|" +
                    "SRT Files|*.srt|" +
                    "VTT Files|*.vtt|" +
                    "All Files|*.*",
            Title = "Open Subtitle File"
        };

        if (dialog.ShowDialog() == true)
        {
            await LoadSubtitleAsync(dialog.FileName);
        }
    }

    [RelayCommand]
    private async Task OpenMedia()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm|" +
                    "Audio Files|*.mp3;*.wav;*.flac;*.aac;*.m4a|" +
                    "All Files|*.*",
            Title = "Open Media File"
        };

        if (dialog.ShowDialog() == true)
        {
            MediaFilePath = dialog.FileName;
            StatusMessage = $"Loaded media: {Path.GetFileName(dialog.FileName)}";
            await GenerateWaveformAsync(MediaFilePath);
        }
    }

    private async Task GenerateWaveformAsync(string videoPath)
    {
        try 
        {
            StatusMessage = "Extracting audio for waveform...";
            var tempDir = Path.Combine(Path.GetTempPath(), "AiSubtitlePro");
            Directory.CreateDirectory(tempDir);
            
            var wavPath = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(videoPath) + ".wav");
            
            // Extract if not exists or if checking logic needed. 
            // For now, simple extract.
            if (!File.Exists(wavPath))
            {
               await _ffmpegService.ExtractAudioAsync(videoPath, wavPath);
            }
            
            WaveformAudioPath = wavPath;
            StatusMessage = "Ready";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Waveform error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Save()
    {
        if (CurrentDocument == null) return;

        if (string.IsNullOrEmpty(CurrentDocument.FilePath))
        {
            await SaveAs();
            return;
        }

        await SaveSubtitleAsync(CurrentDocument.FilePath);
    }

    [RelayCommand]
    private async Task SaveAs()
    {
        if (CurrentDocument == null) return;

        var dialog = new SaveFileDialog
        {
            Filter = "ASS File|*.ass|SRT File|*.srt|VTT File|*.vtt",
            Title = "Save Subtitle File",
            FileName = CurrentDocument.FileName
        };

        if (dialog.ShowDialog() == true)
        {
            await SaveSubtitleAsync(dialog.FileName);
        }
    }

    private async Task LoadSubtitleAsync(string filePath)
    {
        try
        {
            StatusMessage = $"Loading {Path.GetFileName(filePath)}...";

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            ISubtitleParser parser = ext switch
            {
                ".ass" or ".ssa" => _assParser,
                ".srt" => _srtParser,
                ".vtt" => _vttParser,
                _ => _assParser
            };

            CurrentDocument = await parser.ParseFileAsync(filePath);
            RefreshDisplayedLines();

            AddRecentFile(filePath);
            StatusMessage = $"Loaded {CurrentDocument.Lines.Count} lines from {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading file: {ex.Message}";
            MessageBox.Show($"Failed to load subtitle file:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveSubtitleAsync(string filePath)
    {
        if (CurrentDocument == null) return;

        try
        {
            StatusMessage = "Saving...";

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            ISubtitleParser parser = ext switch
            {
                ".ass" or ".ssa" => _assParser,
                ".srt" => _srtParser,
                ".vtt" => _vttParser,
                _ => _assParser
            };

            await parser.SaveFileAsync(CurrentDocument, filePath);
            StatusMessage = $"Saved to {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error saving file: {ex.Message}";
            MessageBox.Show($"Failed to save subtitle file:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion

    #region Edit Commands

    [RelayCommand]
    private void AddLine()
    {
        if (CurrentDocument == null) return;

        var newLine = new SubtitleLine
        {
            Start = CurrentPosition,
            End = CurrentPosition + TimeSpan.FromSeconds(2),
            Text = ""
        };

        // Insert after selected line or at end
        var insertIndex = SelectedLine != null 
            ? CurrentDocument.Lines.IndexOf(SelectedLine) + 1 
            : CurrentDocument.Lines.Count;

        CurrentDocument.Lines.Insert(insertIndex, newLine);
        CurrentDocument.ReindexLines();
        CurrentDocument.IsDirty = true;

        RefreshDisplayedLines();
        SelectedLine = newLine;
        StatusMessage = "Added new line";
    }

    [RelayCommand]
    private void DeleteSelectedLines()
    {
        if (CurrentDocument == null || SelectedLine == null) return;

        var selectedLines = DisplayedLines.Where(l => l.IsSelected).ToList();
        if (selectedLines.Count == 0 && SelectedLine != null)
        {
            selectedLines.Add(SelectedLine);
        }

        foreach (var line in selectedLines)
        {
            CurrentDocument.Lines.Remove(line);
        }

        CurrentDocument.ReindexLines();
        CurrentDocument.IsDirty = true;
        RefreshDisplayedLines();

        StatusMessage = $"Deleted {selectedLines.Count} line(s)";
    }

    [RelayCommand]
    private void SplitLine()
    {
        if (CurrentDocument == null || SelectedLine == null) return;

        var line = SelectedLine;
        var midTime = line.Start + (line.Duration / 2);

        // Create second half
        var newLine = line.Clone();
        newLine.Start = midTime;

        // Update first half
        line.End = midTime;

        // Insert new line
        var index = CurrentDocument.Lines.IndexOf(line);
        CurrentDocument.Lines.Insert(index + 1, newLine);
        CurrentDocument.ReindexLines();
        CurrentDocument.IsDirty = true;

        RefreshDisplayedLines();
        StatusMessage = "Line split";
    }

    [RelayCommand]
    private void MergeWithNext()
    {
        if (CurrentDocument == null || SelectedLine == null) return;

        var index = CurrentDocument.Lines.IndexOf(SelectedLine);
        if (index >= CurrentDocument.Lines.Count - 1) return;

        var nextLine = CurrentDocument.Lines[index + 1];
        
        // Merge text
        SelectedLine.Text = $"{SelectedLine.Text}\\N{nextLine.Text}";
        SelectedLine.End = nextLine.End;

        // Remove next line
        CurrentDocument.Lines.RemoveAt(index + 1);
        CurrentDocument.ReindexLines();
        CurrentDocument.IsDirty = true;

        RefreshDisplayedLines();
        StatusMessage = "Lines merged";
    }

    #endregion

    #region Timing Commands

    [RelayCommand]
    private void ShiftTiming()
    {
        if (CurrentDocument == null) return;
        ShiftTimingDialog.ShowAndApply(CurrentDocument, SelectedLine);
        RefreshDisplayedLines();
        StatusMessage = "Timing shifted";
    }

    [RelayCommand]
    private void SetStartTime()
    {
        if (SelectedLine == null) return;
        SelectedLine.Start = CurrentPosition;
        CurrentDocument!.IsDirty = true;
        StatusMessage = $"Start time set to {FormatTime(CurrentPosition)}";
    }

    [RelayCommand]
    private void SetEndTime()
    {
        if (SelectedLine == null) return;
        SelectedLine.End = CurrentPosition;
        CurrentDocument!.IsDirty = true;
        StatusMessage = $"End time set to {FormatTime(CurrentPosition)}";
    }

    #endregion

    #region Playback Commands



    [RelayCommand]
    private void Stop()
    {
        IsPlaying = false;
        CurrentPosition = TimeSpan.Zero;
    }

    [RelayCommand]
    private void SeekToLine()
    {
        if (SelectedLine != null)
        {
            CurrentPosition = SelectedLine.Start;
        }
    }

    #endregion

    #region AI Commands

    [RelayCommand]
    private async Task TranscribeAudio()
    {
        var wizard = new TranscriptionWizard();
        wizard.Owner = Application.Current.MainWindow;

        if (wizard.ShowDialog() == true && wizard.Result != null)
        {
            CurrentDocument = wizard.Result;
            RefreshDisplayedLines();
            StatusMessage = $"Transcription complete: {CurrentDocument.Lines.Count} lines generated";
        }
    }

    [RelayCommand]
    private async Task TranslateSubtitles()
    {
        if (CurrentDocument == null || CurrentDocument.Lines.Count == 0)
        {
            MessageBox.Show("Please load or create subtitles first.", "No Subtitles",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new TranslationDialog(CurrentDocument);
        dialog.Owner = Application.Current.MainWindow;

        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            CurrentDocument = dialog.Result;
            RefreshDisplayedLines();
            StatusMessage = "Translation complete";
        }
    }

    #endregion

    #region View Commands

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        // Theme will be applied by the view
    }

    #endregion

    #region Helpers

    private void RefreshDisplayedLines()
    {
        DisplayedLines.Clear();
        if (CurrentDocument != null)
        {
            foreach (var line in CurrentDocument.Lines)
            {
                DisplayedLines.Add(line);
            }
        }
    }

    private bool ConfirmDiscardChanges()
    {
        if (CurrentDocument?.IsDirty != true) return true;

        var result = MessageBox.Show(
            "You have unsaved changes. Do you want to save before continuing?",
            "Unsaved Changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            Save().Wait();
            return true;
        }

        return result == MessageBoxResult.No;
    }

    private void AddRecentFile(string filePath)
    {
        RecentFiles.Remove(filePath);
        RecentFiles.Insert(0, filePath);
        
        // Keep only last 10
        while (RecentFiles.Count > 10)
        {
            RecentFiles.RemoveAt(RecentFiles.Count - 1);
        }
    }

    private static string FormatTime(TimeSpan time)
    {
        return $"{(int)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}.{time.Milliseconds / 10:D2}";
    }

    #endregion
}
