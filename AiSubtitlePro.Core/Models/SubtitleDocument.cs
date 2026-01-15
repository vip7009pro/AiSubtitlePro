using AiSubtitlePro.Core.Models.Enums;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AiSubtitlePro.Core.Models;

/// <summary>
/// Represents a complete subtitle document with all metadata, styles, and lines.
/// This is the main model for editing subtitles.
/// </summary>
public class SubtitleDocument : INotifyPropertyChanged
{
    private string _filePath = string.Empty;
    private SubtitleFormat _format = SubtitleFormat.Ass;
    private bool _isDirty;
    private ScriptInfo _scriptInfo = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Full file path of the document
    /// </summary>
    public string FilePath
    {
        get => _filePath;
        set => SetProperty(ref _filePath, value);
    }

    /// <summary>
    /// File name without path
    /// </summary>
    public string FileName => string.IsNullOrEmpty(FilePath) 
        ? "Untitled" 
        : Path.GetFileName(FilePath);

    /// <summary>
    /// Subtitle format (ASS, SRT, VTT, etc.)
    /// </summary>
    public SubtitleFormat Format
    {
        get => _format;
        set => SetProperty(ref _format, value);
    }

    /// <summary>
    /// Whether the document has unsaved changes
    /// </summary>
    public bool IsDirty
    {
        get => _isDirty;
        set => SetProperty(ref _isDirty, value);
    }

    /// <summary>
    /// Script metadata (ASS [Script Info] section)
    /// </summary>
    public ScriptInfo ScriptInfo
    {
        get => _scriptInfo;
        set => SetProperty(ref _scriptInfo, value);
    }

    /// <summary>
    /// Collection of subtitle styles
    /// </summary>
    public ObservableCollection<SubtitleStyle> Styles { get; } = new()
    {
        new SubtitleStyle { Name = "Default" }
    };

    /// <summary>
    /// Collection of subtitle lines/events
    /// </summary>
    public ObservableCollection<SubtitleLine> Lines { get; } = new();

    /// <summary>
    /// Total duration (end time of last subtitle)
    /// </summary>
    public TimeSpan TotalDuration => Lines.Count > 0 
        ? Lines.Max(l => l.End) 
        : TimeSpan.Zero;

    /// <summary>
    /// Creates a new empty subtitle document
    /// </summary>
    public static SubtitleDocument CreateNew()
    {
        return new SubtitleDocument
        {
            Format = SubtitleFormat.Ass,
            ScriptInfo = new ScriptInfo
            {
                Title = "New Subtitle",
                ScriptType = "v4.00+",
                PlayResX = 1920,
                PlayResY = 1080
            }
        };
    }

    /// <summary>
    /// Gets style by name, returns default if not found
    /// </summary>
    public SubtitleStyle GetStyle(string name)
    {
        return Styles.FirstOrDefault(s => s.Name == name) 
            ?? Styles.FirstOrDefault() 
            ?? new SubtitleStyle();
    }

    /// <summary>
    /// Adds a new subtitle line and marks document as dirty
    /// </summary>
    public void AddLine(SubtitleLine line)
    {
        line.Index = Lines.Count + 1;
        Lines.Add(line);
        IsDirty = true;
    }

    /// <summary>
    /// Removes a subtitle line and reindexes
    /// </summary>
    public void RemoveLine(SubtitleLine line)
    {
        Lines.Remove(line);
        ReindexLines();
        IsDirty = true;
    }

    /// <summary>
    /// Reindexes all lines (1-based)
    /// </summary>
    public void ReindexLines()
    {
        for (int i = 0; i < Lines.Count; i++)
        {
            Lines[i].Index = i + 1;
        }
    }

    /// <summary>
    /// Sorts lines by start time
    /// </summary>
    public void SortByTime()
    {
        var sorted = Lines.OrderBy(l => l.Start).ToList();
        Lines.Clear();
        foreach (var line in sorted)
        {
            Lines.Add(line);
        }
        ReindexLines();
        IsDirty = true;
    }

    /// <summary>
    /// Gets lines that are active at a specific time
    /// </summary>
    public IEnumerable<SubtitleLine> GetLinesAtTime(TimeSpan time)
    {
        return Lines.Where(l => l.Start <= time && l.End >= time);
    }

    /// <summary>
    /// Creates a deep copy of the document
    /// </summary>
    public SubtitleDocument Clone()
    {
        var clone = new SubtitleDocument
        {
            FilePath = FilePath,
            Format = Format,
            ScriptInfo = new ScriptInfo
            {
                Title = ScriptInfo.Title,
                PlayResX = ScriptInfo.PlayResX,
                PlayResY = ScriptInfo.PlayResY,
                ScriptType = ScriptInfo.ScriptType,
                WrapStyle = ScriptInfo.WrapStyle,
                ScaledBorderAndShadow = ScriptInfo.ScaledBorderAndShadow
            }
        };

        foreach (var style in Styles)
        {
            clone.Styles.Add(style.Clone());
        }

        foreach (var line in Lines)
        {
            clone.Lines.Add(line.Clone());
        }

        return clone;
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        
        if (propertyName == nameof(FilePath))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileName)));
        }
        
        return true;
    }
}
