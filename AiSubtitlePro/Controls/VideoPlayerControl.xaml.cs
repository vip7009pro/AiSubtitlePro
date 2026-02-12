using MaterialDesignThemes.Wpf;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AiSubtitlePro.Infrastructure.Media;
using AiSubtitlePro.Infrastructure.Rendering;

namespace AiSubtitlePro.Controls;

/// <summary>
/// Video player control using LibVLCSharp
/// </summary>
public partial class VideoPlayerControl : UserControl, IDisposable
{
    private AiSubtitlePro.Infrastructure.Rendering.VideoEngine? _videoEngine;
    private bool _isRenderingAttached;
    private bool _isDragging;
    private bool _isDisposed;

    private DispatcherTimer? _scrubTimer;
    private TimeSpan _pendingScrubTime;
    private bool _hasPendingScrub;
    private readonly SemaphoreSlim _scrubSeekGate = new(1, 1);
    private int _scrubSeq;

    private readonly Stopwatch _renderStopwatch = Stopwatch.StartNew();
    private long _lastRenderTick;
    private TimeSpan _lastRenderedTime = TimeSpan.MinValue;
    private bool _renderOnceRequested;

    // No timer-based scrubbing in audio-master model.

    private AudioPlaybackClock? _audioClock;

    private bool _isMuted;

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

    public static readonly DependencyProperty TrimStartProperty =
        DependencyProperty.Register(nameof(TrimStart), typeof(TimeSpan), typeof(VideoPlayerControl),
            new PropertyMetadata(TimeSpan.Zero, OnTrimChanged));

    public TimeSpan TrimStart
    {
        get => (TimeSpan?)GetValue(TrimStartProperty) ?? TimeSpan.Zero;
        set => SetValue(TrimStartProperty, value);
    }

    public static readonly DependencyProperty TrimEndProperty =
        DependencyProperty.Register(nameof(TrimEnd), typeof(TimeSpan), typeof(VideoPlayerControl),
            new PropertyMetadata(TimeSpan.Zero, OnTrimChanged));

    public TimeSpan TrimEnd
    {
        get => (TimeSpan?)GetValue(TrimEndProperty) ?? TimeSpan.Zero;
        set => SetValue(TrimEndProperty, value);
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

        _scrubTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _scrubTimer.Tick += (_, __) => ApplyPendingScrubSeek();

        try
        {
            D3D11InteropCheckBox.IsChecked = D3DImageRenderer.EnableD3D11SharedInterop;
        }
        catch
        {
        }

        // Ensure one-tap click-to-seek works even if Slider template handles the mouse event.
        TimelineSlider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(TimelineSlider_PreviewMouseDown),
            true);

        // Preserve thumb dragging even when we attach handledEventsToo handlers.
        TimelineSlider.AddHandler(Thumb.DragStartedEvent,
            new DragStartedEventHandler(TimelineSlider_DragStarted),
            true);
        TimelineSlider.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler(TimelineSlider_DragCompleted),
            true);

        // No timers for A/V sync. Rendering is driven by CompositionTarget.Rendering.
    }

    private static bool IsFromThumb(DependencyObject? source)
    {
        var current = source;
        while (current != null)
        {
            if (current is Thumb) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void InitializeEngine()
    {
        try
        {
            _videoEngine = new AiSubtitlePro.Infrastructure.Rendering.VideoEngine();
            _audioClock = new AudioPlaybackClock();

            _videoEngine.MediaEnded += (s, e) =>
            {
                Dispatcher.BeginInvoke(() => MediaEnded?.Invoke(this, EventArgs.Empty));
            };

            // Bind Image
            VideoImage.Source = _videoEngine.VideoSource;
            Log("VideoPlayerControl: engine initialized; VideoImage.Source bound.");

            // Apply current subtitle content immediately (DP may have been set before engine init).
            UpdateSubtitleOverlay(CurrentSubtitle);

            // Do not render continuously while idle; render will be attached on Play or one-shot render requests.
        }
        catch (Exception ex)
        {
            Log($"VideoPlayerControl: engine init error: {ex}");
            NoMediaOverlay.Visibility = Visibility.Visible;
        }
    }

    private void UnloadMedia()
    {
        try
        {
            Pause();
        }
        catch
        {
        }

        try { _videoEngine?.StopPlaybackDecodeLoop(); } catch { }

        try
        {
            DetachRenderLoop();
        }
        catch
        {
        }

        try
        {
            CloseAudioClock();
        }
        catch
        {
        }

        try
        {
            _videoEngine?.Dispose();
        }
        catch
        {
        }
        _videoEngine = null;

        try
        {
            VideoImage.Source = null;
        }
        catch
        {
        }

        Duration = TimeSpan.Zero;
        Position = TimeSpan.Zero;
        IsPlaying = false;
        _lastRenderedTime = TimeSpan.MinValue;
        _renderOnceRequested = false;

        NoMediaOverlay.Visibility = Visibility.Visible;
        UpdatePlayPauseIcon();
        UpdateTimeDisplay();
    }

    private void AttachRenderLoop()
    {
        if (_isRenderingAttached) return;
        CompositionTarget.Rendering += CompositionTarget_Rendering;
        _isRenderingAttached = true;
    }

    private void DetachRenderLoop()
    {
        if (!_isRenderingAttached) return;
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        _isRenderingAttached = false;
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        var engine = _videoEngine;
        var audio = _audioClock;
        if (engine == null || audio == null) return;
        if (_isDragging) return;

        // Throttle to ~60fps to avoid pegging CPU.
        var nowTick = _renderStopwatch.ElapsedTicks;
        var minTicks = Stopwatch.Frequency / 60;
        if (nowTick - _lastRenderTick < minTicks)
            return;

        // Audio device playback position is the master clock while playing.
        // While paused, use the current Position to avoid jumping back to the last pause point.
        var t = audio.IsPlaying ? audio.GetAudioTime() : Position;

        // Clamp to trim range. If we hit the end, pause.
        var (trimStart, trimEnd) = GetEffectiveTrim();
        if (t < trimStart) t = trimStart;
        if (trimEnd > TimeSpan.Zero && t > trimEnd)
        {
            t = trimEnd;
            Pause();
        }

        // If paused and no explicit render requested, do nothing and detach to avoid per-frame callbacks.
        if (!audio.IsPlaying && !_renderOnceRequested)
        {
            DetachRenderLoop();
            return;
        }

        // If time hasn't advanced and there's no explicit render request, skip.
        if (!_renderOnceRequested && t == _lastRenderedTime)
            return;

        _renderOnceRequested = false;
        _lastRenderTick = nowTick;
        _lastRenderedTime = t;

        engine.RenderAt(t);

        Position = t;
        Duration = engine.Duration;
        IsPlaying = audio.IsPlaying;

        // UI updates
        var posMs = t.TotalMilliseconds;
        var durMs = Duration.TotalMilliseconds;
        var (_, effectiveEnd) = GetEffectiveTrim();
        var effectiveEndMs = effectiveEnd.TotalMilliseconds;
        if (durMs > 0 && Math.Abs(TimelineSlider.Maximum - effectiveEndMs) > 0.5)
            TimelineSlider.Maximum = Math.Max(TimelineSlider.Minimum, effectiveEndMs);
        if (Math.Abs(TimelineSlider.Value - posMs) > 0.5)
            TimelineSlider.Value = posMs;

        UpdatePlayPauseIcon();
        UpdateTimeDisplay();
        PositionChanged?.Invoke(this, t);

        if (!audio.IsPlaying)
        {
            // One-shot render finished.
            DetachRenderLoop();
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

    private static void OnTrimChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VideoPlayerControl control)
        {
            control.ApplyTrimToUiAndPosition();
        }
    }

    private (TimeSpan start, TimeSpan end) GetEffectiveTrim()
    {
        var start = TrimStart;
        var end = TrimEnd;
        if (start < TimeSpan.Zero) start = TimeSpan.Zero;

        var dur = _videoEngine?.Duration ?? Duration;
        if (dur > TimeSpan.Zero && start > dur) start = dur;

        if (end <= TimeSpan.Zero || (dur > TimeSpan.Zero && end > dur)) end = dur;
        if (end < start) end = start;
        return (start, end);
    }

    private void ApplyTrimToUiAndPosition()
    {
        var (start, end) = GetEffectiveTrim();

        var minMs = start.TotalMilliseconds;
        var maxMs = Math.Max(minMs, end.TotalMilliseconds);

        TimelineSlider.Minimum = minMs;
        TimelineSlider.Maximum = maxMs;

        // If current position is outside the range, snap to start and render once.
        if (Position < start || Position > end)
        {
            SeekTo(start);
        }

        _renderOnceRequested = true;
        AttachRenderLoop();
    }

    private void LoadMedia(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            Log($"VideoPlayerControl: LoadMedia unload (null/empty source). path='{path}'");
            UnloadMedia();
            return;
        }

        if (_videoEngine == null)
        {
            InitializeEngine();
        }

        if (_videoEngine == null)
        {
            Log($"VideoPlayerControl: LoadMedia aborted - engine null after init. path='{path}'");
            return;
        }

        try
        {
            Log($"VideoPlayerControl: LoadMedia start. path='{path}' exists={File.Exists(path)}");
            _videoEngine.LoadMedia(path);

            _audioClock?.Load(path);
            ApplyAudioVolumeFromUi();

            // Publish duration immediately (some files report 0 format duration; audio/video may differ).
            var vDur = _videoEngine.Duration;
            var aDur = _audioClock?.Duration ?? TimeSpan.Zero;
            Duration = vDur > aDur ? vDur : aDur;

            Log("VideoPlayerControl: LoadMedia success; playback ready (not auto-playing).");

            // Ensure subtitles are applied before first-frame render.
            UpdateSubtitleOverlay(CurrentSubtitle);

            // Sync slider range to media duration
            ApplyTrimToUiAndPosition();

            NoMediaOverlay.Visibility = Visibility.Collapsed;
            MediaLoaded?.Invoke(this, EventArgs.Empty);
            
            // Rebind source in case engine recreated it
            Dispatcher.Invoke(() =>
            {
                VideoImage.Source = _videoEngine.VideoSource;
            });

            // Force a first-frame render AFTER the Image has a bound source and the layout pass has completed.
            // This avoids a common WPF issue where WriteableBitmap updates don't show until the visual is realized.
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    _videoEngine.RenderAtSync(TimeSpan.Zero);
                    VideoImage.InvalidateVisual();
                }
                catch (Exception ex)
                {
                    Log($"VideoPlayerControl: first-frame RenderAtSync failed: {ex}");
                }

                // Render a single frame at start so UI shows the first frame without starting a continuous loop.
                _renderOnceRequested = true;
                AttachRenderLoop();
            }, DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            Log($"VideoPlayerControl: LoadMedia failed: {ex}");
            MessageBox.Show($"Failed to load media:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Playback proxies
    public void Play()
    {
        var (start, _) = GetEffectiveTrim();
        if (Position < start)
            SeekTo(start);

        _audioClock?.Play();
        IsPlaying = _audioClock?.IsPlaying == true;

        // Continuous decode loop for smoother playback.
        var engine = _videoEngine;
        var audio = _audioClock;
        if (engine != null && audio != null)
            engine.StartPlaybackDecodeLoop(() => audio.GetAudioTime());

        AttachRenderLoop();
        _renderOnceRequested = true;
    }

    public void Pause()
    {
        _audioClock?.Pause();

        // Keep DP in sync even if we detach render loop immediately.
        IsPlaying = false;

        // Re-render at current Position while paused to avoid using stale audio clock time.
        var t = _audioClock?.IsPlaying == true ? (_audioClock?.GetAudioTime() ?? Position) : Position;
        _videoEngine?.RenderAt(t);

        try { _videoEngine?.StopPlaybackDecodeLoop(); } catch { }

        _renderOnceRequested = false;
        DetachRenderLoop();
    }

    public void Stop()
    {
        _audioClock?.Stop();
        try { _videoEngine?.StopPlaybackDecodeLoop(); } catch { }
        var (start, _) = GetEffectiveTrim();
        _videoEngine?.SeekTo(start);
        _audioClock?.Seek(start);
        _renderOnceRequested = true;
        AttachRenderLoop();
    }
    
    public void SeekTo(TimeSpan position)
    {
         if (_videoEngine == null) return;
         var (start, end) = GetEffectiveTrim();
         if (position < start) position = start;
         if (end > TimeSpan.Zero && position > end) position = end;

         _audioClock?.Seek(position);
         _videoEngine.SeekTo(position);
         _videoEngine.RenderAt(position);

         // Keep DP + external listeners in sync even while paused.
         Position = position;
         Duration = _videoEngine.Duration;
         IsPlaying = _audioClock?.IsPlaying == true;
         UpdateTimeDisplay();
         PositionChanged?.Invoke(this, position);

         _lastRenderedTime = position;
         _renderOnceRequested = false;

         // Seek already rendered synchronously; ensure we don't keep a render loop running while paused.
         if (_audioClock?.IsPlaying != true)
             DetachRenderLoop();
    }
    
    public void SeekToFrame(bool forward)
    {
        if (_videoEngine == null) return;
        var fps = _videoEngine.FrameRate;
        if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps)) fps = 24;
        var frameDuration = TimeSpan.FromMilliseconds(1000.0 / fps);
        var newTime = Position + (forward ? frameDuration : -frameDuration);
        if (newTime < TimeSpan.Zero) newTime = TimeSpan.Zero;
        SeekTo(newTime);
    }

    // ... Events ...

    // No timer-based clock in audio-master model.
    
    private void UpdateTimeDisplay()
    {
        var (trimStart, trimEnd) = GetEffectiveTrim();

        var pos = Position;
        if (pos < trimStart) pos = trimStart;
        if (trimEnd > TimeSpan.Zero && pos > trimEnd) pos = trimEnd;

        var relPos = pos - trimStart;
        if (relPos < TimeSpan.Zero) relPos = TimeSpan.Zero;

        var relDur = trimEnd - trimStart;
        if (relDur < TimeSpan.Zero) relDur = TimeSpan.Zero;

        TimeDisplay.Text = $"{relPos:hh\\:mm\\:ss\\.ff} / {relDur:hh\\:mm\\:ss\\.ff}";
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

        // Subtitle changes should force a one-shot render even when paused.
        _renderOnceRequested = true;
        AttachRenderLoop();
    }

    public void ForceRenderNow()
    {
        var engine = _videoEngine;
        if (engine == null) return;

        var t = _audioClock?.IsPlaying == true ? (_audioClock?.GetAudioTime() ?? Position) : Position;
        try
        {
            engine.RenderAt(t);
        }
        catch
        {
        }

        _renderOnceRequested = true;
        AttachRenderLoop();
    }

    public void ForceRenderAt(TimeSpan position)
    {
        var engine = _videoEngine;
        if (engine == null) return;

        try
        {
            engine.RenderAt(position);
        }
        catch
        {
        }

        _renderOnceRequested = true;
        AttachRenderLoop();
    }

    private void VideoImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Require double-click to set subtitle position to avoid accidental changes.
        if (e.ClickCount < 2) return;

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
        e.Handled = true;
    }

    private void ApplyAudioVolumeFromUi()
    {
        var audio = _audioClock;
        if (audio == null) return;

        var vol = _isMuted ? 0f : (float)Math.Clamp(VolumeSlider.Value / 100.0, 0, 1);
        audio.SetVolume(vol);
    }
    private void CloseAudioClock()
    {
        try { _audioClock?.Dispose(); } catch { }
        _audioClock = null;
    }

    #region UI Event Handlers

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        var audio = _audioClock;
        if (audio == null) return;

        if (audio.IsPlaying) Pause();
        else Play();
            
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
        if (IsFromThumb(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var pSlider = e.GetPosition(slider);
        double value;

        // Prefer template track mapping if available (more accurate than width ratio)
        if (slider.Template?.FindName("PART_Track", slider) is Track track)
        {
            var pTrack = e.GetPosition(track);
            // Clamp to track bounds to avoid unstable values when clicking near/after edges.
            pTrack = new Point(Math.Clamp(pTrack.X, 0, track.ActualWidth), Math.Clamp(pTrack.Y, 0, track.ActualHeight));

            // Manual linear mapping (more stable than Track.ValueFromPoint with templated sliders).
            var range = slider.Maximum - slider.Minimum;
            if (range <= 0 || track.ActualWidth <= 0)
            {
                value = slider.Minimum;
            }
            else
            {
                var thumbWidth = track.Thumb?.ActualWidth ?? 0;
                var usableWidth = Math.Max(1.0, track.ActualWidth - thumbWidth);
                var ratio = (pTrack.X - (thumbWidth / 2.0)) / usableWidth;
                ratio = Math.Clamp(ratio, 0, 1);
                value = slider.Minimum + (ratio * range);
            }
        }
        else
        {
            var ratio = slider.ActualWidth > 0 ? (pSlider.X / slider.ActualWidth) : 0;
            ratio = Math.Clamp(ratio, 0, 1);
            value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
        }

        value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        slider.Value = value;

        _isDragging = false;
        var t = TimeSpan.FromMilliseconds(value);
        SeekTo(t);
        e.Handled = true;
    }

    private void TimelineSlider_DragStarted(object sender, DragStartedEventArgs e)
    {
        _isDragging = true;
        _hasPendingScrub = false;
        _scrubTimer?.Start();
    }

    private void TimelineSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isDragging = false;
        _scrubTimer?.Stop();
        _hasPendingScrub = false;
        var t = TimeSpan.FromMilliseconds(TimelineSlider.Value);
        SeekTo(t);
        Position = t;
        UpdateTimeDisplay();
        PositionChanged?.Invoke(this, t);
    }

    private void TimelineSlider_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isDragging = false;
        _scrubTimer?.Stop();
        _hasPendingScrub = false;
        // No-op if this was a simple click-to-seek (already applied in mouse down).
        // If user was dragging the thumb, DragCompleted will seek once.
    }

    private void TimelineSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isDragging)
        {
            var newTime = TimeSpan.FromMilliseconds(e.NewValue);
            Position = newTime;
            UpdateTimeDisplay();

            // Debounce seeks while dragging to avoid decoder backlog.
            _pendingScrubTime = newTime;
            _hasPendingScrub = true;
        }
    }

    private void ApplyPendingScrubSeek()
    {
        if (_isDisposed) return;
        if (!_isDragging) return;
        if (!_hasPendingScrub) return;

        _hasPendingScrub = false;

        // Coalesce: only the latest scrub request should be processed.
        var seq = Interlocked.Increment(ref _scrubSeq);
        _ = Task.Run(() => ApplyScrubSeek(seq, _pendingScrubTime));
    }

    private void ApplyScrubSeek(int seq, TimeSpan position)
    {
        if (_isDisposed) return;
        var engine = _videoEngine;
        if (engine == null) return;

        // Do not overlap decoder seeks; if one is in progress, skip this tick.
        if (!_scrubSeekGate.Wait(0))
            return;

        try
        {
            if (_isDisposed) return;
            if (!_isDragging) return;
            if (seq != Volatile.Read(ref _scrubSeq)) return;

            // Video-only seek while scrubbing (do not seek audio clock on every tick).
            var (start, end) = GetEffectiveTrim();
            if (position < start) position = start;
            if (end > TimeSpan.Zero && position > end) position = end;

            engine.SeekTo(position);
            engine.RenderAt(position);

            // Notify listeners (MainWindow updates VM.CurrentPosition from this event).
            if (_isDisposed) return;
            if (!_isDragging) return;
            if (seq != Volatile.Read(ref _scrubSeq)) return;

            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (_isDisposed) return;
                    if (!_isDragging) return;
                    if (seq != Volatile.Read(ref _scrubSeq)) return;

                    Position = position;
                    Duration = engine.Duration;
                    IsPlaying = _audioClock?.IsPlaying == true;
                    UpdateTimeDisplay();
                    PositionChanged?.Invoke(this, position);

                    _lastRenderedTime = position;
                    _renderOnceRequested = false;
                }
                catch
                {
                }
            });
        }
        catch
        {
        }
        finally
        {
            try { _scrubSeekGate.Release(); } catch { }
        }
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        ApplyAudioVolumeFromUi();
        VolumeIcon.Kind = _isMuted 
            ? MaterialDesignThemes.Wpf.PackIconKind.VolumeOff 
            : MaterialDesignThemes.Wpf.PackIconKind.VolumeHigh;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var engine = _videoEngine;
        if (engine != null)
            engine.Volume = (int)e.NewValue;

        ApplyAudioVolumeFromUi();
    }

    private void D3D11InteropCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        ApplyD3D11InteropSetting(enabled: true);
    }

    private void D3D11InteropCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        ApplyD3D11InteropSetting(enabled: false);
    }

    private void ApplyD3D11InteropSetting(bool enabled)
    {
        try
        {
            D3DImageRenderer.EnableD3D11SharedInterop = enabled;
            Log($"VideoPlayerControl: D3D11 interop toggled -> {enabled}");
        }
        catch
        {
            return;
        }

        var path = Source;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        var engine = _videoEngine;
        var audio = _audioClock;
        if (engine == null || audio == null)
            return;

        var pos = Position;
        var wasPlaying = IsPlaying;

        try
        {
            Pause();
        }
        catch
        {
        }

        try
        {
            engine.LoadMedia(path);
            audio.Load(path);
            ApplyAudioVolumeFromUi();

            audio.Seek(pos);
            engine.SeekTo(pos);
            engine.RenderAt(pos);

            _renderOnceRequested = true;
            AttachRenderLoop();
        }
        catch (Exception ex)
        {
            Log($"VideoPlayerControl: failed to reinitialize after D3D11 toggle. {ex}");
        }

        if (wasPlaying)
        {
            try { Play(); } catch { }
        }
    }

    #endregion

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            if (_scrubTimer != null)
            {
                _scrubTimer.Stop();
                _scrubTimer = null;
            }
        }
        catch
        {
        }

        DetachRenderLoop();
        CloseAudioClock();
        _videoEngine?.Dispose();
    }
}
