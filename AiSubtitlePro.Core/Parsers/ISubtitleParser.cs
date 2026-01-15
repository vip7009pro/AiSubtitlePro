using AiSubtitlePro.Core.Models;
using AiSubtitlePro.Core.Models.Enums;

namespace AiSubtitlePro.Core.Parsers;

/// <summary>
/// Interface for subtitle format parsers
/// </summary>
public interface ISubtitleParser
{
    /// <summary>
    /// Supported format for this parser
    /// </summary>
    SubtitleFormat Format { get; }

    /// <summary>
    /// File extensions handled by this parser (e.g., ".ass", ".ssa")
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Parses subtitle content from a string
    /// </summary>
    /// <param name="content">Raw file content</param>
    /// <returns>Parsed subtitle document</returns>
    SubtitleDocument Parse(string content);

    /// <summary>
    /// Parses subtitle content from a file
    /// </summary>
    /// <param name="filePath">Path to subtitle file</param>
    /// <returns>Parsed subtitle document</returns>
    Task<SubtitleDocument> ParseFileAsync(string filePath);

    /// <summary>
    /// Serializes a subtitle document to string
    /// </summary>
    /// <param name="document">Document to serialize</param>
    /// <returns>Formatted subtitle content</returns>
    string Serialize(SubtitleDocument document);

    /// <summary>
    /// Saves a subtitle document to file
    /// </summary>
    /// <param name="document">Document to save</param>
    /// <param name="filePath">Target file path</param>
    Task SaveFileAsync(SubtitleDocument document, string filePath);
}
