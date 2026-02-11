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

    public bool ExportVertical { get; private set; }
    public int BlurSigma { get; private set; } = 20;

    public ExportHardSubOptionsDialog(string mediaPath, string assPath, TimeSpan previewTime)
    {
        InitializeComponent();

        _mediaPath = mediaPath;
        _assPath = assPath;
        _previewTime = previewTime;

        BlurSlider.ValueChanged += (_, __) =>
        {
            BlurSigma = (int)Math.Round(BlurSlider.Value);
            BlurValueText.Text = BlurSigma.ToString();
        };

        VerticalCheck.Checked += (_, __) => UpdateEnabled();
        VerticalCheck.Unchecked += (_, __) => UpdateEnabled();

        UpdateEnabled();
    }

    private void UpdateEnabled()
    {
        var enabled = VerticalCheck.IsChecked == true;
        BlurSlider.IsEnabled = enabled;
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

        DialogResult = true;
        Close();
    }
}
