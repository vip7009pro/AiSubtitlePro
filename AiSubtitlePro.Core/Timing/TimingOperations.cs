using AiSubtitlePro.Core.Models;

namespace AiSubtitlePro.Core.Timing;

/// <summary>
/// Timing operations for subtitle manipulation (Aegisub-style)
/// </summary>
public static class TimingOperations
{
    /// <summary>
    /// Shifts timing of multiple lines by a fixed amount
    /// </summary>
    /// <param name="lines">Lines to shift</param>
    /// <param name="offset">Time offset (can be negative)</param>
    /// <param name="shiftStart">Whether to shift start time</param>
    /// <param name="shiftEnd">Whether to shift end time</param>
    public static void ShiftTiming(IEnumerable<SubtitleLine> lines, TimeSpan offset, bool shiftStart = true, bool shiftEnd = true)
    {
        foreach (var line in lines)
        {
            if (shiftStart)
            {
                line.Start = Max(TimeSpan.Zero, line.Start + offset);
            }
            if (shiftEnd)
            {
                line.End = Max(TimeSpan.Zero, line.End + offset);
            }
        }
    }

    /// <summary>
    /// Scales timing of lines proportionally from a reference point
    /// </summary>
    /// <param name="lines">Lines to scale</param>
    /// <param name="scaleFactor">Scale factor (1.0 = no change, 1.1 = 10% slower, 0.9 = 10% faster)</param>
    /// <param name="referenceTime">Reference point for scaling (typically start of first line)</param>
    public static void ScaleTiming(IEnumerable<SubtitleLine> lines, double scaleFactor, TimeSpan referenceTime)
    {
        foreach (var line in lines)
        {
            var startOffset = line.Start - referenceTime;
            var endOffset = line.End - referenceTime;

            line.Start = referenceTime + TimeSpan.FromTicks((long)(startOffset.Ticks * scaleFactor));
            line.End = referenceTime + TimeSpan.FromTicks((long)(endOffset.Ticks * scaleFactor));
        }
    }

    /// <summary>
    /// Stretches timing to fit between two time points
    /// </summary>
    /// <param name="lines">Lines to stretch</param>
    /// <param name="newStart">New start time for first line</param>
    /// <param name="newEnd">New end time for last line</param>
    public static void StretchTiming(IList<SubtitleLine> lines, TimeSpan newStart, TimeSpan newEnd)
    {
        if (lines.Count == 0) return;

        var oldStart = lines.First().Start;
        var oldEnd = lines.Last().End;
        var oldDuration = oldEnd - oldStart;
        var newDuration = newEnd - newStart;

        if (oldDuration.TotalMilliseconds == 0) return;

        var scaleFactor = newDuration.TotalMilliseconds / oldDuration.TotalMilliseconds;

        foreach (var line in lines)
        {
            var startOffset = line.Start - oldStart;
            var endOffset = line.End - oldStart;

            line.Start = newStart + TimeSpan.FromMilliseconds(startOffset.TotalMilliseconds * scaleFactor);
            line.End = newStart + TimeSpan.FromMilliseconds(endOffset.TotalMilliseconds * scaleFactor);
        }
    }

    /// <summary>
    /// Sets auto-duration based on character count (CPS)
    /// </summary>
    /// <param name="line">Line to adjust</param>
    /// <param name="targetCps">Target characters per second (default: 15)</param>
    /// <param name="minDuration">Minimum duration in seconds (default: 1.0)</param>
    /// <param name="maxDuration">Maximum duration in seconds (default: 7.0)</param>
    public static void SetAutoDuration(SubtitleLine line, double targetCps = 15.0, double minDuration = 1.0, double maxDuration = 7.0)
    {
        var charCount = line.PlainText.Length;
        var calculatedDuration = charCount / targetCps;

        // Clamp duration
        calculatedDuration = Math.Clamp(calculatedDuration, minDuration, maxDuration);

        line.End = line.Start + TimeSpan.FromSeconds(calculatedDuration);
    }

    /// <summary>
    /// Snaps timing to video frames
    /// </summary>
    /// <param name="time">Time to snap</param>
    /// <param name="fps">Frames per second (default: 23.976)</param>
    /// <returns>Snapped time</returns>
    public static TimeSpan SnapToFrame(TimeSpan time, double fps = 23.976)
    {
        var frameDuration = 1.0 / fps;
        var frames = Math.Round(time.TotalSeconds / frameDuration);
        return TimeSpan.FromSeconds(frames * frameDuration);
    }

    /// <summary>
    /// Adjusts timing to make adjacent subtitles continuous (fill gaps)
    /// </summary>
    /// <param name="lines">Lines to adjust (sorted by start time)</param>
    /// <param name="maxGap">Maximum gap to fill (ms)</param>
    public static void FillGaps(IList<SubtitleLine> lines, int maxGap = 200)
    {
        for (int i = 0; i < lines.Count - 1; i++)
        {
            var current = lines[i];
            var next = lines[i + 1];

            var gap = next.Start - current.End;
            if (gap.TotalMilliseconds > 0 && gap.TotalMilliseconds <= maxGap)
            {
                current.End = next.Start;
            }
        }
    }

    /// <summary>
    /// Prevents subtitle overlap by adjusting end times
    /// </summary>
    /// <param name="lines">Lines to fix (sorted by start time)</param>
    /// <param name="minGap">Minimum gap between subtitles (ms)</param>
    public static void FixOverlaps(IList<SubtitleLine> lines, int minGap = 50)
    {
        for (int i = 0; i < lines.Count - 1; i++)
        {
            var current = lines[i];
            var next = lines[i + 1];

            var minEnd = next.Start - TimeSpan.FromMilliseconds(minGap);
            if (current.End > minEnd)
            {
                current.End = minEnd;
            }
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
