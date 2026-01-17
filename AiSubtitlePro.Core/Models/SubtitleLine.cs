using AiSubtitlePro.Core.Models.Enums;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AiSubtitlePro.Core.Models;

/// <summary>
/// Represents a single subtitle line/event with timing and text content.
/// Implements INotifyPropertyChanged for UI binding.
/// </summary>
public class SubtitleLine : INotifyPropertyChanged
{
    private int _index;
    private TimeSpan _start;
    private TimeSpan _end;
    private string _text = string.Empty;
    private string _styleName = "Default";
    private string _actor = string.Empty;
    private string _effect = string.Empty;
    private int _layer;
    private int _marginL;
    private int _marginR;
    private int _marginV;
    private int? _posX;
    private int? _posY;
    private DialogueType _type = DialogueType.Dialogue;
    private bool _isSelected;
    private bool _useStyleOverride;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Line index (1-based for display)
    /// </summary>
    public int Index
    {
        get => _index;
        set => SetProperty(ref _index, value);
    }

    /// <summary>
    /// Start time of the subtitle
    /// </summary>
    public TimeSpan Start
    {
        get => _start;
        set => SetProperty(ref _start, value);
    }

    /// <summary>
    /// End time of the subtitle
    /// </summary>
    public TimeSpan End
    {
        get => _end;
        set => SetProperty(ref _end, value);
    }

    /// <summary>
    /// Duration of the subtitle
    /// </summary>
    public TimeSpan Duration => End - Start;

    /// <summary>
    /// Text content (may include ASS override tags)
    /// </summary>
    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    /// <summary>
    /// Plain text without ASS override tags
    /// </summary>
    public string PlainText => RemoveAssTags(Text);

    /// <summary>
    /// Style name (references SubtitleStyle.Name)
    /// </summary>
    public string StyleName
    {
        get => _styleName;
        set => SetProperty(ref _styleName, value);
    }

    /// <summary>
    /// Actor/speaker name (ASS)
    /// </summary>
    public string Actor
    {
        get => _actor;
        set => SetProperty(ref _actor, value);
    }

    /// <summary>
    /// Effect name (ASS)
    /// </summary>
    public string Effect
    {
        get => _effect;
        set => SetProperty(ref _effect, value);
    }

    /// <summary>
    /// Layer number for overlapping subtitles
    /// </summary>
    public int Layer
    {
        get => _layer;
        set => SetProperty(ref _layer, value);
    }

    /// <summary>
    /// Left margin override (0 = use style default)
    /// </summary>
    public int MarginL
    {
        get => _marginL;
        set => SetProperty(ref _marginL, value);
    }

    /// <summary>
    /// Right margin override
    /// </summary>
    public int MarginR
    {
        get => _marginR;
        set => SetProperty(ref _marginR, value);
    }

    /// <summary>
    /// Vertical margin override
    /// </summary>
    public int MarginV
    {
        get => _marginV;
        set => SetProperty(ref _marginV, value);
    }

    public int? PosX
    {
        get => _posX;
        set => SetProperty(ref _posX, value);
    }

    public int? PosY
    {
        get => _posY;
        set => SetProperty(ref _posY, value);
    }

    /// <summary>
    /// Dialogue type (Dialogue or Comment)
    /// </summary>
    public DialogueType Type
    {
        get => _type;
        set => SetProperty(ref _type, value);
    }

    /// <summary>
    /// Whether this line is selected in the grid
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool UseStyleOverride
    {
        get => _useStyleOverride;
        set => SetProperty(ref _useStyleOverride, value);
    }

    /// <summary>
    /// Characters per second (reading speed metric)
    /// </summary>
    public double Cps
    {
        get
        {
            var duration = Duration.TotalSeconds;
            if (duration <= 0) return 0;
            return PlainText.Length / duration;
        }
    }

    /// <summary>
    /// Creates a deep copy of this subtitle line
    /// </summary>
    public SubtitleLine Clone()
    {
        return new SubtitleLine
        {
            Index = Index,
            Start = Start,
            End = End,
            Text = Text,
            StyleName = StyleName,
            Actor = Actor,
            Effect = Effect,
            Layer = Layer,
            MarginL = MarginL,
            MarginR = MarginR,
            MarginV = MarginV,
            PosX = PosX,
            PosY = PosY,
            Type = Type,
            UseStyleOverride = UseStyleOverride
        };
    }

    /// <summary>
    /// Removes ASS override tags from text
    /// </summary>
    private static string RemoveAssTags(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // Remove {...} blocks
        var result = System.Text.RegularExpressions.Regex.Replace(text, @"\{[^}]*\}", "");
        // Replace \N and \n with space for CPS calculation
        result = result.Replace("\\N", " ").Replace("\\n", " ");
        return result.Trim();
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        
        // Notify dependent properties
        if (propertyName == nameof(Start) || propertyName == nameof(End))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Duration)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cps)));
        }
        if (propertyName == nameof(Text))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PlainText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Cps)));
        }
        
        return true;
    }
}
