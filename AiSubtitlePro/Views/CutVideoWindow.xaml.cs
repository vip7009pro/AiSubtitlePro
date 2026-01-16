using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AiSubtitlePro.Views;

public partial class CutVideoWindow : Window
{
    private enum DragMode
    {
        None,
        Start,
        End,
        Range
    }

    private DragMode _dragMode = DragMode.None;
    private Point _dragStartPoint;
    private double _dragStartStartMs;
    private double _dragStartEndMs;

    public static readonly DependencyProperty MediaDurationAbsProperty =
        DependencyProperty.Register(nameof(MediaDurationAbs), typeof(TimeSpan), typeof(CutVideoWindow),
            new PropertyMetadata(TimeSpan.Zero, (_, __) => { }));

    public static readonly DependencyProperty CurrentAbsProperty =
        DependencyProperty.Register(nameof(CurrentAbs), typeof(TimeSpan), typeof(CutVideoWindow),
            new PropertyMetadata(TimeSpan.Zero));

    public TimeSpan MediaDurationAbs
    {
        get => (TimeSpan)GetValue(MediaDurationAbsProperty);
        set => SetValue(MediaDurationAbsProperty, value);
    }

    private void RangeOverlay_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (MediaDurationAbs.TotalSeconds <= 0) return;

        var p = e.GetPosition(RangeOverlay);
        _dragStartPoint = p;
        _dragStartStartMs = StartSlider.Value;
        _dragStartEndMs = EndSlider.Value;

        var start = TimeSpan.FromMilliseconds(StartSlider.Value);
        var end = TimeSpan.FromMilliseconds(EndSlider.Value);
        if (ToEndCheck.IsChecked == true)
            end = _mediaDuration;

        var width = RangeOverlay.ActualWidth;
        if (width <= 0) return;

        var startX = (start.TotalSeconds / MediaDurationAbs.TotalSeconds) * width;
        var endX = (end.TotalSeconds / MediaDurationAbs.TotalSeconds) * width;
        if (endX < startX) endX = startX;

        const double handleHitPx = 8.0;
        if (Math.Abs(p.X - startX) <= handleHitPx)
            _dragMode = DragMode.Start;
        else if (Math.Abs(p.X - endX) <= handleHitPx)
            _dragMode = DragMode.End;
        else if (p.X >= startX && p.X <= endX)
            _dragMode = DragMode.Range;
        else
            _dragMode = DragMode.None;

        if (_dragMode != DragMode.None)
        {
            RangeOverlay.CaptureMouse();
            e.Handled = true;
        }
    }

    private void RangeOverlay_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragMode == DragMode.None) return;
        if (MediaDurationAbs.TotalMilliseconds <= 0) return;

        var p = e.GetPosition(RangeOverlay);
        var dx = p.X - _dragStartPoint.X;
        var width = RangeOverlay.ActualWidth;
        if (width <= 0) return;

        var deltaMs = (dx / width) * MediaDurationAbs.TotalMilliseconds;

        var newStart = _dragStartStartMs;
        var newEnd = _dragStartEndMs;

        if (_dragMode == DragMode.Start)
        {
            newStart = _dragStartStartMs + deltaMs;
            newStart = Math.Clamp(newStart, StartSlider.Minimum, StartSlider.Maximum);
            if (ToEndCheck.IsChecked != true)
                newStart = Math.Min(newStart, EndSlider.Value);
        }
        else if (_dragMode == DragMode.End)
        {
            if (ToEndCheck.IsChecked == true) return;
            newEnd = _dragStartEndMs + deltaMs;
            newEnd = Math.Clamp(newEnd, EndSlider.Minimum, EndSlider.Maximum);
            newEnd = Math.Max(newEnd, StartSlider.Value);
        }
        else if (_dragMode == DragMode.Range)
        {
            if (ToEndCheck.IsChecked == true) return;

            var len = _dragStartEndMs - _dragStartStartMs;
            if (len < 0) len = 0;

            newStart = _dragStartStartMs + deltaMs;
            newStart = Math.Clamp(newStart, StartSlider.Minimum, StartSlider.Maximum);
            newEnd = newStart + len;

            if (newEnd > EndSlider.Maximum)
            {
                newEnd = EndSlider.Maximum;
                newStart = newEnd - len;
            }

            if (newStart < StartSlider.Minimum)
            {
                newStart = StartSlider.Minimum;
                newEnd = newStart + len;
            }
        }

        // Apply without fighting user drag.
        if (_dragMode == DragMode.Start || _dragMode == DragMode.Range)
            StartSlider.Value = newStart;
        if (_dragMode == DragMode.End || _dragMode == DragMode.Range)
            EndSlider.Value = newEnd;

        e.Handled = true;
    }

    private void RangeOverlay_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_dragMode == DragMode.None) return;
        _dragMode = DragMode.None;
        RangeOverlay.ReleaseMouseCapture();
        e.Handled = true;
    }

    public TimeSpan CurrentAbs
    {
        get => (TimeSpan)GetValue(CurrentAbsProperty);
        set => SetValue(CurrentAbsProperty, value);
    }

    private readonly TimeSpan _mediaDuration;

    public TimeSpan StartAbs { get; private set; }
    public TimeSpan EndAbs { get; private set; }

    public CutVideoWindow(string mediaPath, TimeSpan mediaDuration, TimeSpan startAbs, TimeSpan endAbs, TimeSpan currentAbs)
    {
        InitializeComponent();

        _mediaDuration = mediaDuration;
        MediaDurationAbs = mediaDuration;

        if (_mediaDuration < TimeSpan.Zero)
            _mediaDuration = TimeSpan.Zero;

        StartSlider.Minimum = 0;
        StartSlider.Maximum = Math.Max(0, _mediaDuration.TotalMilliseconds);
        EndSlider.Minimum = 0;
        EndSlider.Maximum = Math.Max(0, _mediaDuration.TotalMilliseconds);

        if (startAbs < TimeSpan.Zero) startAbs = TimeSpan.Zero;
        if (startAbs > _mediaDuration) startAbs = _mediaDuration;

        var effectiveEndAbs = endAbs;
        if (effectiveEndAbs <= TimeSpan.Zero || effectiveEndAbs > _mediaDuration)
            effectiveEndAbs = _mediaDuration;

        StartSlider.Value = startAbs.TotalMilliseconds;
        EndSlider.Value = effectiveEndAbs.TotalMilliseconds;

        ToEndCheck.IsChecked = endAbs <= TimeSpan.Zero;

        PreviewPlayer.Source = mediaPath;

        PreviewPlayer.PositionChanged += (_, t) =>
        {
            CurrentAbs = t;
            CurrentText.Text = t.ToString(@"hh\:mm\:ss\.ff");
        };

        Waveform.PositionSeek += (_, t) => PreviewPlayer.SeekTo(t);

        StartSlider.ValueChanged += OnSliderChanged;
        EndSlider.ValueChanged += OnSliderChanged;
        ToEndCheck.Checked += (_, __) => UpdateUi();
        ToEndCheck.Unchecked += (_, __) => UpdateUi();

        Loaded += (_, __) =>
        {
            CurrentAbs = currentAbs;
            PreviewPlayer.SeekTo(currentAbs);

            // Ensure overlay is placed after layout.
            Dispatcher.InvokeAsync(UpdateUi, DispatcherPriority.Loaded);
            Waveform.SizeChanged += (_, __) => UpdateUi();
        };

        UpdateUi();
    }

    private void OnSliderChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateUi();

        // Live preview seek while adjusting.
        if (sender == StartSlider)
            PreviewPlayer.SeekTo(TimeSpan.FromMilliseconds(StartSlider.Value));
        else if (sender == EndSlider && ToEndCheck.IsChecked != true)
            PreviewPlayer.SeekTo(TimeSpan.FromMilliseconds(EndSlider.Value));
    }

    private void UpdateUi()
    {
        var start = TimeSpan.FromMilliseconds(StartSlider.Value);
        var end = TimeSpan.FromMilliseconds(EndSlider.Value);

        if (start < TimeSpan.Zero) start = TimeSpan.Zero;
        if (start > _mediaDuration) start = _mediaDuration;

        if (ToEndCheck.IsChecked == true)
        {
            end = _mediaDuration;
            EndSlider.IsEnabled = false;
        }
        else
        {
            EndSlider.IsEnabled = true;
            if (end < start) end = start;
            if (end > _mediaDuration) end = _mediaDuration;
        }

        StartAbs = start;
        EndAbs = ToEndCheck.IsChecked == true ? TimeSpan.Zero : end;

        StartText.Text = start.ToString(@"hh\:mm\:ss\.ff");
        EndText.Text = end.ToString(@"hh\:mm\:ss\.ff");

        var segmentLen = end - start;
        if (segmentLen < TimeSpan.Zero) segmentLen = TimeSpan.Zero;
        SummaryText.Text = $"Segment: {start:hh\\:mm\\:ss\\.ff} → {end:hh\\:mm\\:ss\\.ff}   (Length: {segmentLen:hh\\:mm\\:ss\\.ff})";

        UpdateOverlay(start, end);
    }

    private void UpdateOverlay(TimeSpan start, TimeSpan end)
    {
        if (MediaDurationAbs.TotalSeconds <= 0)
            return;

        var width = RangeOverlay.ActualWidth;
        var height = RangeOverlay.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var startX = (start.TotalSeconds / MediaDurationAbs.TotalSeconds) * width;
        var endX = (end.TotalSeconds / MediaDurationAbs.TotalSeconds) * width;
        if (endX < startX) endX = startX;

        Canvas.SetLeft(StartHandle, startX);
        Canvas.SetTop(StartHandle, 0);
        StartHandle.Height = height;

        Canvas.SetLeft(EndHandle, endX);
        Canvas.SetTop(EndHandle, 0);
        EndHandle.Height = height;

        RangeRect.Width = Math.Max(0, endX - startX);
        RangeRect.Height = height;
        Canvas.SetLeft(RangeRect, startX);
        Canvas.SetTop(RangeRect, 0);
    }

    private void SetStartToCurrent_Click(object sender, RoutedEventArgs e)
    {
        var t = PreviewPlayer.Position;
        StartSlider.Value = t.TotalMilliseconds;
    }

    private void SetEndToCurrent_Click(object sender, RoutedEventArgs e)
    {
        ToEndCheck.IsChecked = false;
        var t = PreviewPlayer.Position;
        EndSlider.Value = t.TotalMilliseconds;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        PreviewPlayer.Dispose();
        base.OnClosed(e);
    }
}
