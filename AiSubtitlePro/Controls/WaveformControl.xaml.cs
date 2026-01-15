using AiSubtitlePro.Core.Models;
using NAudio.Wave;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using IOPath = System.IO.Path;

namespace AiSubtitlePro.Controls;

/// <summary>
/// Waveform visualization control using NAudio
/// </summary>
public partial class WaveformControl : UserControl
{
    private float[]? _waveformData;
    private readonly List<Rectangle> _subtitleMarkers = new();

    #region Dependency Properties

    public static readonly DependencyProperty AudioPathProperty =
        DependencyProperty.Register(nameof(AudioPath), typeof(string), typeof(WaveformControl),
            new PropertyMetadata(null, OnAudioPathChanged));

    public string? AudioPath
    {
        get => (string?)GetValue(AudioPathProperty);
        set => SetValue(AudioPathProperty, value);
    }

    public static readonly DependencyProperty PositionProperty =
        DependencyProperty.Register(nameof(Position), typeof(TimeSpan), typeof(WaveformControl),
            new PropertyMetadata(TimeSpan.Zero, OnPositionChanged));

    public TimeSpan Position
    {
        get => (TimeSpan)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public static readonly DependencyProperty DurationProperty =
        DependencyProperty.Register(nameof(Duration), typeof(TimeSpan), typeof(WaveformControl),
            new PropertyMetadata(TimeSpan.Zero));

    public TimeSpan Duration
    {
        get => (TimeSpan)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public static readonly DependencyProperty SubtitlesProperty =
        DependencyProperty.Register(nameof(Subtitles), typeof(ObservableCollection<SubtitleLine>), typeof(WaveformControl),
            new PropertyMetadata(null, OnSubtitlesChanged));

    public ObservableCollection<SubtitleLine>? Subtitles
    {
        get => (ObservableCollection<SubtitleLine>?)GetValue(SubtitlesProperty);
        set => SetValue(SubtitlesProperty, value);
    }

    #endregion

    #region Events

    public event EventHandler<TimeSpan>? PositionSeek;

    #endregion

    public WaveformControl()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        WaveformCanvas.MouseLeftButtonDown += OnCanvasClick;
    }

    private static void OnAudioPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WaveformControl control)
        {
            control.LoadWaveform(e.NewValue as string);
        }
    }

    private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WaveformControl control)
        {
            control.UpdatePlayhead();
        }
    }

    private static void OnSubtitlesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WaveformControl control)
        {
            control.DrawSubtitleMarkers();
        }
    }

    private async void LoadWaveform(string? path)
    {
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            NoAudioOverlay.Visibility = Visibility.Visible;
            return;
        }

        NoAudioOverlay.Visibility = Visibility.Collapsed;

        try
        {
            await Task.Run(() =>
            {
                _waveformData = ExtractWaveformData(path);
            });

            DrawWaveform();
            DrawTimeRuler();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Waveform load error: {ex.Message}");
            NoAudioOverlay.Visibility = Visibility.Visible;
        }
    }

    private float[] ExtractWaveformData(string path)
    {
        var data = new List<float>();

        try
        {
            using var reader = new AudioFileReader(path);
            var sampleBuffer = new float[reader.WaveFormat.SampleRate];
            int samplesRead;

            while ((samplesRead = reader.Read(sampleBuffer, 0, sampleBuffer.Length)) > 0)
            {
                // Calculate RMS for each chunk
                var sum = 0f;
                for (int i = 0; i < samplesRead; i++)
                {
                    sum += sampleBuffer[i] * sampleBuffer[i];
                }
                var rms = (float)Math.Sqrt(sum / samplesRead);
                data.Add(rms);
            }

            Duration = reader.TotalTime;
        }
        catch
        {
            // Return empty on error
        }

        return data.ToArray();
    }

    private void DrawWaveform()
    {
        WaveformCanvas.Children.Clear();

        if (_waveformData == null || _waveformData.Length == 0)
            return;

        var width = WaveformCanvas.ActualWidth;
        var height = WaveformCanvas.ActualHeight;
        if (width <= 0 || height <= 0) return;

        var midY = height / 2;
        var samplesPerPixel = Math.Max(1, _waveformData.Length / (int)width);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, midY), false, false);

            for (int x = 0; x < (int)width; x++)
            {
                var startSample = x * samplesPerPixel;
                var endSample = Math.Min(startSample + samplesPerPixel, _waveformData.Length);

                var maxVal = 0f;
                for (int i = startSample; i < endSample; i++)
                {
                    if (_waveformData[i] > maxVal)
                        maxVal = _waveformData[i];
                }

                var y = midY - (maxVal * height * 0.8);
                ctx.LineTo(new Point(x, y), true, false);
            }

            // Draw bottom half (mirror)
            for (int x = (int)width - 1; x >= 0; x--)
            {
                var startSample = x * samplesPerPixel;
                var endSample = Math.Min(startSample + samplesPerPixel, _waveformData.Length);

                var maxVal = 0f;
                for (int i = startSample; i < endSample; i++)
                {
                    if (_waveformData[i] > maxVal)
                        maxVal = _waveformData[i];
                }

                var y = midY + (maxVal * height * 0.8);
                ctx.LineTo(new Point(x, y), true, false);
            }
        }

        geometry.Freeze();

        var path = new System.Windows.Shapes.Path
        {
            Data = geometry,
            Fill = new SolidColorBrush(Color.FromRgb(0x00, 0xBC, 0xD4)),
            Opacity = 0.7
        };

        WaveformCanvas.Children.Add(path);
    }

    private void DrawTimeRuler()
    {
        TimeRulerCanvas.Children.Clear();

        var width = TimeRulerCanvas.ActualWidth;
        if (width <= 0 || Duration.TotalSeconds <= 0) return;

        var pixelsPerSecond = width / Duration.TotalSeconds;
        var secondsPerMark = 1.0;
        
        // Adjust mark interval based on zoom
        if (pixelsPerSecond < 10) secondsPerMark = 10;
        else if (pixelsPerSecond < 50) secondsPerMark = 5;

        for (var t = 0.0; t < Duration.TotalSeconds; t += secondsPerMark)
        {
            var x = t * pixelsPerSecond;
            
            var line = new Line
            {
                X1 = x, X2 = x,
                Y1 = 16, Y2 = 24,
                Stroke = Brushes.Gray,
                StrokeThickness = 1
            };
            TimeRulerCanvas.Children.Add(line);

            var text = new TextBlock
            {
                Text = TimeSpan.FromSeconds(t).ToString(@"mm\:ss"),
                Foreground = Brushes.Gray,
                FontSize = 10
            };
            Canvas.SetLeft(text, x + 2);
            Canvas.SetTop(text, 0);
            TimeRulerCanvas.Children.Add(text);
        }
    }

    private void DrawSubtitleMarkers()
    {
        SubtitleMarkersCanvas.Children.Clear();
        _subtitleMarkers.Clear();

        if (Subtitles == null || Duration.TotalSeconds <= 0)
            return;

        var width = SubtitleMarkersCanvas.ActualWidth;
        var height = SubtitleMarkersCanvas.ActualHeight;
        if (width <= 0) return;

        var pixelsPerSecond = width / Duration.TotalSeconds;

        foreach (var line in Subtitles)
        {
            var startX = line.Start.TotalSeconds * pixelsPerSecond;
            var endX = line.End.TotalSeconds * pixelsPerSecond;
            var markerWidth = Math.Max(2, endX - startX);

            var rect = new Rectangle
            {
                Width = markerWidth,
                Height = height,
                Fill = new SolidColorBrush(Color.FromArgb(60, 255, 193, 7)),
                Stroke = new SolidColorBrush(Color.FromRgb(255, 193, 7)),
                StrokeThickness = 1
            };

            Canvas.SetLeft(rect, startX);
            Canvas.SetTop(rect, 0);
            SubtitleMarkersCanvas.Children.Add(rect);
            _subtitleMarkers.Add(rect);
        }
    }

    private void UpdatePlayhead()
    {
        if (Duration.TotalSeconds <= 0) return;

        var width = PlayheadCanvas.ActualWidth;
        var x = (Position.TotalSeconds / Duration.TotalSeconds) * width;

        Canvas.SetLeft(PlayheadLine, x);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawWaveform();
        DrawTimeRuler();
        DrawSubtitleMarkers();
        UpdatePlayhead();
    }

    private void OnCanvasClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Duration.TotalSeconds <= 0) return;

        var width = WaveformCanvas.ActualWidth;
        var x = e.GetPosition(WaveformCanvas).X;
        var newPosition = TimeSpan.FromSeconds((x / width) * Duration.TotalSeconds);

        PositionSeek?.Invoke(this, newPosition);
    }
}
