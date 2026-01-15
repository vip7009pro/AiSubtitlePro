using AiSubtitlePro.Core.Models;
using AiSubtitlePro.Core.Models.Enums;
using System.Text;
using System.Text.RegularExpressions;

namespace AiSubtitlePro.Core.Parsers;

/// <summary>
/// Parser for WebVTT subtitle format.
/// </summary>
public class VttParser : ISubtitleParser
{
    public SubtitleFormat Format => SubtitleFormat.Vtt;
    
    public IReadOnlyList<string> SupportedExtensions => new[] { ".vtt" };

    private static readonly Regex TimeLineRegex = new(
        @"(\d{2}):(\d{2}):(\d{2})\.(\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2})\.(\d{3})",
        RegexOptions.Compiled);
    
    private static readonly Regex ShortTimeLineRegex = new(
        @"(\d{2}):(\d{2})\.(\d{3})\s*-->\s*(\d{2}):(\d{2})\.(\d{3})",
        RegexOptions.Compiled);

    public SubtitleDocument Parse(string content)
    {
        var document = new SubtitleDocument { Format = SubtitleFormat.Vtt };
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        
        int i = 0;
        int subtitleIndex = 1;

        // Skip WEBVTT header
        while (i < lines.Length && !lines[i].StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
            i++;
        i++; // Skip WEBVTT line

        // Skip header metadata
        while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            i++;

        while (i < lines.Length)
        {
            // Skip empty lines
            while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i]))
                i++;

            if (i >= lines.Length) break;

            // Check for cue identifier (optional)
            string? cueId = null;
            if (!lines[i].Contains("-->"))
            {
                cueId = lines[i].Trim();
                i++;
                if (i >= lines.Length) break;
            }

            // Parse timing line
            TimeSpan start, end;
            var timeMatch = TimeLineRegex.Match(lines[i]);
            if (timeMatch.Success)
            {
                start = new TimeSpan(0,
                    int.Parse(timeMatch.Groups[1].Value),
                    int.Parse(timeMatch.Groups[2].Value),
                    int.Parse(timeMatch.Groups[3].Value),
                    int.Parse(timeMatch.Groups[4].Value));

                end = new TimeSpan(0,
                    int.Parse(timeMatch.Groups[5].Value),
                    int.Parse(timeMatch.Groups[6].Value),
                    int.Parse(timeMatch.Groups[7].Value),
                    int.Parse(timeMatch.Groups[8].Value));
            }
            else
            {
                // Try short format (mm:ss.fff)
                var shortMatch = ShortTimeLineRegex.Match(lines[i]);
                if (shortMatch.Success)
                {
                    start = new TimeSpan(0, 0,
                        int.Parse(shortMatch.Groups[1].Value),
                        int.Parse(shortMatch.Groups[2].Value),
                        int.Parse(shortMatch.Groups[3].Value));

                    end = new TimeSpan(0, 0,
                        int.Parse(shortMatch.Groups[4].Value),
                        int.Parse(shortMatch.Groups[5].Value),
                        int.Parse(shortMatch.Groups[6].Value));
                }
                else
                {
                    i++;
                    continue;
                }
            }

            i++;

            // Parse text (until empty line or end)
            var textBuilder = new StringBuilder();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                if (textBuilder.Length > 0)
                    textBuilder.Append("\\N");
                textBuilder.Append(lines[i].Trim());
                i++;
            }

            // Create subtitle line
            var subtitleLine = new SubtitleLine
            {
                Index = subtitleIndex++,
                Start = start,
                End = end,
                Text = ConvertVttToAss(textBuilder.ToString()),
                Actor = cueId ?? string.Empty
            };

            document.Lines.Add(subtitleLine);
        }

        return document;
    }

    public async Task<SubtitleDocument> ParseFileAsync(string filePath)
    {
        var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
        var document = Parse(content);
        document.FilePath = filePath;
        return document;
    }

    public string Serialize(SubtitleDocument document)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();

        foreach (var line in document.Lines)
        {
            // Optional cue identifier
            if (!string.IsNullOrEmpty(line.Actor))
            {
                sb.AppendLine(line.Actor);
            }

            sb.AppendLine($"{FormatTime(line.Start)} --> {FormatTime(line.End)}");
            
            // Convert ASS formatting to VTT
            var text = ConvertAssToVtt(line.Text);
            sb.AppendLine(text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public async Task SaveFileAsync(SubtitleDocument document, string filePath)
    {
        var content = Serialize(document);
        await File.WriteAllTextAsync(filePath, content, new UTF8Encoding(false));
        document.FilePath = filePath;
        document.IsDirty = false;
    }

    private static string FormatTime(TimeSpan time)
    {
        return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}.{time.Milliseconds:D3}";
    }

    private static string ConvertVttToAss(string text)
    {
        // Convert VTT tags to ASS equivalents
        text = Regex.Replace(text, @"<b>", "{\\b1}");
        text = Regex.Replace(text, @"</b>", "{\\b0}");
        text = Regex.Replace(text, @"<i>", "{\\i1}");
        text = Regex.Replace(text, @"</i>", "{\\i0}");
        text = Regex.Replace(text, @"<u>", "{\\u1}");
        text = Regex.Replace(text, @"</u>", "{\\u0}");
        
        // Remove other VTT tags
        text = Regex.Replace(text, @"<[^>]+>", "");
        
        return text;
    }

    private static string ConvertAssToVtt(string text)
    {
        // Replace ASS line breaks
        text = text.Replace("\\N", "\n").Replace("\\n", "\n");
        
        // Convert basic ASS tags to VTT
        text = Regex.Replace(text, @"\{\\b1\}", "<b>");
        text = Regex.Replace(text, @"\{\\b0\}", "</b>");
        text = Regex.Replace(text, @"\{\\i1\}", "<i>");
        text = Regex.Replace(text, @"\{\\i0\}", "</i>");
        text = Regex.Replace(text, @"\{\\u1\}", "<u>");
        text = Regex.Replace(text, @"\{\\u0\}", "</u>");
        
        // Remove other ASS tags
        text = Regex.Replace(text, @"\{[^}]*\}", "");
        
        return text;
    }
}
