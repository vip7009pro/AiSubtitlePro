using AiSubtitlePro.Core.Models;
using AiSubtitlePro.Core.Models.Enums;
using System.Text;
using System.Text.RegularExpressions;

namespace AiSubtitlePro.Core.Parsers;

/// <summary>
/// Parser for SRT (SubRip) subtitle format.
/// </summary>
public class SrtParser : ISubtitleParser
{
    public SubtitleFormat Format => SubtitleFormat.Srt;
    
    public IReadOnlyList<string> SupportedExtensions => new[] { ".srt" };

    private static readonly Regex TimeLineRegex = new(
        @"(\d{2}):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2})[,.](\d{3})",
        RegexOptions.Compiled);

    public SubtitleDocument Parse(string content)
    {
        var document = new SubtitleDocument { Format = SubtitleFormat.Srt };
        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        
        int i = 0;
        int subtitleIndex = 1;

        while (i < lines.Length)
        {
            // Skip empty lines
            while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i]))
                i++;

            if (i >= lines.Length) break;

            // Parse subtitle number (optional, we generate our own index)
            if (int.TryParse(lines[i].Trim(), out _))
            {
                i++;
                if (i >= lines.Length) break;
            }

            // Parse timing line
            var timeMatch = TimeLineRegex.Match(lines[i]);
            if (!timeMatch.Success)
            {
                i++;
                continue;
            }

            var start = new TimeSpan(0,
                int.Parse(timeMatch.Groups[1].Value),
                int.Parse(timeMatch.Groups[2].Value),
                int.Parse(timeMatch.Groups[3].Value),
                int.Parse(timeMatch.Groups[4].Value));

            var end = new TimeSpan(0,
                int.Parse(timeMatch.Groups[5].Value),
                int.Parse(timeMatch.Groups[6].Value),
                int.Parse(timeMatch.Groups[7].Value),
                int.Parse(timeMatch.Groups[8].Value));

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
                Text = textBuilder.ToString()
            };

            document.Lines.Add(subtitleLine);
        }

        return document;
    }

    public async Task<SubtitleDocument> ParseFileAsync(string filePath)
    {
        // Try different encodings
        string content;
        try
        {
            content = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
        }
        catch
        {
            content = await File.ReadAllTextAsync(filePath, Encoding.Default);
        }

        var document = Parse(content);
        document.FilePath = filePath;
        return document;
    }

    public string Serialize(SubtitleDocument document)
    {
        var sb = new StringBuilder();

        foreach (var line in document.Lines)
        {
            sb.AppendLine(line.Index.ToString());
            sb.AppendLine($"{FormatTime(line.Start)} --> {FormatTime(line.End)}");
            
            // Convert ASS line breaks to SRT format
            var text = line.Text
                .Replace("\\N", "\r\n")
                .Replace("\\n", "\r\n");
            
            // Remove ASS tags for SRT
            text = RemoveAssTags(text);
            
            sb.AppendLine(text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public async Task SaveFileAsync(SubtitleDocument document, string filePath)
    {
        var content = Serialize(document);
        await File.WriteAllTextAsync(filePath, content, new UTF8Encoding(true));
        document.FilePath = filePath;
        document.IsDirty = false;
    }

    private static string FormatTime(TimeSpan time)
    {
        return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2},{time.Milliseconds:D3}";
    }

    private static string RemoveAssTags(string text)
    {
        // Remove {...} ASS override tags
        return Regex.Replace(text, @"\{[^}]*\}", "");
    }
}
