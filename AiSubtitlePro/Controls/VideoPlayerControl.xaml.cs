using MaterialDesignThemes.Wpf;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace AiSubtitlePro.Controls;

/// <summary>
/// Video player control using LibVLCSharp
/// </summary>
public partial class VideoPlayerControl : UserControl, IDisposable
{
    private AiSubtitlePro.Infrastructure.Rendering.VideoEngine? _videoEngine;
    private DispatcherTimer? _positionTimer;
    private bool _isDragging;
    private bool _isDisposed;

    private static string LogPath => Path.Combine(Path.GetTempPath(), "AiSubtitlePro.VideoPreview.log");

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
        Debug.WriteLine(message);
    }

    #region Dependency Properties
    
    // ... DependencyProperties ...

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(string), typeof(VideoPlayerControl),
            new PropertyMetadata(null, OnSourceChanged));

    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public static readonly DependencyProperty PositionProperty =
        DependencyProperty.Register(nameof(Position), typeof(TimeSpan), typeof(VideoPlayerControl),
            new PropertyMetadata(TimeSpan.Zero));

    public TimeSpan Position
    {
        get => (TimeSpan?)GetValue(PositionProperty) ?? TimeSpan.Zero;
        set => SetValue(PositionProperty, value);
    }

    public static readonly DependencyProperty DurationProperty =
        DependencyProperty.Register(nameof(Duration), typeof(TimeSpan), typeof(VideoPlayerControl),
            new PropertyMetadata(TimeSpan.Zero));

    public TimeSpan Duration
    {
        get => (TimeSpan?)GetValue(DurationProperty) ?? TimeSpan.Zero;
        private set => SetValue(DurationProperty, value);
    }

    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(VideoPlayerControl),
            new PropertyMetadata(false));

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        private set => SetValue(IsPlayingProperty, value);
    }

    public static readonly DependencyProperty CurrentSubtitleProperty =
        DependencyProperty.Register(nameof(CurrentSubtitle), typeof(string), typeof(VideoPlayerControl),
            new PropertyMetadata(null, OnCurrentSubtitleChanged));

    public string? CurrentSubtitle
    {
        get => (string?)GetValue(CurrentSubtitleProperty);
        set => SetValue(CurrentSubtitleProperty, value);
    }

    #endregion

    #region Events

    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler? MediaLoaded;
    public event EventHandler? MediaEnded;
    public event EventHandler<(int X, int Y)>? VideoClicked;

    #endregion

    public VideoPlayerControl()
    {
        InitializeComponent();
        InitializeEngine();

        // Ensure one-tap click-to-seek works even if Slider template handles the mouse event.
        TimelineSlider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(TimelineSlider_PreviewMouseDown),
            true);
    }

    private void InitializeEngine()
    {
        try
        {
            _videoEngine = new AiSubtitlePro.Infrastructure.Rendering.VideoEngine();
            
            // Wire up events
            _videoEngine.PositionChanged += (s, time) => {
                 // Engine updates position
            };
            
            _videoEngine.MediaEnded += (s, e) => {
                 Dispatcher.Invoke(() => MediaEnded?.Invoke(this, EventArgs.Empty));
            };

            // Bind Image
            VideoImage.Source = _videoEngine.VideoSource;
            Log("VideoPlayerControl: engine initialized; VideoImage.Source bound.");

            // Position update timer (UI sync)
            _positionTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _positionTimer.Tick += OnPositionTimerTick;
            _positionTimer.Start();
        }
        catch (Exception ex)
        {
            Log($"VideoPlayerControl: engine init error: {ex}");
            NoMediaOverlay.Visibility = Visibility.Visible;
        }
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VideoPlayerControl control)
        {
            Log($"VideoPlayerControl: Source changed. New='{e.NewValue as string}'");
            control.LoadMedia(e.NewValue as string);
        }
    }

    private static void OnCurrentSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VideoPlayerControl control)
        {
            control.UpdateSubtitleOverlay(e.NewValue as string);
        }
    }

    private void LoadMedia(string? path)
    {
        if (string.IsNullOrEmpty(path) || _videoEngine == null)
        {
            Log($"VideoPlayerControl: LoadMedia skipped. path='{path}', engineNull={_videoEngine == null}");
            return;
        }

        try
        {
            Log($"VideoPlayerControl: LoadMedia start. path='{path}' exists={File.Exists(path)}");
            _videoEngine.LoadMedia(path);

            Log("VideoPlayerControl: LoadMedia success; playback ready (not auto-playing).");

            // Sync slider range to media duration
            TimelineSlider.Minimum = 0;
            TimelineSlider.Maximum = Math.Max(0, _videoEngine.Duration.TotalMilliseconds);

            NoMediaOverlay.Visibility = Visibility.Collapsed;
            MediaLoaded?.Invoke(this, EventArgs.Empty);
            
            // Rebind source in case engine recreated it
             Dispatcher.Invoke(() => {
                VideoImage.Source = _videoEngine.VideoSource;
            });
        }
        catch (Exception ex)
        {
            Log($"VideoPlayerControl: LoadMedia failed: {ex}");
            MessageBox.Show($"Failed to load media:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Playback proxies
    public void Play() => _videoEngine?.Play();
    public void Pause() => _videoEngine?.Pause();
    public void Stop() => _videoEngine?.Stop();
    
    public void SeekTo(TimeSpan position)
    {
         if (_videoEngine == null) return;
         _videoEngine.SeekTo(position);
    }
    
    public void SeekToFrame(bool forward)
    {
        if (_videoEngine == null) return;
        var frameDuration = TimeSpan.FromMilliseconds(1000.0 / 24);
        var newTime = Position + (forward ? frameDuration : -frameDuration);
        if (newTime < TimeSpan.Zero) newTime = TimeSpan.Zero;
        SeekTo(newTime);
    }

    // ... Events ...

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        var engine = _videoEngine;
        if (engine == null || _isDragging) return;

        var posMs = engine.Position.TotalMilliseconds;
        var durMs = engine.Duration.TotalMilliseconds;
        if (durMs > 0 && Math.Abs(TimelineSlider.Maximum - durMs) > 0.5)
            TimelineSlider.Maximum = durMs;
        TimelineSlider.Value = posMs;

        Position = engine.Position;
        Duration = engine.Duration;

        IsPlaying = engine.IsPlaying;
        UpdatePlayPauseIcon();
        UpdateTimeDisplay();
        
        PositionChanged?.Invoke(this, Position);
    }
    
    private void UpdateTimeDisplay()
    {
        TimeDisplay.Text = $"{Position:hh\\:mm\\:ss\\.ff} / {Duration:hh\\:mm\\:ss\\.ff}";
    }

    private void UpdatePlayPauseIcon()
    {
        PlayPauseIcon.Kind = IsPlaying 
            ? MaterialDesignThemes.Wpf.PackIconKind.Pause 
            : MaterialDesignThemes.Wpf.PackIconKind.Play;
    }

    private void UpdateSubtitleOverlay(string? text)
    {
        _videoEngine?.SetSubtitleContent(text ?? "");
    }

    private void VideoImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var engine = _videoEngine;
        if (engine == null) return;
        if (engine.VideoWidth <= 0 || engine.VideoHeight <= 0) return;

        var p = e.GetPosition(VideoImage);
        var viewW = VideoImage.ActualWidth;
        var viewH = VideoImage.ActualHeight;
        if (viewW <= 0 || viewH <= 0) return;

        // Stretch=Uniform => letterboxing. Map click point to source pixel coords.
        var scale = Math.Min(viewW / engine.VideoWidth, viewH / engine.VideoHeight);
        if (scale <= 0) return;

        var contentW = engine.VideoWidth * scale;
        var contentH = engine.VideoHeight * scale;
        var offsetX = (viewW - contentW) / 2;
        var offsetY = (viewH - contentH) / 2;

        var x = (p.X - offsetX) / scale;
        var y = (p.Y - offsetY) / scale;
        if (x < 0 || y < 0 || x > engine.VideoWidth || y > engine.VideoHeight) return;

        var xi = (int)Math.Round(Math.Clamp(x, 0, engine.VideoWidth));
        var yi = (int)Math.Round(Math.Clamp(y, 0, engine.VideoHeight));
        VideoClicked?.Invoke(this, (xi, yi));
    }

    #region UI Event Handlers

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        var engine = _videoEngine;
        if (engine == null) return;

        if (engine.IsPlaying)
            Pause();
        else
            Play();
            
        UpdatePlayPauseIcon();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => Stop();
    private void FrameBack_Click(object sender, RoutedEventArgs e) => SeekToFrame(false);
    private void FrameForward_Click(object sender, RoutedEventArgs e) => SeekToFrame(true);

    private void TimelineSlider_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Slider slider) return;

        // Click-to-seek: jump immediately to clicked position.
        // If the user grabs the thumb, let normal dragging work.
        if (e.OriginalSource is Thumb)
        {
            _isDragging = true;
            return;
        }

        var p = e.GetPosition(slider);
        double value;

        // Prefer template track mapping if available (more accurate than width ratio)
        if (slider.Template?.FindName("PART_Track", slider) is Track track)
        {
            value = track.ValueFromPoint(p);
        }
        else
        {
            var ratio = slider.ActualWidth > 0 ? (p.X / slider.ActualWidth) : 0;
            ratio = Math.Clamp(ratio, 0, 1);
            value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
        }

        value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        slider.Value = value;

        _isDragging = false;
        SeekTo(TimeSpan.FromMilliseconds(value));
        e.Handled = true;
    }

    private void TimelineSlider_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isDragging = false;
        // No-op if this was a simple click-to-seek (already applied in mouse down).
        // If user was dragging the thumb, this will land on the final position.
        if (e.OriginalSource is Thumb)
            SeekTo(TimeSpan.FromMilliseconds(TimelineSlider.Value));
    }

    private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isDragging)
        {
            var newTime = TimeSpan.FromMilliseconds(e.NewValue);
            Position = newTime;
            UpdateTimeDisplay();
            SeekTo(newTime);
        }
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        var engine = _videoEngine;
        if (engine == null) return;
        engine.IsMuted = !engine.IsMuted;
        VolumeIcon.Kind = engine.IsMuted 
            ? MaterialDesignThemes.Wpf.PackIconKind.VolumeOff 
            : MaterialDesignThemes.Wpf.PackIconKind.VolumeHigh;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var engine = _videoEngine;
        if (engine != null)
            engine.Volume = (int)e.NewValue;
    }

    #endregion

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _positionTimer?.Stop();
        _videoEngine?.Dispose();
    }
}
