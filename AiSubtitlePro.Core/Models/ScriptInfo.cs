namespace AiSubtitlePro.Core.Models;

/// <summary>
/// ASS Script Info section containing metadata
/// </summary>
public class ScriptInfo
{
    /// <summary>
    /// Script title
    /// </summary>
    public string Title { get; set; } = "Untitled";

    /// <summary>
    /// Original script author
    /// </summary>
    public string OriginalScript { get; set; } = string.Empty;

    /// <summary>
    /// Original translation author
    /// </summary>
    public string OriginalTranslation { get; set; } = string.Empty;

    /// <summary>
    /// Original editing author
    /// </summary>
    public string OriginalEditing { get; set; } = string.Empty;

    /// <summary>
    /// Original timing author
    /// </summary>
    public string OriginalTiming { get; set; } = string.Empty;

    /// <summary>
    /// Synch point for timing
    /// </summary>
    public string SynchPoint { get; set; } = string.Empty;

    /// <summary>
    /// Script updated by
    /// </summary>
    public string ScriptUpdatedBy { get; set; } = string.Empty;

    /// <summary>
    /// Update details
    /// </summary>
    public string UpdateDetails { get; set; } = string.Empty;

    /// <summary>
    /// Script type (v4.00+ for ASS)
    /// </summary>
    public string ScriptType { get; set; } = "v4.00+";

    /// <summary>
    /// Collisions handling (Normal or Reverse)
    /// </summary>
    public string Collisions { get; set; } = "Normal";

    /// <summary>
    /// Video playback resolution width
    /// </summary>
    public int PlayResX { get; set; } = 1920;

    /// <summary>
    /// Video playback resolution height
    /// </summary>
    public int PlayResY { get; set; } = 1080;

    /// <summary>
    /// Playback speed (100 = normal)
    /// </summary>
    public double PlayDepth { get; set; } = 0;

    /// <summary>
    /// Timer value (100 = normal speed)
    /// </summary>
    public double Timer { get; set; } = 100.0000;

    /// <summary>
    /// Wrap style (0-3)
    /// 0 = Smart wrapping, 1 = No smart wrap, 2 = No soft wrap, 3 = Same as 0 but lower
    /// </summary>
    public int WrapStyle { get; set; } = 0;

    /// <summary>
    /// Whether the script uses scaledBorderAndShadow
    /// </summary>
    public bool ScaledBorderAndShadow { get; set; } = true;

    /// <summary>
    /// YCbCr matrix (e.g., "None", "TV.601", "PC.709")
    /// </summary>
    public string YCbCrMatrix { get; set; } = "None";

    /// <summary>
    /// Custom/unknown properties
    /// </summary>
    public Dictionary<string, string> CustomProperties { get; set; } = new();
}
