using AiSubtitlePro.Infrastructure.Media;
using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AiSubtitlePro.Views;

public partial class ExportHardSubOptionsDialog : Window
{
    private readonly string _mediaPath;
    private readonly string _assPath;
    private readonly TimeSpan _previewTime;

    public bool ExportVerticalInitial { get; set; }
    public int BlurSigmaInitial { get; set; } = 20;
    public bool EnableTrailerInitial { get; set; }
    public TimeSpan TrailerStartInitial { get; set; } = TimeSpan.Zero;
    public TimeSpan TrailerDurationInitial { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan TrailerTransitionInitial { get; set; } = TimeSpan.FromSeconds(1);

    public bool ExportVertical { get; private set; }
    public int BlurSigma { get; private set; } = 20;

    public bool EnableTrailer { get; private set; }
    public TimeSpan TrailerStart { get; private set; } = TimeSpan.Zero;
    public TimeSpan TrailerDuration { get; private set; } = TimeSpan.FromSeconds(5);
    public TimeSpan TrailerTransition { get; private set; } = TimeSpan.FromSeconds(1);

    public ExportHardSubOptionsDialog(string mediaPath, string assPath, TimeSpan previewTime)
    {
        InitializeComponent();

        _mediaPath = mediaPath;
        _assPath = assPath;
        _previewTime = previewTime;

        Loaded += (_, __) =>
        {
            VerticalCheck.IsChecked = ExportVerticalInitial;

            BlurSigma = BlurSigmaInitial;
            BlurSlider.Value = BlurSigmaInitial;
            BlurValueText.Text = ((int)Math.Round(BlurSlider.Value)).ToString();

            TrailerCheck.IsChecked = EnableTrailerInitial;
            TrailerStartBox.Text = FormatTs(TrailerStartInitial);
            TrailerDurationBox.Text = FormatTs(TrailerDurationInitial);
            TrailerTransitionBox.Text = FormatTs(TrailerTransitionInitial);

            UpdateEnabled();
        };

        BlurSlider.ValueChanged += (_, __) =>
        {
            BlurSigma = (int)Math.Round(BlurSlider.Value);
            BlurValueText.Text = BlurSigma.ToString();
        };

        VerticalCheck.Checked += (_, __) => UpdateEnabled();
        VerticalCheck.Unchecked += (_, __) => UpdateEnabled();

        TrailerCheck.Checked += (_, __) => UpdateEnabled();
        TrailerCheck.Unchecked += (_, __) => UpdateEnabled();

        UpdateEnabled();
    }

    private void UpdateEnabled()
    {
        var enabled = VerticalCheck.IsChecked == true;
        BlurSlider.IsEnabled = enabled;

        var trailerEnabled = TrailerCheck.IsChecked == true;
        TrailerStartBox.IsEnabled = trailerEnabled;
        TrailerDurationBox.IsEnabled = trailerEnabled;
        TrailerTransitionBox.IsEnabled = trailerEnabled;
        UseCurrentAsStartButton.IsEnabled = trailerEnabled;
    }

    private void UseCurrentAsStart_Click(object sender, RoutedEventArgs e)
    {
        TrailerStartBox.Text = FormatTs(_previewTime);
    }

    private static string FormatTs(TimeSpan t)
    {
        var cs = (int)Math.Round(t.TotalMilliseconds / 10.0);
        var hh = (int)(cs / (60 * 60 * 100));
        var mm = (int)((cs / (60 * 100)) % 60);
        var ss = (int)((cs / 100) % 60);
        var ff = (int)(cs % 100);
        return $"{hh}:{mm:D2}:{ss:D2}.{ff:D2}";
    }

    private static bool TryParseTime(string? text, out TimeSpan ts)
    {
        ts = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var t = text.Trim();
        var parts = t.Split(':');
        int h = 0, m = 0;
        double s = 0;

        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[0], out h)) return false;
            if (!int.TryParse(parts[1], out m)) return false;
            if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out s)) return false;
        }
        else if (parts.Length == 2)
        {
            if (!int.TryParse(parts[0], out m)) return false;
            if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out s)) return false;
        }
        else if (parts.Length == 1)
        {
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out s)) return false;
        }
        else
        {
            return false;
        }

        var wholeSeconds = (int)Math.Floor(s);
        var frac = s - wholeSeconds;
        var ms = (int)Math.Round(frac * 1000);
        if (ms >= 1000)
        {
            ms -= 1000;
            wholeSeconds += 1;
        }

        try
        {
            ts = new TimeSpan(0, h, m, wholeSeconds, ms);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (VerticalCheck.IsChecked != true)
        {
            PreviewStatus.Text = "Enable vertical export to preview.";
            return;
        }

        PreviewStatus.Text = "Generating preview...";
        PreviewImage.Source = null;

        var tempDir = Path.Combine(Path.GetTempPath(), "AiSubtitlePro");
        Directory.CreateDirectory(tempDir);
        var jpgPath = Path.Combine(tempDir, $"vertical_preview_{Guid.NewGuid():N}.jpg");

        try
        {
            using var ffmpeg = new FFmpegService();
            await ffmpeg.GenerateVerticalPreviewJpgAsync(
                videoPath: _mediaPath,
                subtitlePath: _assPath,
                outputJpgPath: jpgPath,
                time: _previewTime,
                blurSigma: BlurSigma,
                width: 540,
                height: 960);

            if (!File.Exists(jpgPath))
                throw new Exception("Preview image not created");

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(jpgPath);
            bmp.EndInit();
            bmp.Freeze();

            PreviewImage.Source = bmp;
            PreviewStatus.Text = "";
        }
        catch (Exception ex)
        {
            PreviewStatus.Text = $"Preview failed: {ex.Message}";
        }
        finally
        {
            try { if (File.Exists(jpgPath)) File.Delete(jpgPath); } catch { }
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ExportVertical = VerticalCheck.IsChecked == true;
        BlurSigma = (int)Math.Round(BlurSlider.Value);

        EnableTrailer = TrailerCheck.IsChecked == true;
        if (EnableTrailer)
        {
            if (!TryParseTime(TrailerStartBox.Text, out var tsStart)
                || !TryParseTime(TrailerDurationBox.Text, out var tsDur)
                || !TryParseTime(TrailerTransitionBox.Text, out var tsTrans))
            {
                MessageBox.Show("Invalid trailer time format. Use h:mm:ss.ff", "Trailer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (tsDur <= TimeSpan.Zero)
            {
                MessageBox.Show("Trailer duration must be > 0", "Trailer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (tsTrans < TimeSpan.Zero)
                tsTrans = TimeSpan.Zero;
            if (tsTrans > tsDur)
                tsTrans = tsDur;

            TrailerStart = tsStart;
            TrailerDuration = tsDur;
            TrailerTransition = tsTrans;
        }

        DialogResult = true;
        Close();
    }
}
