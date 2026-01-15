namespace AiSubtitlePro.Core.Models.Enums;

/// <summary>
/// Supported subtitle file formats
/// </summary>
public enum SubtitleFormat
{
    Unknown,
    Ass,        // Advanced SubStation Alpha (.ass)
    Ssa,        // SubStation Alpha (.ssa)
    Srt,        // SubRip (.srt)
    Vtt         // WebVTT (.vtt)
}

/// <summary>
/// ASS text alignment (numpad style)
/// </summary>
public enum SubtitleAlignment
{
    BottomLeft = 1,
    BottomCenter = 2,
    BottomRight = 3,
    MiddleLeft = 4,
    MiddleCenter = 5,
    MiddleRight = 6,
    TopLeft = 7,
    TopCenter = 8,
    TopRight = 9
}

/// <summary>
/// ASS border style
/// </summary>
public enum BorderStyle
{
    OutlineAndDropShadow = 1,
    OpaqueBox = 3
}

/// <summary>
/// Subtitle line type for ASS format
/// </summary>
public enum DialogueType
{
    Dialogue,
    Comment
}
