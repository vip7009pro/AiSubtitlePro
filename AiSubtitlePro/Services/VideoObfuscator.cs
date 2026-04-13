using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AiSubtitlePro.Services;

/// <summary>
/// Video processing service using FFmpeg to apply various transformations
/// </summary>
public class VideoObfuscator
{
    private readonly string _ffmpegPath;
    private readonly string _tempDirectory;

    public delegate void ProgressChangedHandler(int stepNumber, string stepName, int totalSteps);
    public delegate void LogMessageHandler(string message);

    public event ProgressChangedHandler? ProgressChanged;
    public event LogMessageHandler? LogMessage;

    public VideoObfuscator(string ffmpegPath = "ffmpeg", string? tempDirectory = null)
    {
        _ffmpegPath = ffmpegPath;
        _tempDirectory = tempDirectory ?? Path.Combine(Path.GetTempPath(), "VideoObfuscator");
        
        if (!Directory.Exists(_tempDirectory))
            Directory.CreateDirectory(_tempDirectory);
    }

    public class ProcessingOptions
    {
        public bool RemoveMetadata { get; set; } = true;
        public bool ChangeAspectRatio { get; set; } = true;
        public string AspectRatio { get; set; } = "16:9";  // Options: "1:1", "4:5", "9:16", "16:9"
        public bool HorizontalFlip { get; set; } = true;
        public bool AdjustSpeed { get; set; } = true;
        public double SpeedMultiplier { get; set; } = 0.98;  // 0.5-2.0
        public bool AdjustColors { get; set; } = true;
        public double Brightness { get; set; } = 0.05;
        public double Contrast { get; set; } = 1.05;
        public double Saturation { get; set; } = 1.1;
        public bool ShiftPitch { get; set; } = true;
        public double PitchShift { get; set; } = -0.5;  // semitones
        public bool ReplaceMusic { get; set; } = false;
        public string? MusicfilePath { get; set; }
        public bool ReencodeVideo { get; set; } = true;
        public string Codec { get; set; } = "libx265";  // "libx264" or "libx265"
        public int CrfValue { get; set; } = 22;
        public int GopSize { get; set; } = 250;
        public bool AddWatermark { get; set; } = false;
        public string? WatermarkText { get; set; }
    }

    public async Task<bool> ProcessVideoAsync(
        string inputPath,
        string outputPath,
        ProcessingOptions options,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(inputPath))
                throw new FileNotFoundException($"Input video not found: {inputPath}");

            var processedPath = inputPath;
            var stepCount = GetEnabledStepCount(options);
            var currentStep = 0;

            // Step 1: Remove metadata
            if (options.RemoveMetadata)
            {
                currentStep++;
                ReportProgress(currentStep, "Removing metadata", stepCount);
                LogInfo("Starting Step 1: Removing metadata...");
                
                var output = GetTempFile("step1");
                if (!await RunFFmpegAsync(
                    $"-i \"{inputPath}\" -map_metadata -1 -c copy \"{output}\"",
                    cancellationToken))
                {
                    throw new Exception("Failed to remove metadata");
                }
                
                processedPath = output;
                LogInfo("✓ Metadata removed");
                progress?.Report(currentStep);
            }

            // Step 2: Change aspect ratio
            if (options.ChangeAspectRatio && options.AspectRatio != "16:9")
            {
                currentStep++;
                ReportProgress(currentStep, "Changing aspect ratio", stepCount);
                LogInfo($"Starting Step 2: Changing aspect ratio to {options.AspectRatio}...");
                
                var output = GetTempFile("step2");
                var filter = GetAspectRatioFilter(options.AspectRatio);
                
                if (!await RunFFmpegAsync(
                    $"-i \"{processedPath}\" -vf \"{filter}\" -c:a copy \"{output}\"",
                    cancellationToken))
                {
                    throw new Exception($"Failed to change aspect ratio to {options.AspectRatio}");
                }
                
                processedPath = output;
                LogInfo($"✓ Aspect ratio changed to {options.AspectRatio}");
                progress?.Report(currentStep);
            }

            // Step 3: Horizontal flip
            if (options.HorizontalFlip)
            {
                currentStep++;
                ReportProgress(currentStep, "Flipping video", stepCount);
                LogInfo("Starting Step 3: Flipping video horizontally...");
                
                var output = GetTempFile("step3");
                if (!await RunFFmpegAsync(
                    $"-i \"{processedPath}\" -vf hflip -c:a copy \"{output}\"",
                    cancellationToken))
                {
                    throw new Exception("Failed to flip video");
                }
                
                processedPath = output;
                LogInfo("✓ Video flipped");
                progress?.Report(currentStep);
            }

            // Step 4: Adjust speed
            if (options.AdjustSpeed)
            {
                currentStep++;
                ReportProgress(currentStep, "Adjusting speed", stepCount);
                LogInfo($"Starting Step 4: Adjusting speed to {options.SpeedMultiplier}x...");
                
                var output = GetTempFile("step4");
                var setpts = FormatDouble(1.0 / options.SpeedMultiplier);
                var atempoValue = FormatDouble(options.SpeedMultiplier);
                
                if (!await RunFFmpegAsync(
                    $"-i \"{processedPath}\" -filter_complex \"[0:v]setpts={setpts}*PTS[v];[0:a]atempo={atempoValue}[a]\" -map \"[v]\" -map \"[a]\" \"{output}\"",
                    cancellationToken))
                {
                    throw new Exception("Failed to adjust speed");
                }
                
                processedPath = output;
                LogInfo($"✓ Speed adjusted to {options.SpeedMultiplier}x");
                progress?.Report(currentStep);
            }

            // Step 5: Color correction
            if (options.AdjustColors)
            {
                currentStep++;
                ReportProgress(currentStep, "Adjusting colors", stepCount);
                LogInfo("Starting Step 5: Adjusting brightness, contrast, and saturation...");
                
                var output = GetTempFile("step5");
                var filter = $"eq=brightness={FormatDouble(options.Brightness)}:contrast={FormatDouble(options.Contrast)}:saturation={FormatDouble(options.Saturation)}";
                
                if (!await RunFFmpegAsync(
                    $"-i \"{processedPath}\" -vf \"{filter}\" -c:a copy \"{output}\"",
                    cancellationToken))
                {
                    throw new Exception("Failed to adjust colors");
                }
                
                processedPath = output;
                LogInfo("✓ Colors adjusted");
                progress?.Report(currentStep);
            }

            // Step 6: Audio pitch shift
            if (options.ShiftPitch)
            {
                currentStep++;
                ReportProgress(currentStep, "Shifting pitch", stepCount);
                LogInfo($"Starting Step 6: Shifting pitch by {options.PitchShift} semitones...");
                
                var output = GetTempFile("step6");
                var pitchFactor = Math.Pow(2, options.PitchShift / 12.0);
                var sampleRate = 44100;
                var inversePitchFactor = 1.0 / pitchFactor;
                var audioFilter = $"[0:a]asetrate={sampleRate}*{FormatDouble(pitchFactor)},atempo={FormatDouble(inversePitchFactor)},aresample={sampleRate}[a]";
                
                if (!await RunFFmpegAsync(
                    $"-i \"{processedPath}\" -filter_complex \"{audioFilter}\" -map 0:v -map \"[a]\" -c:v copy \"{output}\"",
                    cancellationToken))
                {
                    throw new Exception("Failed to shift pitch");
                }
                
                processedPath = output;
                LogInfo("✓ Pitch shifted");
                progress?.Report(currentStep);
            }

            // Step 7: Replace music
            if (options.ReplaceMusic && !string.IsNullOrEmpty(options.MusicfilePath) && File.Exists(options.MusicfilePath))
            {
                currentStep++;
                ReportProgress(currentStep, "Replacing music", stepCount);
                LogInfo("Starting Step 7: Replacing background music...");
                
                var output = GetTempFile("step7");
                if (!await RunFFmpegAsync(
                    $"-i \"{processedPath}\" -i \"{options.MusicfilePath}\" -c:v copy -c:a aac -map 0:v:0 -map 1:a:0 -shortest \"{output}\"",
                    cancellationToken))
                {
                    throw new Exception("Failed to replace music");
                }
                
                processedPath = output;
                LogInfo("✓ Music replaced");
                progress?.Report(currentStep);
            }

            // Step 8: Re-encode
            if (options.ReencodeVideo)
            {
                currentStep++;
                ReportProgress(currentStep, "Re-encoding", stepCount);
                LogInfo($"Starting Step 8: Re-encoding with {options.Codec}...");
                
                var output = GetTempFile("step8");
                var codecName = options.Codec switch
                {
                    "libx264" => "h264",
                    "libx265" => "h265",
                    _ => options.Codec
                };
                
                LogInfo($"Using codec: {options.Codec}");
                
                if (!await RunFFmpegAsync(
                    $"-i \"{processedPath}\" -c:v {options.Codec} -crf {options.CrfValue} -g {options.GopSize} -c:a aac -b:a 128k \"{output}\"",
                    cancellationToken))
                {
                    throw new Exception($"Failed to re-encode with {options.Codec}");
                }
                
                processedPath = output;
                LogInfo($"✓ Re-encoded with {options.Codec}");
                progress?.Report(currentStep);
            }

            // Step 9: Add watermark
            if (options.AddWatermark && !string.IsNullOrEmpty(options.WatermarkText))
            {
                currentStep++;
                ReportProgress(currentStep, "Adding watermark", stepCount);
                LogInfo("Starting Step 9: Adding watermark...");
                
                var output = GetTempFile("step9");
                var watermarkFilter = $"drawtext=text='{options.WatermarkText}':fontcolor=white:fontsize=24:x=10:y=10";
                
                if (!await RunFFmpegAsync(
                    $"-i \"{processedPath}\" -vf \"{watermarkFilter}\" -c:a copy \"{output}\"",
                    cancellationToken))
                {
                    throw new Exception("Failed to add watermark");
                }
                
                processedPath = output;
                LogInfo("✓ Watermark added");
                progress?.Report(currentStep);
            }

            // Move final output to desired location
            LogInfo($"Finalizing output: {outputPath}");
            if (File.Exists(outputPath))
                File.Delete(outputPath);
            
            File.Move(processedPath, outputPath, true);
            LogInfo($"✓ Video processing complete! Output: {outputPath}");
            
            return true;
        }
        catch (OperationCanceledException)
        {
            LogError("Video processing cancelled by user");
            return false;
        }
        catch (Exception ex)
        {
            LogError($"Error during video processing: {ex.Message}");
            return false;
        }
        finally
        {
            // Cleanup temp files
            CleanupTempFiles();
        }
    }

    private string GetAspectRatioFilter(string aspectRatio)
    {
        return aspectRatio switch
        {
            "1:1" => "scale=1080:1080:force_original_aspect_ratio=1,pad=1080:1080:(ow-iw)/2:(oh-ih)/2",
            "4:5" => "scale=864:1080:force_original_aspect_ratio=1,pad=864:1080:(ow-iw)/2:(oh-ih)/2",
            "9:16" => "scale=1080:1920:force_original_aspect_ratio=1,pad=1080:1920:(ow-iw)/2:(oh-ih)/2",
            _ => "scale=1920:1080:force_original_aspect_ratio=1,pad=1920:1080:(ow-iw)/2:(oh-ih)/2"
        };
    }

    private async Task<bool> RunFFmpegAsync(string arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = $"-y {arguments}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(psi))
            {
                if (process == null)
                {
                    LogError($"Failed to start FFmpeg process");
                    return false;
                }

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync(cancellationToken);

                var output = await outputTask;
                var error = await errorTask;

                if (process.ExitCode != 0)
                {
                    LogError($"FFmpeg error (exit code {process.ExitCode}): {error}");
                    return false;
                }

                // Log successful output
                if (!string.IsNullOrEmpty(error))
                {
                    // FFmpeg writes info to stderr
                    LogDebug(error);
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            LogError($"Exception running FFmpeg: {ex.Message}");
            return false;
        }
    }

    private int GetEnabledStepCount(ProcessingOptions options)
    {
        int count = 0;
        if (options.RemoveMetadata) count++;
        if (options.ChangeAspectRatio && options.AspectRatio != "16:9") count++;
        if (options.HorizontalFlip) count++;
        if (options.AdjustSpeed) count++;
        if (options.AdjustColors) count++;
        if (options.ShiftPitch) count++;
        if (options.ReplaceMusic && !string.IsNullOrEmpty(options.MusicfilePath) && File.Exists(options.MusicfilePath)) count++;
        if (options.ReencodeVideo) count++;
        if (options.AddWatermark && !string.IsNullOrEmpty(options.WatermarkText)) count++;
        return count;
    }

    private string GetTempFile(string stepName)
    {
        return Path.Combine(_tempDirectory, $"{stepName}_{Guid.NewGuid():N}.mp4");
    }

    private void CleanupTempFiles()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                var files = Directory.GetFiles(_tempDirectory);
                foreach (var file in files)
                {
                    try { File.Delete(file); }
                    catch { /* Ignore cleanup errors */ }
                }
            }
        }
        catch { /* Ignore cleanup errors */ }
    }

    private void ReportProgress(int currentStep, string stepName, int totalSteps)
    {
        ProgressChanged?.Invoke(currentStep, stepName, totalSteps);
    }

    private static string FormatDouble(double value) => value.ToString(CultureInfo.InvariantCulture);

    private void LogInfo(string message) => LogMessage?.Invoke($"[INFO] {message}");
    private void LogError(string message) => LogMessage?.Invoke($"[ERROR] {message}");
    private void LogDebug(string message) => LogMessage?.Invoke($"[DEBUG] {message}");
}
