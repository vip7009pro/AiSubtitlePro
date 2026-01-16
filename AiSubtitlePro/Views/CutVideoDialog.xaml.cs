using System;
using System.Windows;

namespace AiSubtitlePro.Views;

public partial class CutVideoDialog : Window
{
    private readonly TimeSpan _mediaDuration;
    private readonly TimeSpan _currentAbs;

    public TimeSpan StartAbs { get; private set; }
    public TimeSpan EndAbs { get; private set; }

    public CutVideoDialog(TimeSpan mediaDuration, TimeSpan startAbs, TimeSpan endAbs, TimeSpan currentAbs)
    {
        InitializeComponent();

        _mediaDuration = mediaDuration;
        _currentAbs = currentAbs;

        if (_mediaDuration < TimeSpan.Zero) _mediaDuration = TimeSpan.Zero;

        var durMs = Math.Max(0, _mediaDuration.TotalMilliseconds);
        StartSlider.Minimum = 0;
        StartSlider.Maximum = durMs;
        EndSlider.Minimum = 0;
        EndSlider.Maximum = durMs;

        if (startAbs < TimeSpan.Zero) startAbs = TimeSpan.Zero;
        if (startAbs > _mediaDuration) startAbs = _mediaDuration;

        var effectiveEndAbs = endAbs;
        if (effectiveEndAbs <= TimeSpan.Zero || effectiveEndAbs > _mediaDuration)
            effectiveEndAbs = _mediaDuration;

        StartSlider.ValueChanged += (_, __) => UpdateTexts();
        EndSlider.ValueChanged += (_, __) => UpdateTexts();

        StartSlider.Value = startAbs.TotalMilliseconds;
        EndSlider.Value = effectiveEndAbs.TotalMilliseconds;

        ToEndCheck.IsChecked = endAbs <= TimeSpan.Zero;

        UpdateTexts();
    }

    private void UpdateTexts()
    {
        var start = TimeSpan.FromMilliseconds(StartSlider.Value);
        var end = TimeSpan.FromMilliseconds(EndSlider.Value);

        if (start < TimeSpan.Zero) start = TimeSpan.Zero;
        if (start > _mediaDuration) start = _mediaDuration;

        if (ToEndCheck.IsChecked == true)
        {
            end = _mediaDuration;
        }
        else
        {
            if (end < start) end = start;
            if (end > _mediaDuration) end = _mediaDuration;
        }

        StartText.Text = $"Start: {start:hh\\:mm\\:ss\\.ff}";
        EndText.Text = $"End: {end:hh\\:mm\\:ss\\.ff}";
        SummaryText.Text = $"Segment length: {(end - start):hh\\:mm\\:ss\\.ff}";

        // Keep slider UI consistent when ToEnd is enabled.
        if (ToEndCheck.IsChecked == true)
        {
            if (Math.Abs(EndSlider.Value - end.TotalMilliseconds) > 0.5)
                EndSlider.Value = end.TotalMilliseconds;
            EndSlider.IsEnabled = false;
        }
        else
        {
            EndSlider.IsEnabled = true;
        }
    }

    private void SetStartToCurrent_Click(object sender, RoutedEventArgs e)
    {
        var ms = Math.Clamp(_currentAbs.TotalMilliseconds, 0, _mediaDuration.TotalMilliseconds);
        StartSlider.Value = ms;
    }

    private void SetEndToCurrent_Click(object sender, RoutedEventArgs e)
    {
        ToEndCheck.IsChecked = false;
        var ms = Math.Clamp(_currentAbs.TotalMilliseconds, 0, _mediaDuration.TotalMilliseconds);
        EndSlider.Value = ms;
    }

    private void ToEndCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateTexts();
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var start = TimeSpan.FromMilliseconds(StartSlider.Value);
        var end = (ToEndCheck.IsChecked == true)
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(EndSlider.Value);

        if (start < TimeSpan.Zero) start = TimeSpan.Zero;
        if (start > _mediaDuration) start = _mediaDuration;

        if (end != TimeSpan.Zero)
        {
            if (end < start) end = start;
            if (end > _mediaDuration) end = _mediaDuration;
        }

        StartAbs = start;
        EndAbs = end; // EndAbs==0 means "to end".

        DialogResult = true;
        Close();
    }
}
