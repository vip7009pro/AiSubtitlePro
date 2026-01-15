using System.Drawing;

namespace AiSubtitlePro.Core.Models;

/// <summary>
/// Represents an ASS/SSA subtitle style with all formatting properties.
/// </summary>
public class SubtitleStyle
{
    /// <summary>
    /// Style name (referenced by dialogue lines)
    /// </summary>
    public string Name { get; set; } = "Default";

    /// <summary>
    /// Font family name
    /// </summary>
    public string FontName { get; set; } = "Arial";

    /// <summary>
    /// Font size in points
    /// </summary>
    public double FontSize { get; set; } = 48;

    /// <summary>
    /// Primary (fill) color in ABGR format
    /// </summary>
    public Color PrimaryColor { get; set; } = Color.White;

    /// <summary>
    /// Secondary color (for karaoke effects)
    /// </summary>
    public Color SecondaryColor { get; set; } = Color.Red;

    /// <summary>
    /// Outline color
    /// </summary>
    public Color OutlineColor { get; set; } = Color.Black;

    /// <summary>
    /// Shadow color
    /// </summary>
    public Color BackColor { get; set; } = Color.Black;

    /// <summary>
    /// Bold weight (-1 = true, 0 = false)
    /// </summary>
    public bool Bold { get; set; }

    /// <summary>
    /// Italic style
    /// </summary>
    public bool Italic { get; set; }

    /// <summary>
    /// Underline decoration
    /// </summary>
    public bool Underline { get; set; }

    /// <summary>
    /// Strikeout decoration
    /// </summary>
    public bool StrikeOut { get; set; }

    /// <summary>
    /// Horizontal scaling percentage (100 = normal)
    /// </summary>
    public double ScaleX { get; set; } = 100;

    /// <summary>
    /// Vertical scaling percentage (100 = normal)
    /// </summary>
    public double ScaleY { get; set; } = 100;

    /// <summary>
    /// Character spacing in pixels
    /// </summary>
    public double Spacing { get; set; }

    /// <summary>
    /// Rotation angle in degrees
    /// </summary>
    public double Angle { get; set; }

    /// <summary>
    /// Border style (1 = outline + shadow, 3 = opaque box)
    /// </summary>
    public int BorderStyle { get; set; } = 1;

    /// <summary>
    /// Outline thickness in pixels
    /// </summary>
    public double Outline { get; set; } = 2;

    /// <summary>
    /// Shadow depth in pixels
    /// </summary>
    public double Shadow { get; set; } = 2;

    /// <summary>
    /// Text alignment (numpad style: 1-9)
    /// </summary>
    public int Alignment { get; set; } = 2;

    /// <summary>
    /// Left margin in pixels
    /// </summary>
    public int MarginL { get; set; } = 10;

    /// <summary>
    /// Right margin in pixels
    /// </summary>
    public int MarginR { get; set; } = 10;

    /// <summary>
    /// Vertical margin in pixels
    /// </summary>
    public int MarginV { get; set; } = 10;

    /// <summary>
    /// Encoding (usually 1 for default)
    /// </summary>
    public int Encoding { get; set; } = 1;

    /// <summary>
    /// Creates a deep copy of this style
    /// </summary>
    public SubtitleStyle Clone()
    {
        return (SubtitleStyle)MemberwiseClone();
    }

    /// <summary>
    /// Converts color to ASS format (&HAABBGGRR)
    /// </summary>
    public static string ColorToAss(Color color)
    {
        return $"&H{color.A:X2}{color.B:X2}{color.G:X2}{color.R:X2}";
    }

    /// <summary>
    /// Parses ASS color format (&HAABBGGRR or &HBBGGRR)
    /// </summary>
    public static Color AssToColor(string assColor)
    {
        if (string.IsNullOrEmpty(assColor))
            return Color.White;

        var hex = assColor.Replace("&H", "").Replace("&h", "").TrimEnd('&');
        
        try
        {
            if (hex.Length == 6)
            {
                // &HBBGGRR format
                var b = Convert.ToInt32(hex.Substring(0, 2), 16);
                var g = Convert.ToInt32(hex.Substring(2, 2), 16);
                var r = Convert.ToInt32(hex.Substring(4, 2), 16);
                return Color.FromArgb(255, r, g, b);
            }
            else if (hex.Length == 8)
            {
                // &HAABBGGRR format
                var a = 255 - Convert.ToInt32(hex.Substring(0, 2), 16);
                var b = Convert.ToInt32(hex.Substring(2, 2), 16);
                var g = Convert.ToInt32(hex.Substring(4, 2), 16);
                var r = Convert.ToInt32(hex.Substring(6, 2), 16);
                return Color.FromArgb(a, r, g, b);
            }
        }
        catch
        {
            // Return default on parse error
        }

        return Color.White;
    }
}
