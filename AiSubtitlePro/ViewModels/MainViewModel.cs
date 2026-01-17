using AiSubtitlePro.Core.Models;
using AiSubtitlePro.Core.Parsers;
using AiSubtitlePro.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Diagnostics;
using System.Threading;

using AiSubtitlePro.Infrastructure.Media;
using System.Globalization;
using System.Text;
using AiSubtitlePro.Services;

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
    private readonly UndoRedoManager _undo = new();

    private enum FfmpegOperation
    {
        None,
        Waveform,
        ExportHardSub
    }

    private FfmpegOperation _ffmpegOperation = FfmpegOperation.None;
    private readonly SemaphoreSlim _ffmpegGate = new(1, 1);
    private CancellationTokenSource? _exportCts;

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
    private TimeSpan _mediaDurationAbs = TimeSpan.Zero;

    [ObservableProperty]
    private TimeSpan _cutStartAbs = TimeSpan.Zero;

    [ObservableProperty]
    private TimeSpan _cutEndAbs = TimeSpan.Zero;

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

    [ObservableProperty]
    private bool _canUndo;

    [ObservableProperty]
    private bool _canRedo;

    [ObservableProperty]
    private bool _isFfmpegBusy;

    [ObservableProperty]
    private int _ffmpegProgressPercent;

    [ObservableProperty]
    private string _ffmpegProgressText = string.Empty;

    [ObservableProperty]
    private bool _isExportingHardSub;

    private SubtitleLine? _selectedLineHook;

    private bool _isSyncingSelectedStyle;

    [ObservableProperty]
    private string? _activeSubtitleText;

    [ObservableProperty]
    private string? _activeSubtitleAss;

    private SubtitleDocument? _cutSourceDocument;

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
        _ffmpegService.ProgressChanged += (_, p) =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            dispatcher.BeginInvoke(() =>
            {
                if (_ffmpegOperation == FfmpegOperation.None)
                    return;

                FfmpegProgressPercent = p.ProgressPercent;
                FfmpegProgressText = string.IsNullOrWhiteSpace(p.Status) ? "Processing" : p.Status;

                if (_ffmpegOperation == FfmpegOperation.Waveform)
                {
                    StatusMessage = p.ProgressPercent > 0 || p.TotalDuration > TimeSpan.Zero
                        ? $"Extracting audio for waveform... {p.ProgressPercent}%"
                        : (string.IsNullOrWhiteSpace(p.Status) ? "Extracting audio for waveform..." : p.Status);
                }
                else if (_ffmpegOperation == FfmpegOperation.ExportHardSub)
                {
                    StatusMessage = p.ProgressPercent > 0 || p.TotalDuration > TimeSpan.Zero
                        ? $"Exporting hard-sub video... {p.ProgressPercent}%"
                        : (string.IsNullOrWhiteSpace(p.Status) ? "Exporting hard-sub video..." : p.Status);
                }
            });
        };

        _undo.StateChanged += (_, __) =>
        {
            CanUndo = _undo.CanUndo;
            CanRedo = _undo.CanRedo;
        };

        // Create new document on startup
        NewDocument();

        CanUndo = _undo.CanUndo;
        CanRedo = _undo.CanRedo;
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        _undo.Undo();
        RefreshDisplayedLines();
        RefreshSubtitlePreview();
        if (CurrentDocument != null) CurrentDocument.IsDirty = true;
        StatusMessage = string.IsNullOrWhiteSpace(_undo.RedoDescription) ? "Undone" : $"Undone: {_undo.RedoDescription}";
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        _undo.Redo();
        RefreshDisplayedLines();
        RefreshSubtitlePreview();
        if (CurrentDocument != null) CurrentDocument.IsDirty = true;
        StatusMessage = string.IsNullOrWhiteSpace(_undo.UndoDescription) ? "Redone" : $"Redone: {_undo.UndoDescription}";
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

        var doc = CurrentDocument;
        var styleName = SelectedLine.StyleName;
        var old = doc.Lines.Select(l => (l, l.StyleName)).ToList();

        _undo.Execute(new DelegateCommand(
            "Apply Style To All",
            execute: () =>
            {
                foreach (var line in doc.Lines)
                    line.StyleName = styleName;
                doc.IsDirty = true;
            },
            undo: () =>
            {
                foreach (var (line, prev) in old)
                    line.StyleName = prev;
                doc.IsDirty = true;
            }));

        RefreshSubtitlePreview();
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

    public TimeSpan ToMediaTime(TimeSpan timelineTime)
    {
        var abs = timelineTime + CutStartAbs;
        if (abs < TimeSpan.Zero) abs = TimeSpan.Zero;
        var endAbs = CutEndAbs;
        if (endAbs <= TimeSpan.Zero || endAbs > MediaDurationAbs) endAbs = MediaDurationAbs;
        if (endAbs > TimeSpan.Zero && abs > endAbs) abs = endAbs;
        return abs;
    }

    public TimeSpan ToTimelineTime(TimeSpan mediaTime)
    {
        var rel = mediaTime - CutStartAbs;
        if (rel < TimeSpan.Zero) rel = TimeSpan.Zero;
        var end = GetTimelineDuration(MediaDurationAbs);
        if (end > TimeSpan.Zero && rel > end) rel = end;
        return rel;
    }

    public TimeSpan GetTimelineDuration(TimeSpan mediaDuration)
    {
        var endAbs = CutEndAbs;
        if (endAbs <= TimeSpan.Zero || endAbs > mediaDuration) endAbs = mediaDuration;
        var startAbs = CutStartAbs;
        if (startAbs < TimeSpan.Zero) startAbs = TimeSpan.Zero;
        if (startAbs > endAbs) startAbs = endAbs;
        return endAbs - startAbs;
    }

    private TimeSpan ClampTimelineTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var end = MediaDuration;
        if (end > TimeSpan.Zero && t > end) t = end;
        return t;
    }

    private void ApplyCutToDocument(TimeSpan startAbs, TimeSpan endAbs)
    {
        if (CurrentDocument == null) return;

        var source = _cutSourceDocument ?? CurrentDocument;
        var mediaDur = MediaDurationAbs;
        if (mediaDur <= TimeSpan.Zero)
            return;

        if (startAbs < TimeSpan.Zero) startAbs = TimeSpan.Zero;
        if (endAbs <= TimeSpan.Zero || endAbs > mediaDur) endAbs = mediaDur;
        if (endAbs < startAbs) endAbs = startAbs;

        // Ensure we always keep an immutable-ish source snapshot for repeated cuts.
        _cutSourceDocument ??= source.Clone();

        var cutDoc = source.Clone();
        cutDoc.Lines.Clear();

        foreach (var line in source.Lines)
        {
            if (line.End <= startAbs) continue;
            if (line.Start >= endAbs) continue;

            var shifted = line.Clone();
            shifted.Start = shifted.Start - startAbs;
            shifted.End = shifted.End - startAbs;
            if (shifted.Start < TimeSpan.Zero) shifted.Start = TimeSpan.Zero;
            if (shifted.End < shifted.Start) shifted.End = shifted.Start;
            cutDoc.Lines.Add(shifted);
        }

        cutDoc.ReindexLines();
        CurrentDocument = cutDoc;
        RefreshDisplayedLines();

        CutStartAbs = startAbs;
        CutEndAbs = endAbs;
        MediaDuration = GetTimelineDuration(MediaDurationAbs);
        CurrentPosition = TimeSpan.Zero;

        RefreshSubtitlePreview();
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

        var prevStyleName = line.StyleName;
        var prevStyles = doc.Styles.Select(s => (s, s.Clone())).ToList();

        _undo.Execute(new DelegateCommand(
            "Edit Style",
            execute: () =>
            {
                var style = EnsureLineHasEditableStyle(doc, line);
                mutator(style);
                doc.IsDirty = true;
            },
            undo: () =>
            {
                doc.Styles.Clear();
                foreach (var (_, clone) in prevStyles)
                    doc.Styles.Add(clone);
                line.StyleName = prevStyleName;
                doc.IsDirty = true;
            }));

        OnCurrentPositionChanged(CurrentPosition);
    }

    public void CommitSelectedLineTextEdit(string? oldText, string? newText)
    {
        if (SelectedLine == null || CurrentDocument == null) return;
        if (oldText == newText) return;

        var doc = CurrentDocument;
        var line = SelectedLine;
        var prev = oldText ?? string.Empty;
        var next = newText ?? string.Empty;

        _undo.Execute(new DelegateCommand(
            "Edit Text",
            execute: () => { line.Text = next; doc.IsDirty = true; },
            undo: () => { line.Text = prev; doc.IsDirty = true; }));

        RefreshSubtitlePreview();
    }

    public void SetSelectedLinePosition(int x, int y)
    {
        if (SelectedLine == null || CurrentDocument == null) return;
        var doc = CurrentDocument;
        var line = SelectedLine;
        var oldX = line.PosX;
        var oldY = line.PosY;

        _undo.Execute(new DelegateCommand(
            "Set Position",
            execute: () => { line.PosX = x; line.PosY = y; doc.IsDirty = true; },
            undo: () => { line.PosX = oldX; line.PosY = oldY; doc.IsDirty = true; }));

        RefreshSubtitlePreview();
    }

    private static SubtitleStyle EnsureLineHasEditableStyle(SubtitleDocument doc, SubtitleLine line)
    {
        var currentName = string.IsNullOrWhiteSpace(line.StyleName) ? "Default" : line.StyleName;
        var existing = doc.Styles.FirstOrDefault(s => s.Name == currentName);

        if (existing == null)
        {
            existing = new SubtitleStyle { Name = currentName };
            doc.Styles.Add(existing);
            line.StyleName = currentName;
            return existing;
        }

        var usageCount = doc.Lines.Count(l => string.Equals(l.StyleName, currentName, StringComparison.Ordinal));
        if (usageCount <= 1)
            return existing;

        // Clone to unique name for this line
        var baseName = currentName;
        var uniqueName = $"{baseName}__L{line.Index}";
        var suffix = 1;
        while (doc.Styles.Any(s => string.Equals(s.Name, uniqueName, StringComparison.Ordinal)))
        {
            uniqueName = $"{baseName}__L{line.Index}_{suffix}";
            suffix++;
        }

        var clone = existing.Clone();
        clone.Name = uniqueName;
        doc.Styles.Add(clone);
        line.StyleName = uniqueName;
        return clone;
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

            // Reset cut range when opening new media.
            CutStartAbs = TimeSpan.Zero;
            CutEndAbs = TimeSpan.Zero;
            _cutSourceDocument = null;

            await GenerateWaveformAsync(MediaFilePath);
        }
    }

    [RelayCommand]
    private void CutVideo()
    {
        if (string.IsNullOrWhiteSpace(MediaFilePath) || !File.Exists(MediaFilePath))
        {
            MessageBox.Show("Please open a media file first.", "Cut Video", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MediaDurationAbs <= TimeSpan.Zero)
        {
            MessageBox.Show("Media is not ready yet. Please wait for the video to finish loading, then try again.", "Cut Video", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (CurrentDocument == null)
        {
            MessageBox.Show("Please open a subtitle file first.", "Cut Video", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(WaveformAudioPath) || !File.Exists(WaveformAudioPath))
        {
            _ = GenerateWaveformAsync(MediaFilePath);
            MessageBox.Show("Waveform is not ready yet. Please wait a moment and try again.", "Cut Video", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var wnd = new CutVideoWindow(
            mediaPath: MediaFilePath,
            mediaDuration: MediaDurationAbs,
            startAbs: CutStartAbs,
            endAbs: CutEndAbs,
            currentAbs: ToMediaTime(CurrentPosition));

        wnd.Owner = Application.Current.MainWindow;
        wnd.DataContext = this;
        if (wnd.ShowDialog() != true)
            return;

        ApplyCutToDocument(wnd.StartAbs, wnd.EndAbs);
        var effectiveEndAbs = wnd.EndAbs;
        if (effectiveEndAbs <= TimeSpan.Zero || effectiveEndAbs > MediaDurationAbs) effectiveEndAbs = MediaDurationAbs;
        StatusMessage = $"Cut range set: {FormatTime(TimeSpan.Zero)} - {FormatTime(effectiveEndAbs - wnd.StartAbs)}";
    }

    private async Task GenerateWaveformAsync(string videoPath)
    {
        var acquired = await _ffmpegGate.WaitAsync(0);
        if (!acquired)
        {
            if (IsExportingHardSub)
                StatusMessage = "Exporting... (waveform extraction skipped)";
            return;
        }
        var success = false;
        try 
        {
            _ffmpegOperation = FfmpegOperation.Waveform;
            IsFfmpegBusy = true;
            IsExportingHardSub = false;
            FfmpegProgressPercent = 0;
            FfmpegProgressText = "Extracting audio for waveform";
            StatusMessage = "Extracting audio for waveform...";
            var tempDir = Path.Combine(Path.GetTempPath(), "AiSubtitlePro");
            Directory.CreateDirectory(tempDir);

            // Use a unique path to ensure WPF binding updates and to avoid stale waveform caches.
            var wavPath = Path.Combine(tempDir, $"{Path.GetFileNameWithoutExtension(videoPath)}_{Guid.NewGuid():N}.wav");

            WaveformAudioPath = null;
            await _ffmpegService.ExtractAudioAsync(videoPath, wavPath);

            WaveformAudioPath = wavPath;
            success = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Waveform error: {ex.Message}";
        }
        finally
        {
            // Stop accepting waveform progress updates before we set final status.
            _ffmpegOperation = FfmpegOperation.None;
            IsFfmpegBusy = false;
            IsExportingHardSub = false;
            if (success)
            {
                FfmpegProgressPercent = 0;
                FfmpegProgressText = string.Empty;
                StatusMessage = "Ready";
            }
            if (acquired)
                _ffmpegGate.Release();
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

    [RelayCommand]
    private async Task ExportHardSub()
    {
        if (CurrentDocument == null) return;
        if (string.IsNullOrWhiteSpace(MediaFilePath) || !File.Exists(MediaFilePath))
        {
            MessageBox.Show("Please open a media file first.", "Export Hard-Sub", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var acquired = await _ffmpegGate.WaitAsync(0);
        if (!acquired)
        {
            if (_ffmpegOperation == FfmpegOperation.Waveform)
            {
                try
                {
                    _ffmpegService.Cancel();
                }
                catch
                {
                }

                await _ffmpegGate.WaitAsync();
                acquired = true;
            }
            else
            {
                StatusMessage = "Another FFmpeg operation is already running";
                return;
            }
        }

        try
        {
            var isFfmpegAvailable = await _ffmpegService.IsAvailableAsync();
            if (!isFfmpegAvailable)
            {
                MessageBox.Show("FFmpeg is not available. Please place ffmpeg.exe next to the app or add it to PATH.", "Export Hard-Sub", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

        var mediaPath = MediaFilePath;
        var defaultName = Path.GetFileNameWithoutExtension(mediaPath) + "_hardsub" + Path.GetExtension(mediaPath);

        var dialog = new SaveFileDialog
        {
            Title = "Export Video (Hard-Sub) - Render subtitles into video",
            FileName = defaultName,
            Filter = "Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm|All Files|*.*"
        };

            if (dialog.ShowDialog() != true)
                return;

        var outputPath = dialog.FileName;

            string tempAssPath = string.Empty;
            try
            {
                _ffmpegOperation = FfmpegOperation.ExportHardSub;
                IsFfmpegBusy = true;
                IsExportingHardSub = true;
                FfmpegProgressPercent = 0;
                FfmpegProgressText = "Exporting hard-sub video";
                StatusMessage = "Exporting hard-sub video...";

                _exportCts?.Cancel();
                _exportCts?.Dispose();
                _exportCts = new CancellationTokenSource();

                var tempDir = Path.Combine(Path.GetTempPath(), "AiSubtitlePro");
                Directory.CreateDirectory(tempDir);
                tempAssPath = Path.Combine(tempDir, $"export_{Guid.NewGuid():N}.ass");

                var assContent = _assParser.Serialize(CurrentDocument);
                await File.WriteAllTextAsync(tempAssPath, assContent, new UTF8Encoding(true));

                // Ensure UI updates even if FFmpeg progress messages are delayed.
                StatusMessage = "Exporting hard-sub video...";

                var startAbs = CutStartAbs;
                var endAbs = CutEndAbs;
                if (startAbs < TimeSpan.Zero) startAbs = TimeSpan.Zero;
                if (endAbs <= TimeSpan.Zero || endAbs > MediaDurationAbs) endAbs = MediaDurationAbs;

                if (startAbs > TimeSpan.Zero || CutEndAbs > TimeSpan.Zero)
                {
                    var segDuration = endAbs - startAbs;
                    if (segDuration < TimeSpan.Zero) segDuration = TimeSpan.Zero;

                    await _ffmpegService.RenderHardSubAsync(
                        mediaPath,
                        tempAssPath,
                        outputPath,
                        start: startAbs,
                        duration: segDuration,
                        preferGpuEncoding: true,
                        cancellationToken: _exportCts.Token);
                }
                else
                {
                    await _ffmpegService.RenderHardSubAsync(mediaPath, tempAssPath, outputPath, preferGpuEncoding: true, _exportCts.Token);
                }

                StatusMessage = $"Export complete: {Path.GetFileName(outputPath)}";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Export canceled";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Export error: {ex.Message}";
                MessageBox.Show($"Export failed:\n{ex.Message}", "Export Hard-Sub", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(tempAssPath) && File.Exists(tempAssPath))
                        File.Delete(tempAssPath);
                }
                catch
                {
                }

                try
                {
                    _exportCts?.Dispose();
                    _exportCts = null;
                }
                catch
                {
                }

                _ffmpegOperation = FfmpegOperation.None;
                IsFfmpegBusy = false;
                IsExportingHardSub = false;
            }
        }
        finally
        {
            if (acquired)
                _ffmpegGate.Release();
        }
    }

    partial void OnIsExportingHardSubChanged(bool value)
    {
        CancelExportHardSubCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(IsExportingHardSub))]
    private void CancelExportHardSub()
    {
        if (!IsExportingHardSub)
            return;

        try
        {
            _exportCts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _ffmpegService.Cancel();
        }
        catch
        {
        }
    }

    #region Edit Commands

    [RelayCommand]
    private void AddLine()
    {
        if (CurrentDocument == null) return;

        var doc = CurrentDocument;

        var start = ClampTimelineTime(CurrentPosition);
        var end = ClampTimelineTime(start + TimeSpan.FromSeconds(2));
        if (end < start) end = start;

        var newLine = new SubtitleLine
        {
            Start = start,
            End = end,
            Text = ""
        };

        // Insert after selected line or at end
        var insertIndex = SelectedLine != null 
            ? CurrentDocument.Lines.IndexOf(SelectedLine) + 1 
            : CurrentDocument.Lines.Count;

        _undo.Execute(new DelegateCommand(
            "Add Line",
            execute: () =>
            {
                doc.Lines.Insert(insertIndex, newLine);
                doc.ReindexLines();
                doc.IsDirty = true;
            },
            undo: () =>
            {
                doc.Lines.Remove(newLine);
                doc.ReindexLines();
                doc.IsDirty = true;
            }));

        RefreshDisplayedLines();
        SelectedLine = newLine;
        StatusMessage = "Added new line";
    }

    [RelayCommand]
    private void DeleteSelectedLines()
    {
        if (CurrentDocument == null || SelectedLine == null) return;

        var doc = CurrentDocument;

        var selectedLines = DisplayedLines.Where(l => l.IsSelected).ToList();
        if (selectedLines.Count == 0 && SelectedLine != null)
        {
            selectedLines.Add(SelectedLine);
        }

        var deleted = selectedLines.Select(l => (index: doc.Lines.IndexOf(l), line: l)).OrderByDescending(x => x.index).ToList();

        _undo.Execute(new DelegateCommand(
            selectedLines.Count == 1 ? "Delete Line" : $"Delete {selectedLines.Count} Lines",
            execute: () =>
            {
                foreach (var (_, line) in deleted)
                    doc.Lines.Remove(line);
                doc.ReindexLines();
                doc.IsDirty = true;
            },
            undo: () =>
            {
                foreach (var (idx, line) in deleted.OrderBy(x => x.index))
                    doc.Lines.Insert(Math.Min(idx, doc.Lines.Count), line);
                doc.ReindexLines();
                doc.IsDirty = true;
            }));

        RefreshDisplayedLines();

        StatusMessage = $"Deleted {selectedLines.Count} line(s)";
    }

    [RelayCommand]
    private void SplitLine()
    {
        if (CurrentDocument == null || SelectedLine == null) return;

        var doc = CurrentDocument;

        var line = SelectedLine;
        var midTime = line.Start + (line.Duration / 2);

        var index = doc.Lines.IndexOf(line);
        var oldEnd = line.End;
        var newLine = line.Clone();
        newLine.Start = midTime;

        _undo.Execute(new DelegateCommand(
            "Split Line",
            execute: () =>
            {
                line.End = midTime;
                doc.Lines.Insert(index + 1, newLine);
                doc.ReindexLines();
                doc.IsDirty = true;
            },
            undo: () =>
            {
                doc.Lines.Remove(newLine);
                line.End = oldEnd;
                doc.ReindexLines();
                doc.IsDirty = true;
            }));

        RefreshDisplayedLines();
        StatusMessage = "Line split";
    }

    [RelayCommand]
    private void MergeWithNext()
    {
        if (CurrentDocument == null || SelectedLine == null) return;

        var doc = CurrentDocument;
        var index = doc.Lines.IndexOf(SelectedLine);
        if (index >= doc.Lines.Count - 1) return;

        var line = SelectedLine;
        var nextLine = doc.Lines[index + 1];
        var oldText = line.Text;
        var oldEnd = line.End;

        _undo.Execute(new DelegateCommand(
            "Merge Lines",
            execute: () =>
            {
                line.Text = $"{line.Text}\\N{nextLine.Text}";
                line.End = nextLine.End;
                doc.Lines.Remove(nextLine);
                doc.ReindexLines();
                doc.IsDirty = true;
            },
            undo: () =>
            {
                var insertAt = Math.Min(index + 1, doc.Lines.Count);
                doc.Lines.Insert(insertAt, nextLine);
                line.Text = oldText;
                line.End = oldEnd;
                doc.ReindexLines();
                doc.IsDirty = true;
            }));

        RefreshDisplayedLines();
        StatusMessage = "Lines merged";
    }

    #endregion

    #region Timing Commands

    [RelayCommand]
    private void ShiftTiming()
    {
        if (CurrentDocument == null) return;
        if (!ShiftTimingDialog.TryGetOptions(SelectedLine, CurrentDocument, out var linesToShift, out var offset, out var shiftStart, out var shiftEnd))
            return;

        var doc = CurrentDocument;
        var states = linesToShift.Select(l => (l, l.Start, l.End)).ToList();

        _undo.Execute(new DelegateCommand(
            "Shift Timing",
            execute: () =>
            {
                foreach (var (l, _, _) in states)
                {
                    if (shiftStart) l.Start += offset;
                    if (shiftEnd) l.End += offset;
                }
                doc.IsDirty = true;
            },
            undo: () =>
            {
                foreach (var (l, s, e) in states)
                {
                    if (shiftStart) l.Start = s;
                    if (shiftEnd) l.End = e;
                }
                doc.IsDirty = true;
            }));

        RefreshDisplayedLines();
        StatusMessage = "Timing shifted";
    }

    [RelayCommand]
    private void SetStartTime()
    {
        if (SelectedLine == null) return;
        var doc = CurrentDocument;
        if (doc == null) return;
        var line = SelectedLine;
        var old = line.Start;
        var next = CurrentPosition;
        _undo.Execute(new DelegateCommand(
            "Set Start Time",
            execute: () => { line.Start = next; doc.IsDirty = true; },
            undo: () => { line.Start = old; doc.IsDirty = true; }));
        StatusMessage = $"Start time set to {FormatTime(CurrentPosition)}";
    }

    [RelayCommand]
    private void SetEndTime()
    {
        if (SelectedLine == null) return;
        var doc = CurrentDocument;
        if (doc == null) return;
        var line = SelectedLine;
        var old = line.End;
        var next = CurrentPosition;
        _undo.Execute(new DelegateCommand(
            "Set End Time",
            execute: () => { line.End = next; doc.IsDirty = true; },
            undo: () => { line.End = old; doc.IsDirty = true; }));
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
        // If a media file is loaded, transcribe the currently selected cut range.
        if (!string.IsNullOrWhiteSpace(MediaFilePath) && File.Exists(MediaFilePath))
        {
            if (MediaDurationAbs <= TimeSpan.Zero)
            {
                MessageBox.Show("Media is not ready yet. Please wait for the video to finish loading, then try again.", "Transcribe", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var startAbs = CutStartAbs;
            var endAbs = CutEndAbs;
            if (startAbs < TimeSpan.Zero) startAbs = TimeSpan.Zero;
            if (endAbs <= TimeSpan.Zero || endAbs > MediaDurationAbs) endAbs = MediaDurationAbs;
            var duration = endAbs - startAbs;
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

            var wizard = new TranscriptionWizard(mediaFilePath: MediaFilePath, startAbs: startAbs, duration: duration);
            wizard.Owner = Application.Current.MainWindow;

            if (wizard.ShowDialog() == true && wizard.Result != null)
            {
                CurrentDocument = wizard.Result;
                RefreshDisplayedLines();
                StatusMessage = $"Transcription complete: {CurrentDocument.Lines.Count} lines generated";
            }

            return;
        }

        // Fallback: old behavior (choose any file).
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

    [RelayCommand]
    private void RemoveBilingualKeepFirst()
    {
        RemoveBilingualInternal(keepSecond: false);
    }

    [RelayCommand]
    private void RemoveBilingualKeepSecond()
    {
        RemoveBilingualInternal(keepSecond: true);
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

    private void RemoveBilingualInternal(bool keepSecond)
    {
        if (CurrentDocument == null || CurrentDocument.Lines.Count == 0)
            return;

        var doc = CurrentDocument;

        var targetLines = DisplayedLines.Where(l => l.IsSelected).ToList();
        if (targetLines.Count == 0)
            targetLines = doc.Lines.ToList();

        var oldTexts = targetLines.Select(l => (line: l, text: l.Text)).ToList();

        string title = keepSecond ? "Remove Bilingual (Keep 2nd)" : "Remove Bilingual (Keep 1st)";
        _undo.Execute(new DelegateCommand(
            title,
            execute: () =>
            {
                foreach (var line in targetLines)
                {
                    var (first, second, has) = SplitBilingual(line.Text);
                    if (!has) continue;
                    line.Text = keepSecond ? second : first;
                }
                doc.IsDirty = true;
            },
            undo: () =>
            {
                foreach (var (line, text) in oldTexts)
                    line.Text = text;
                doc.IsDirty = true;
            }));

        RefreshDisplayedLines();
        RefreshSubtitlePreview();
        StatusMessage = keepSecond ? "Removed bilingual text (kept 2nd line)" : "Removed bilingual text (kept 1st line)";
    }

    private static (string first, string second, bool hasBilingual) SplitBilingual(string text)
    {
        if (string.IsNullOrEmpty(text))
            return (text, text, false);

        const string assNewLine = "\\N";
        var idx = text.IndexOf(assNewLine, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var first = text.Substring(0, idx);
            var second = text.Substring(idx + assNewLine.Length);
            return (first, second, true);
        }

        // Fallback to real newlines
        var parts = text.Split(new[] { "\r\n", "\n", "\r" }, 2, StringSplitOptions.None);
        if (parts.Length == 2)
            return (parts[0], parts[1], true);

        return (text, text, false);
    }

    #endregion
}
