using AiSubtitlePro.Core.Models;
using AiSubtitlePro.Core.Parsers;
using AiSubtitlePro.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.VisualBasic;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Diagnostics;
using System.Threading;

using AiSubtitlePro.Infrastructure.Media;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using AiSubtitlePro.Services;
using System.Windows.Threading;

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

    private bool _syncingPosFromText;
    private bool _syncingTextFromPos;
    private static readonly Regex AssPosRegex = new(@"\\pos\((\d+),(\d+)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Stopwatch _subtitlePerfStopwatch = Stopwatch.StartNew();
    private long _lastSubtitleOverlayUpdateTick;
    private SubtitleDocument? _subtitleCacheDoc;
    private bool _subtitleCacheDirty = true;
    private List<SubtitleLine> _linesByStart = new();
    private List<SubtitleLine> _lastActiveLines = new();

    private readonly DispatcherTimer _previewDebounceTimer;
    private bool _previewDebouncePending;

    private bool _exportHardSubLastVertical;
    private int _exportHardSubLastBlurSigma = 20;
    private bool _exportHardSubLastEnableTrailer;
    private TimeSpan _exportHardSubLastTrailerStart = TimeSpan.Zero;
    private TimeSpan _exportHardSubLastTrailerDuration = TimeSpan.FromSeconds(5);
    private TimeSpan _exportHardSubLastTrailerTransition = TimeSpan.FromSeconds(1);
    private DateTime _ffmpegOperationStartedUtc;

    [ObservableProperty]
    private SubtitleDocument? _currentDocument;

    [ObservableProperty]
    private SubtitleLine? _selectedLine;

    [ObservableProperty]
    private string _selectedLineStartText = string.Empty;

    [ObservableProperty]
    private string _selectedLineEndText = string.Empty;

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
    private string _selectedStyleFontName = "Arial";

    public ObservableCollection<string> InstalledFontFamilies { get; } = new();

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
        _previewDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _previewDebounceTimer.Tick += (_, __) =>
        {
            _previewDebounceTimer.Stop();
            if (!_previewDebouncePending) return;
            _previewDebouncePending = false;
            RefreshSubtitlePreview();
        };

        TryCleanupTempFiles();

        try
        {
            foreach (var f in Fonts.SystemFontFamilies
                         .Select(ff => ff.Source)
                         .Where(s => !string.IsNullOrWhiteSpace(s))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                InstalledFontFamilies.Add(f);
            }
        }
        catch
        {
        }

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
                    var elapsed = DateTime.UtcNow - _ffmpegOperationStartedUtc;
                    var elapsedText = elapsed > TimeSpan.Zero ? $" (elapsed {FormatElapsed(elapsed)})" : string.Empty;
                    StatusMessage = p.ProgressPercent > 0 || p.TotalDuration > TimeSpan.Zero
                        ? $"Exporting hard-sub video... {p.ProgressPercent}%{elapsedText}"
                        : (string.IsNullOrWhiteSpace(p.Status) ? $"Exporting hard-sub video...{elapsedText}" : p.Status + elapsedText);
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

    private void RequestSubtitlePreviewRebuild(bool immediate)
    {
        if (immediate)
        {
            try { _previewDebounceTimer.Stop(); } catch { }
            _previewDebouncePending = false;
            RefreshSubtitlePreview();
            return;
        }

        _previewDebouncePending = true;
        if (!_previewDebounceTimer.IsEnabled)
            _previewDebounceTimer.Start();
    }

    partial void OnCurrentDocumentChanged(SubtitleDocument? value)
    {
        try
        {
            if (_subtitleCacheDoc != null)
                _subtitleCacheDoc.Lines.CollectionChanged -= SubtitleLines_CollectionChanged;
        }
        catch
        {
        }

        _subtitleCacheDoc = value;
        _subtitleCacheDirty = true;
        _lastActiveLines = new List<SubtitleLine>();

        try
        {
            if (_subtitleCacheDoc != null)
                _subtitleCacheDoc.Lines.CollectionChanged += SubtitleLines_CollectionChanged;
        }
        catch
        {
        }
    }

    private void SubtitleLines_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        _subtitleCacheDirty = true;
    }

    private void EnsureSubtitleTimingCache()
    {
        var doc = CurrentDocument;
        if (doc == null) return;

        if (!_subtitleCacheDirty && ReferenceEquals(_subtitleCacheDoc, doc) && _linesByStart.Count == doc.Lines.Count)
            return;

        _subtitleCacheDoc = doc;
        _linesByStart = doc.Lines
            .OrderBy(l => l.Start)
            .ThenBy(l => l.End)
            .ToList();
        _subtitleCacheDirty = false;
    }

    private static int UpperBoundStart(List<SubtitleLine> lines, TimeSpan t)
    {
        var lo = 0;
        var hi = lines.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (lines[mid].Start <= t) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private List<SubtitleLine> ComputeActiveLines(TimeSpan value)
    {
        EnsureSubtitleTimingCache();
        if (_linesByStart.Count == 0) return new List<SubtitleLine>();

        var idx = UpperBoundStart(_linesByStart, value);
        var active = new List<SubtitleLine>();

        // Start inclusive, End exclusive (Aegisub-style), scan backward for overlaps.
        for (var i = idx - 1; i >= 0; i--)
        {
            var l = _linesByStart[i];
            if (l.End <= value)
                break;
            if (value >= l.Start && value < l.End)
                active.Add(l);
        }

        if (active.Count <= 1)
            return active;

        // Stable order
        return active.OrderBy(l => l.Start).ThenBy(l => l.Layer).ToList();
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

    [RelayCommand]
    private void AddStyle()
    {
        var doc = CurrentDocument;
        var line = SelectedLine;
        if (doc == null || line == null) return;

        try
        {
            var proposed = Interaction.InputBox("Enter new style name:", "Add Style", "NewStyle");
            proposed = (proposed ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(proposed))
                return;

            var name = proposed;
            var suffix = 1;
            while (doc.Styles.Any(s => string.Equals(s.Name, name, StringComparison.Ordinal)))
            {
                name = $"{proposed}_{suffix}";
                suffix++;
            }

            var prevStyleName = line.StyleName;
            var prevStyles = doc.Styles.Select(s => (s, s.Clone())).ToList();

            _undo.Execute(new DelegateCommand(
                "Add Style",
                execute: () =>
                {
                    var baseStyle = doc.GetStyle("Default");
                    var st = baseStyle.Clone();
                    st.Name = name;
                    doc.Styles.Add(st);
                    line.StyleName = name;
                    line.UseStyleOverride = false;
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

            SyncSelectedStyleFromLine();
            RefreshSubtitlePreview();
            StatusMessage = $"Added style '{name}'";
        }
        catch
        {
        }
    }

    public async Task OpenSubtitleFromPathAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        if (!ConfirmDiscardChanges())
            return;

        await LoadSubtitleAsync(filePath);
    }

    [RelayCommand]
    private void RenameStyle()
    {
        var doc = CurrentDocument;
        var line = SelectedLine;
        if (doc == null || line == null) return;

        var oldName = string.IsNullOrWhiteSpace(line.StyleName) ? "Default" : line.StyleName;
        if (string.Equals(oldName, "Default", StringComparison.Ordinal))
        {
            MessageBox.Show("Cannot rename the 'Default' style.", "Rename Style", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var style = doc.Styles.FirstOrDefault(s => string.Equals(s.Name, oldName, StringComparison.Ordinal));
        if (style == null)
            return;

        var proposed = Interaction.InputBox("Enter new style name:", "Rename Style", oldName);
        proposed = (proposed ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(proposed) || string.Equals(proposed, oldName, StringComparison.Ordinal))
            return;

        if (doc.Styles.Any(s => string.Equals(s.Name, proposed, StringComparison.Ordinal)))
        {
            MessageBox.Show("A style with that name already exists.", "Rename Style", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var prevStyles = doc.Styles.Select(s => (s, s.Clone())).ToList();
        var prevLineStyles = doc.Lines.Select(l => (line: l, styleName: l.StyleName)).ToList();
        var affected = doc.Lines.Where(l => string.Equals(l.StyleName, oldName, StringComparison.Ordinal)).ToList();

        _undo.Execute(new DelegateCommand(
            "Rename Style",
            execute: () =>
            {
                style.Name = proposed;
                foreach (var l in affected)
                    l.StyleName = proposed;
                doc.IsDirty = true;
            },
            undo: () =>
            {
                doc.Styles.Clear();
                foreach (var (_, clone) in prevStyles)
                    doc.Styles.Add(clone);
                foreach (var (ln, styleName) in prevLineStyles)
                    ln.StyleName = styleName;
                doc.IsDirty = true;
            }));

        SyncSelectedStyleFromLine();
        RefreshSubtitlePreview();
        StatusMessage = $"Renamed style '{oldName}' -> '{proposed}'";
    }

    [RelayCommand]
    private void DeleteStyle()
    {
        var doc = CurrentDocument;
        var line = SelectedLine;
        if (doc == null || line == null) return;

        var name = string.IsNullOrWhiteSpace(line.StyleName) ? "Default" : line.StyleName;
        if (string.Equals(name, "Default", StringComparison.Ordinal))
        {
            MessageBox.Show("Cannot delete the 'Default' style.", "Delete Style", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var st = doc.Styles.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        if (st == null)
            return;

        var usedBy = doc.Lines.Where(l => string.Equals(l.StyleName, name, StringComparison.Ordinal)).ToList();
        if (usedBy.Count > 0)
        {
            var r = MessageBox.Show(
                $"Style '{name}' is used by {usedBy.Count} line(s).\n\nDelete it and move those lines to 'Default'?",
                "Delete Style",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (r != MessageBoxResult.Yes)
                return;
        }
        else
        {
            var r = MessageBox.Show($"Delete style '{name}'?", "Delete Style", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes)
                return;
        }

        var prevStyles = doc.Styles.Select(s => (s, s.Clone())).ToList();
        var prevLineStyles = doc.Lines.Select(l => (line: l, styleName: l.StyleName, useOverride: l.UseStyleOverride)).ToList();
        var prevStyleName = line.StyleName;

        _undo.Execute(new DelegateCommand(
            "Delete Style",
            execute: () =>
            {
                foreach (var l in usedBy)
                    l.StyleName = "Default";
                doc.Styles.Remove(st);
                if (string.Equals(line.StyleName, name, StringComparison.Ordinal))
                    line.StyleName = "Default";
                if (string.Equals(prevStyleName, name, StringComparison.Ordinal))
                    line.UseStyleOverride = false;
                doc.IsDirty = true;
            },
            undo: () =>
            {
                doc.Styles.Clear();
                foreach (var (_, clone) in prevStyles)
                    doc.Styles.Add(clone);
                line.StyleName = prevStyleName;
                foreach (var (ln, styleName, useOverride) in prevLineStyles)
                {
                    ln.StyleName = styleName;
                    ln.UseStyleOverride = useOverride;
                }
                doc.IsDirty = true;
            }));

        SyncSelectedStyleFromLine();
        RefreshSubtitlePreview();
        StatusMessage = $"Deleted style '{name}'";
    }

    partial void OnCurrentPositionChanged(TimeSpan value)
    {
        if (CurrentDocument != null)
        {
            // Throttle overlay updates while scrubbing/playing.
            // We only push new ASS to the renderer when the active set changes.
            var nowTick = _subtitlePerfStopwatch.ElapsedTicks;
            var minTicks = Stopwatch.Frequency / 60; // cap overlay rebuild to ~60hz
            if (nowTick - _lastSubtitleOverlayUpdateTick < minTicks)
                return;

            var activeLines = ComputeActiveLines(value);

            var changed = _lastActiveLines.Count != activeLines.Count;
            if (!changed)
            {
                for (var i = 0; i < _lastActiveLines.Count; i++)
                {
                    if (!ReferenceEquals(_lastActiveLines[i], activeLines[i]))
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (!changed)
                return;

            _lastSubtitleOverlayUpdateTick = nowTick;
            _lastActiveLines = activeLines;

            ActiveSubtitleText = activeLines.Count > 0
                ? string.Join("\n", activeLines.Select(l => l.Text))
                : string.Empty;
        }
    }

    [RelayCommand]
    private void InsertLineBeforeAtCurrentTime()
    {
        InsertLineRelativeToSelection(insertAfter: false);
    }

    [RelayCommand]
    private void InsertLineAfterAtCurrentTime()
    {
        InsertLineRelativeToSelection(insertAfter: true);
    }

    private void InsertLineRelativeToSelection(bool insertAfter)
    {
        if (CurrentDocument == null) return;

        var doc = CurrentDocument;
        var anchor = SelectedLine;
        var baseIndex = anchor != null ? doc.Lines.IndexOf(anchor) : doc.Lines.Count;
        if (baseIndex < 0) baseIndex = doc.Lines.Count;

        var insertIndex = insertAfter ? Math.Min(baseIndex + 1, doc.Lines.Count) : Math.Min(baseIndex, doc.Lines.Count);

        var start = ClampTimelineTime(CurrentPosition);
        var end = ClampTimelineTime(start + TimeSpan.FromSeconds(3));
        if (end < start) end = start;

        var newLine = new SubtitleLine
        {
            Start = start,
            End = end,
            Text = string.Empty,
            StyleName = anchor?.StyleName ?? "Default"
        };

        _undo.Execute(new DelegateCommand(
            insertAfter ? "Insert Line After" : "Insert Line Before",
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
        StatusMessage = insertAfter ? "Inserted line after" : "Inserted line before";

        _subtitleCacheDirty = true;
        RefreshSubtitlePreview();
    }

    private static string? TryComputeWaveformCacheKey(string mediaPath)
    {
        try
        {
            var fi = new FileInfo(mediaPath);
            if (!fi.Exists) return null;

            var payload = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
            var bytes = Encoding.UTF8.GetBytes(payload);

            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static void TryTouchFileUtc(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch
        {
        }
    }

    private void HandleSelectedLineOverrideChanged()
    {
        try
        {
            var doc = CurrentDocument;
            var line = SelectedLine;
            if (doc == null || line == null) return;

            if (line.UseStyleOverride)
            {
                EnsureLineHasEditableStyle(doc, line);
                doc.IsDirty = true;
                return;
            }

            var styleName = string.IsNullOrWhiteSpace(line.StyleName) ? "Default" : line.StyleName;
            var marker = "__L";
            var idx = styleName.IndexOf(marker, StringComparison.Ordinal);
            if (idx <= 0)
                return;

            var baseName = styleName[..idx];
            if (!doc.Styles.Any(s => string.Equals(s.Name, baseName, StringComparison.Ordinal)))
                return;

            var oldName = styleName;
            line.StyleName = baseName;

            var stillUsed = doc.Lines.Any(l => string.Equals(l.StyleName, oldName, StringComparison.Ordinal));
            if (!stillUsed)
            {
                var toRemove = doc.Styles.FirstOrDefault(s => string.Equals(s.Name, oldName, StringComparison.Ordinal));
                if (toRemove != null)
                    doc.Styles.Remove(toRemove);
            }

            doc.IsDirty = true;
        }
        catch
        {
        }
    }

    private static string? TryGetAegisubVideoFilePath(string subtitlePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(subtitlePath) || !File.Exists(subtitlePath))
                return null;

            var dir = Path.GetDirectoryName(subtitlePath);
            var inSection = false;
            foreach (var raw in File.ReadLines(subtitlePath))
            {
                var line = raw?.Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    inSection = string.Equals(line, "[Aegisub Project Garbage]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection)
                    continue;

                if (line.StartsWith("Video File:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line.Substring("Video File:".Length).Trim();
                    if (value.Length >= 2 && ((value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal))
                        || (value.StartsWith("'", StringComparison.Ordinal) && value.EndsWith("'", StringComparison.Ordinal))))
                    {
                        value = value[1..^1].Trim();
                    }
                    if (string.IsNullOrWhiteSpace(value))
                        return null;

                    if (Path.IsPathRooted(value))
                        return value;

                    if (!string.IsNullOrWhiteSpace(dir))
                        return Path.GetFullPath(Path.Combine(dir, value));

                    return value;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatElapsed(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
        return $"{t.Minutes:D2}:{t.Seconds:D2}";
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

    private void TrySyncPosFromText(SubtitleLine line)
    {
        if (_syncingTextFromPos)
            return;

        try
        {
            _syncingPosFromText = true;

            var text = line.Text ?? string.Empty;
            var m = AssPosRegex.Match(text);
            if (!m.Success)
            {
                // If the user removed the \pos tag, revert back to default (style-based) position.
                if (line.PosX.HasValue) line.PosX = null;
                if (line.PosY.HasValue) line.PosY = null;
                return;
            }

            if (!int.TryParse(m.Groups[1].Value, out var px))
                return;
            if (!int.TryParse(m.Groups[2].Value, out var py))
                return;

            // Only update if changed to avoid churn.
            if (line.PosX != px) line.PosX = px;
            if (line.PosY != py) line.PosY = py;
        }
        catch
        {
        }
        finally
        {
            _syncingPosFromText = false;
        }
    }

    private void TrySyncTextFromPos(SubtitleLine line)
    {
        if (_syncingPosFromText)
            return;

        if (!line.PosX.HasValue || !line.PosY.HasValue)
            return;

        try
        {
            _syncingTextFromPos = true;

            var text = line.Text ?? string.Empty;
            var newTag = $"\\pos({line.PosX.Value},{line.PosY.Value})";

            if (AssPosRegex.IsMatch(text))
            {
                // Replace first \pos(x,y) in the line
                text = AssPosRegex.Replace(text, newTag, 1);
                line.Text = text;
                return;
            }

            // No existing \pos - insert into first override block if present, else prepend a new one.
            var idxOpen = text.IndexOf('{');
            var idxClose = idxOpen >= 0 ? text.IndexOf('}', idxOpen + 1) : -1;
            if (idxOpen == 0 && idxClose > 0)
            {
                var inner = text.Substring(1, idxClose - 1);
                var updated = "{" + inner + "\\" + newTag + "}" + text.Substring(idxClose + 1);
                line.Text = updated;
            }
            else
            {
                line.Text = "{" + "\\" + newTag + "}" + text;
            }
        }
        catch
        {
        }
        finally
        {
            _syncingTextFromPos = false;
        }
    }

    partial void OnSelectedLineChanged(SubtitleLine? value)
    {
        if (_selectedLineHook != null)
            _selectedLineHook.PropertyChanged -= SelectedLine_PropertyChanged;

        _selectedLineHook = value;
        if (_selectedLineHook != null)
            _selectedLineHook.PropertyChanged += SelectedLine_PropertyChanged;

        SelectedLineStartText = value != null ? FormatTime(value.Start) : string.Empty;
        SelectedLineEndText = value != null ? FormatTime(value.End) : string.Empty;

        SyncSelectedStyleFromLine();
        RefreshSubtitlePreview();
    }

    private void SelectedLine_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Ensure edits (text/style name/pos/timing) reflect immediately on preview
        if (e.PropertyName == nameof(SubtitleLine.Text)
            || e.PropertyName == nameof(SubtitleLine.StyleName)
            || e.PropertyName == nameof(SubtitleLine.UseStyleOverride)
            || e.PropertyName == nameof(SubtitleLine.PosX)
            || e.PropertyName == nameof(SubtitleLine.PosY)
            || e.PropertyName == nameof(SubtitleLine.Start)
            || e.PropertyName == nameof(SubtitleLine.End))
        {
            if (sender is SubtitleLine line)
            {
                if (e.PropertyName == nameof(SubtitleLine.Text))
                {
                    TrySyncPosFromText(line);
                }
                else if (e.PropertyName == nameof(SubtitleLine.PosX) || e.PropertyName == nameof(SubtitleLine.PosY))
                {
                    TrySyncTextFromPos(line);
                }
            }

            if (e.PropertyName == nameof(SubtitleLine.StyleName))
            {
                TryEnsureSelectedLineStyleExists();
                SyncSelectedStyleFromLine();
            }
            else if (e.PropertyName == nameof(SubtitleLine.UseStyleOverride))
            {
                HandleSelectedLineOverrideChanged();
                SyncSelectedStyleFromLine();
            }

            // Text edits happen per-keystroke -> debounce to avoid UI stalls.
            // Timing/style/pos edits should apply immediately.
            var immediate = e.PropertyName != nameof(SubtitleLine.Text);
            RequestSubtitlePreviewRebuild(immediate);
        }

        if (sender == SelectedLine)
        {
            if (e.PropertyName == nameof(SubtitleLine.Start))
                SelectedLineStartText = SelectedLine != null ? FormatTime(SelectedLine.Start) : string.Empty;
            else if (e.PropertyName == nameof(SubtitleLine.End))
                SelectedLineEndText = SelectedLine != null ? FormatTime(SelectedLine.End) : string.Empty;
        }
    }

    partial void OnSelectedLineStartTextChanged(string value)
    {
        if (SelectedLine == null) return;
        if (!TryParseTime(value, out var ts)) return;
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        SelectedLine.Start = ts;
        if (SelectedLine.End < SelectedLine.Start)
            SelectedLine.End = SelectedLine.Start;
        CurrentDocument!.IsDirty = true;
    }

    partial void OnSelectedLineEndTextChanged(string value)
    {
        if (SelectedLine == null) return;
        if (!TryParseTime(value, out var ts)) return;
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        if (ts < SelectedLine.Start) ts = SelectedLine.Start;
        SelectedLine.End = ts;
        CurrentDocument!.IsDirty = true;
    }

    private static bool TryParseTime(string? text, out TimeSpan ts)
    {
        ts = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var t = text.Trim();

        // Accept: h:mm:ss.ff | mm:ss.ff | ss.ff
        // Convert last segment fractional separator to milliseconds.
        var parts = t.Split(':');
        int h = 0, m = 0;
        double s = 0;

        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[0], out h)) return false;
            if (!int.TryParse(parts[1], out m)) return false;
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out s)) return false;
        }
        else if (parts.Length == 2)
        {
            if (!int.TryParse(parts[0], out m)) return false;
            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out s)) return false;
        }
        else if (parts.Length == 1)
        {
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out s)) return false;
        }
        else
        {
            return false;
        }

        var wholeSeconds = (int)Math.Floor(s);
        var frac = s - wholeSeconds;
        var ms = (int)Math.Round(frac * 1000);
        if (ms >= 1000)
        {
            ms -= 1000;
            wholeSeconds += 1;
        }

        try
        {
            ts = new TimeSpan(0, h, m, wholeSeconds, ms);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void RefreshSubtitlePreview()
    {
        try
        {
            var doc = CurrentDocument;
            if (doc == null)
            {
                ActiveSubtitleText = string.Empty;
                ActiveSubtitleAss = string.Empty;
                return;
            }

            ActiveSubtitleAss = BuildAssForPreview(doc);
            ActiveSubtitleText = SelectedLine?.Text ?? string.Empty;

            _lastSubtitleOverlayUpdateTick = 0;
            _lastActiveLines = new List<SubtitleLine>();
        }
        catch
        {
        }
    }


    partial void OnSelectedStyleFontNameChanged(string value)
    {
        if (_isSyncingSelectedStyle) return;
        UpdateSelectedStyle(s => s.FontName = value);
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
            SelectedStyleFontName = style.FontName;
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

    private void TryEnsureSelectedLineStyleExists()
    {
        try
        {
            var doc = CurrentDocument;
            var line = SelectedLine;
            if (doc == null || line == null) return;

            var name = string.IsNullOrWhiteSpace(line.StyleName) ? "Default" : line.StyleName;
            if (!string.Equals(line.StyleName, name, StringComparison.Ordinal))
                line.StyleName = name;

            if (doc.Styles.Any(s => string.Equals(s.Name, name, StringComparison.Ordinal)))
                return;

            // Create a new style with this name (cloned from Default) so editing can begin immediately.
            var baseStyle = doc.GetStyle("Default");
            var newStyle = baseStyle.Clone();
            newStyle.Name = name;
            doc.Styles.Add(newStyle);
            doc.IsDirty = true;
        }
        catch
        {
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
                // If overriding for this line, ensure the line has its own unique style.
                // Otherwise, edit the shared style so all lines using it update together.
                var style = line.UseStyleOverride
                    ? EnsureLineHasEditableStyle(doc, line)
                    : doc.GetStyle(line.StyleName);
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

        // Force overlay rebuild even if the active line set is unchanged.
        _lastActiveLines = new List<SubtitleLine>();
        _lastSubtitleOverlayUpdateTick = 0;
        RequestSubtitlePreviewRebuild(immediate: true);
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

        // Force overlay rebuild even if the active line set is unchanged.
        _lastActiveLines = new List<SubtitleLine>();
        _lastSubtitleOverlayUpdateTick = 0;
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

    private static string BuildAssForPreview(SubtitleDocument doc)
    {
        if (doc.Lines.Count == 0)
            return string.Empty;

        var stylesToInclude = doc.Styles.ToList();

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
        foreach (var l in doc.Lines)
        {
            var text = (l.Text ?? string.Empty)
                .Replace("\r\n", "\\N")
                .Replace("\n", "\\N")
                .Replace("\r", "\\N");

            var overrideTags = string.Empty;
            if (l.PosX.HasValue && l.PosY.HasValue)
                overrideTags = $"{{\\pos({l.PosX.Value},{l.PosY.Value})}}";

            var start = l.Start;
            var end = l.End;
            if (end < start) end = start;

            sb.AppendLine($"Dialogue: {l.Layer},{FormatAssTime(start)},{FormatAssTime(end)},{l.StyleName},{l.Actor},{l.MarginL},{l.MarginR},{l.MarginV},{l.Effect},{overrideTags}{text}");
        }

        return sb.ToString();
    }

    private static string FormatAssTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var cs = (int)Math.Round(t.TotalMilliseconds / 10.0);
        var h = cs / (100 * 60 * 60);
        cs -= h * 100 * 60 * 60;
        var m = cs / (100 * 60);
        cs -= m * 100 * 60;
        var s = cs / 100;
        cs -= s * 100;
        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}:{2:D2}.{3:D2}", h, m, s, cs);
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

            if (CurrentDocument != null)
                CurrentDocument.IsDirty = true;

            CutStartAbs = TimeSpan.Zero;
            CutEndAbs = TimeSpan.Zero;
            _cutSourceDocument = null;

            await GenerateWaveformAsync(MediaFilePath);
        }
    }

    public async Task OpenMediaFromPathAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        if (!ConfirmDiscardChanges())
            return;

        // Dropping media is treated as starting a new project.
        CurrentDocument = SubtitleDocument.CreateNew();
        RefreshDisplayedLines();

        MediaFilePath = filePath;
        StatusMessage = $"Loaded media: {Path.GetFileName(filePath)}";

        // Reset cut range when opening new media.
        CutStartAbs = TimeSpan.Zero;
        CutEndAbs = TimeSpan.Zero;
        _cutSourceDocument = null;

        await GenerateWaveformAsync(MediaFilePath);
    }

    [RelayCommand]
    private void SelectAllLines()
    {
        foreach (var l in DisplayedLines)
            l.IsSelected = true;
    }

    [RelayCommand]
    private void CloseProject()
    {
        if (!ConfirmDiscardChanges()) return;

        try
        {
            _exportCts?.Cancel();
        }
        catch
        {
        }

        CurrentDocument = SubtitleDocument.CreateNew();
        RefreshDisplayedLines();

        MediaFilePath = null;
        WaveformAudioPath = null;

        CutStartAbs = TimeSpan.Zero;
        CutEndAbs = TimeSpan.Zero;
        _cutSourceDocument = null;

        MediaDurationAbs = TimeSpan.Zero;
        MediaDuration = TimeSpan.Zero;
        CurrentPosition = TimeSpan.Zero;

        StatusMessage = "Project closed";
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
        TryCleanupTempFiles();

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

            // Waveform cache: avoid re-extracting if media hasn't changed.
            var cacheKey = TryComputeWaveformCacheKey(videoPath);
            if (!string.IsNullOrWhiteSpace(cacheKey))
            {
                var cachedWavPath = Path.Combine(tempDir, $"waveform_{cacheKey}.wav");
                if (File.Exists(cachedWavPath))
                {
                    WaveformAudioPath = null;
                    WaveformAudioPath = cachedWavPath;
                    TryTouchFileUtc(cachedWavPath);
                    success = true;
                    return;
                }
            }

            // Use a unique path to ensure WPF binding updates and to avoid stale waveform caches.
            var wavPath = Path.Combine(tempDir, $"waveform_tmp_{Guid.NewGuid():N}.wav");

            WaveformAudioPath = null;
            await _ffmpegService.ExtractAudioAsync(videoPath, wavPath);

            if (!string.IsNullOrWhiteSpace(cacheKey))
            {
                var cachedWavPath = Path.Combine(tempDir, $"waveform_{cacheKey}.wav");
                try
                {
                    if (File.Exists(cachedWavPath))
                        File.Delete(cachedWavPath);
                }
                catch
                {
                }

                try
                {
                    File.Move(wavPath, cachedWavPath);
                    wavPath = cachedWavPath;
                    TryTouchFileUtc(wavPath);
                }
                catch
                {
                    // If move fails (locked, antivirus, etc.), fall back to using tmp wav.
                }
            }

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

    private static void TryCleanupTempFiles()
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "AiSubtitlePro");
            if (!Directory.Exists(tempDir))
                return;

            var maxAge = TimeSpan.FromDays(7);
            var cutoffUtc = DateTime.UtcNow - maxAge;

            var files = Directory.EnumerateFiles(tempDir, "*", SearchOption.TopDirectoryOnly)
                .Select(p =>
                {
                    try
                    {
                        var fi = new FileInfo(p);
                        return (path: p, lastWriteUtc: fi.LastWriteTimeUtc, length: fi.Exists ? fi.Length : 0L);
                    }
                    catch
                    {
                        return (path: p, lastWriteUtc: DateTime.MinValue, length: 0L);
                    }
                })
                .ToList();

            foreach (var f in files)
            {
                if (f.lastWriteUtc != DateTime.MinValue && f.lastWriteUtc < cutoffUtc)
                {
                    try { File.Delete(f.path); } catch { }
                }
            }

            const int maxFilesToKeep = 200;
            var remaining = Directory.EnumerateFiles(tempDir, "*", SearchOption.TopDirectoryOnly)
                .Select(p =>
                {
                    try
                    {
                        var fi = new FileInfo(p);
                        return (path: p, lastWriteUtc: fi.LastWriteTimeUtc);
                    }
                    catch
                    {
                        return (path: p, lastWriteUtc: DateTime.MinValue);
                    }
                })
                .OrderByDescending(x => x.lastWriteUtc)
                .ToList();

            if (remaining.Count > maxFilesToKeep)
            {
                foreach (var f in remaining.Skip(maxFilesToKeep))
                {
                    try { File.Delete(f.path); } catch { }
                }
            }
        }
        catch
        {
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

            var openedMedia = false;
            if (string.IsNullOrWhiteSpace(MediaFilePath) || !File.Exists(MediaFilePath))
            {
                string? associated = null;
                if (string.Equals(Path.GetExtension(filePath), ".ass", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetExtension(filePath), ".ssa", StringComparison.OrdinalIgnoreCase))
                {
                    associated = TryGetAegisubVideoFilePath(filePath);
                }

                associated ??= FindAssociatedMediaPath(filePath);
                if (!string.IsNullOrWhiteSpace(associated) && File.Exists(associated))
                {
                    MediaFilePath = associated;

                    // Reset cut range when opening new media.
                    CutStartAbs = TimeSpan.Zero;
                    CutEndAbs = TimeSpan.Zero;
                    _cutSourceDocument = null;

                    await GenerateWaveformAsync(MediaFilePath);
                    openedMedia = true;
                }
                else
                {
                    MessageBox.Show("No associated media file was found next to this subtitle file. You can open media manually via Open Media...", "Media not found", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }

            StatusMessage = openedMedia
                ? $"Loaded {CurrentDocument.Lines.Count} lines from {Path.GetFileName(filePath)} (media auto-opened)"
                : $"Loaded {CurrentDocument.Lines.Count} lines from {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading file: {ex.Message}";
            MessageBox.Show($"Failed to load subtitle file:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string? FindAssociatedMediaPath(string subtitlePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(subtitlePath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return null;

            var baseName = Path.GetFileNameWithoutExtension(subtitlePath);
            if (string.IsNullOrWhiteSpace(baseName))
                return null;

            var exts = new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm" };
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, baseName + ext);
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
        catch
        {
            return null;
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

            // For ASS/SSA, persist Aegisub-style video link (Video File:) when a media is loaded.
            if ((ext == ".ass" || ext == ".ssa") && !string.IsNullOrWhiteSpace(MediaFilePath) && File.Exists(MediaFilePath))
            {
                var ass = _assParser.Serialize(CurrentDocument);
                ass = UpsertAegisubVideoFile(ass, subtitlePath: filePath, mediaPath: MediaFilePath);
                await File.WriteAllTextAsync(filePath, ass, new UTF8Encoding(true));
                CurrentDocument.FilePath = filePath;
                CurrentDocument.IsDirty = false;
            }
            else
            {
                await parser.SaveFileAsync(CurrentDocument, filePath);
            }
            StatusMessage = $"Saved to {Path.GetFileName(filePath)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error saving file: {ex.Message}";
            MessageBox.Show($"Failed to save subtitle file:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string UpsertAegisubVideoFile(string assContent, string subtitlePath, string mediaPath)
    {
        if (string.IsNullOrWhiteSpace(assContent))
            return assContent;

        var dir = Path.GetDirectoryName(subtitlePath) ?? string.Empty;
        var videoValue = mediaPath;

        try
        {
            if (!string.IsNullOrWhiteSpace(dir))
            {
                var relative = Path.GetRelativePath(dir, mediaPath);
                // Keep relative paths if they don't traverse to different drive.
                if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                    videoValue = relative;
            }
        }
        catch
        {
        }

        // Ensure we have an Aegisub section containing Video File.
        var lines = assContent.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None).ToList();
        var inSection = false;
        var sectionStart = -1;
        var videoLineIndex = -1;

        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("[", StringComparison.Ordinal) && t.EndsWith("]", StringComparison.Ordinal))
            {
                inSection = string.Equals(t, "[Aegisub Project Garbage]", StringComparison.OrdinalIgnoreCase);
                if (inSection) sectionStart = i;
                continue;
            }

            if (!inSection) continue;

            if (t.StartsWith("Video File:", StringComparison.OrdinalIgnoreCase))
            {
                videoLineIndex = i;
                break;
            }
        }

        var newVideoLine = $"Video File: {videoValue}";

        if (videoLineIndex >= 0)
        {
            lines[videoLineIndex] = newVideoLine;
        }
        else if (sectionStart >= 0)
        {
            // Insert right after the section header.
            lines.Insert(sectionStart + 1, newVideoLine);
        }
        else
        {
            // Append new section at end.
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add(string.Empty);
            lines.Add("[Aegisub Project Garbage]");
            lines.Add(newVideoLine);
        }

        return string.Join(Environment.NewLine, lines);
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

            string tempAssPath = string.Empty;
            try
            {
                _ffmpegOperation = FfmpegOperation.ExportHardSub;
                _ffmpegOperationStartedUtc = DateTime.UtcNow;
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

                // Options dialog (vertical blur + preview)
                var optionsDlg = new ExportHardSubOptionsDialog(
                    mediaPath: mediaPath,
                    assPath: tempAssPath,
                    previewTime: ToMediaTime(CurrentPosition));
                optionsDlg.ExportVerticalInitial = _exportHardSubLastVertical;
                optionsDlg.BlurSigmaInitial = _exportHardSubLastBlurSigma;
                optionsDlg.EnableTrailerInitial = _exportHardSubLastEnableTrailer;
                optionsDlg.TrailerStartInitial = _exportHardSubLastTrailerStart;
                optionsDlg.TrailerDurationInitial = _exportHardSubLastTrailerDuration;
                optionsDlg.TrailerTransitionInitial = _exportHardSubLastTrailerTransition;
                optionsDlg.Owner = Application.Current.MainWindow;
                if (optionsDlg.ShowDialog() != true)
                    return;

                _exportHardSubLastVertical = optionsDlg.ExportVertical;
                _exportHardSubLastBlurSigma = optionsDlg.BlurSigma;
                _exportHardSubLastEnableTrailer = optionsDlg.EnableTrailer;
                _exportHardSubLastTrailerStart = optionsDlg.TrailerStart;
                _exportHardSubLastTrailerDuration = optionsDlg.TrailerDuration;
                _exportHardSubLastTrailerTransition = optionsDlg.TrailerTransition;

                var saveDlg = new SaveFileDialog
                {
                    Title = "Export Video (Hard-Sub) - Render subtitles into video",
                    FileName = defaultName,
                    Filter = "Video Files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm|All Files|*.*"
                };

                if (saveDlg.ShowDialog() != true)
                    return;

                var outputPath = saveDlg.FileName;
                string? verticalOutputPath = null;
                if (optionsDlg.ExportVertical)
                {
                    var dir = Path.GetDirectoryName(outputPath) ?? string.Empty;
                    var name = Path.GetFileNameWithoutExtension(outputPath);
                    var ext = Path.GetExtension(outputPath);
                    verticalOutputPath = Path.Combine(dir, name + "_vertical" + ext);
                }

                var finalOutputPath = outputPath;
                string? tempMainPath = null;
                if (optionsDlg.EnableTrailer)
                {
                    var tempDir2 = Path.Combine(Path.GetTempPath(), "AiSubtitlePro");
                    Directory.CreateDirectory(tempDir2);
                    tempMainPath = Path.Combine(tempDir2, $"main_hardsub_{Guid.NewGuid():N}{Path.GetExtension(outputPath)}");
                }

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
                        tempMainPath ?? outputPath,
                        start: startAbs,
                        duration: segDuration,
                        preferGpuEncoding: true,
                        cancellationToken: _exportCts.Token);

                    if (optionsDlg.EnableTrailer && !string.IsNullOrWhiteSpace(tempMainPath))
                    {
                        FfmpegProgressText = "Prepending trailer";
                        StatusMessage = "Prepending trailer...";
                        await _ffmpegService.PrependTrailerWithTransitionAsync(
                            trailerVideoPath: tempMainPath,
                            trailerStart: optionsDlg.TrailerStart,
                            trailerDuration: optionsDlg.TrailerDuration,
                            mainVideoPath: tempMainPath,
                            outputVideoPath: finalOutputPath,
                            transitionDuration: optionsDlg.TrailerTransition,
                            preferGpuEncoding: true,
                            cancellationToken: _exportCts.Token);
                    }
                }
                else
                {
                    await _ffmpegService.RenderHardSubAsync(mediaPath, tempAssPath, tempMainPath ?? outputPath, preferGpuEncoding: true, _exportCts.Token);

                    if (optionsDlg.EnableTrailer && !string.IsNullOrWhiteSpace(tempMainPath))
                    {
                        FfmpegProgressText = "Prepending trailer";
                        StatusMessage = "Prepending trailer...";
                        await _ffmpegService.PrependTrailerWithTransitionAsync(
                            trailerVideoPath: tempMainPath,
                            trailerStart: optionsDlg.TrailerStart,
                            trailerDuration: optionsDlg.TrailerDuration,
                            mainVideoPath: tempMainPath,
                            outputVideoPath: finalOutputPath,
                            transitionDuration: optionsDlg.TrailerTransition,
                            preferGpuEncoding: true,
                            cancellationToken: _exportCts.Token);
                    }
                }

                if (!string.IsNullOrWhiteSpace(verticalOutputPath))
                {
                    FfmpegProgressText = "Exporting vertical blur video";
                    StatusMessage = "Exporting vertical blur video...";
                    await _ffmpegService.ConvertToVerticalBlurAsync(
                        inputVideoPath: finalOutputPath,
                        outputVideoPath: verticalOutputPath,
                        width: 1080,
                        height: 1920,
                        blurSigma: optionsDlg.BlurSigma,
                        preferGpuEncoding: true,
                        cancellationToken: _exportCts.Token);
                }

                if (!string.IsNullOrWhiteSpace(tempMainPath))
                {
                    try
                    {
                        if (File.Exists(tempMainPath))
                            File.Delete(tempMainPath);
                    }
                    catch
                    {
                    }
                }

                var elapsed = DateTime.UtcNow - _ffmpegOperationStartedUtc;
                StatusMessage = !string.IsNullOrWhiteSpace(verticalOutputPath)
                    ? $"Export complete: {Path.GetFileName(outputPath)} + {Path.GetFileName(verticalOutputPath)} (elapsed {FormatElapsed(elapsed)})"
                    : $"Export complete: {Path.GetFileName(outputPath)} (elapsed {FormatElapsed(elapsed)})";
            }
            catch (OperationCanceledException)
            {
                var elapsed = DateTime.UtcNow - _ffmpegOperationStartedUtc;
                StatusMessage = $"Export canceled (elapsed {FormatElapsed(elapsed)})";
            }
            catch (Exception ex)
            {
                var elapsed = DateTime.UtcNow - _ffmpegOperationStartedUtc;
                StatusMessage = $"Export error: {ex.Message} (elapsed {FormatElapsed(elapsed)})";
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

        // Insert after selected line or at end.
        // New line timing: Start = End of previous line; End = Start + 2 seconds.
        var insertIndex = SelectedLine != null
            ? CurrentDocument.Lines.IndexOf(SelectedLine) + 1
            : CurrentDocument.Lines.Count;

        SubtitleLine? anchor = null;
        if (SelectedLine != null)
        {
            anchor = SelectedLine;
        }
        else if (doc.Lines.Count > 0)
        {
            anchor = doc.Lines[^1];
        }

        var start = anchor != null
            ? ClampTimelineTime(anchor.End)
            : ClampTimelineTime(CurrentPosition);
        var end = ClampTimelineTime(start + TimeSpan.FromSeconds(2));
        if (end < start) end = start;

        var newLine = new SubtitleLine
        {
            Start = start,
            End = end,
            Text = ""
        };

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
        if (CurrentDocument == null) return;

        var doc = CurrentDocument;

        var selectedLines = DisplayedLines.Where(l => l.IsSelected).ToList();
        if (selectedLines.Count == 0 && SelectedLine != null)
        {
            selectedLines.Add(SelectedLine);
        }

        if (selectedLines.Count == 0)
            return;

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
                var rt = string.IsNullOrWhiteSpace(wizard.RuntimeUsed) ? "" : $" ({wizard.RuntimeUsed})";
                var elapsed = wizard.Elapsed > TimeSpan.Zero ? $" (elapsed {FormatElapsed(wizard.Elapsed)})" : string.Empty;
                StatusMessage = $"Transcription complete: {CurrentDocument.Lines.Count} lines generated{rt}{elapsed}";
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
