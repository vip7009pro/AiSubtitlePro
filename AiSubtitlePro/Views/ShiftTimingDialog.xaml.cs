using AiSubtitlePro.Core.Models;
using AiSubtitlePro.Core.Timing;
using System.Windows;

namespace AiSubtitlePro.Views;

/// <summary>
/// Shift Timing Dialog
/// </summary>
public partial class ShiftTimingDialog : Window
{
    public TimeSpan Offset { get; private set; }
    public bool ShiftForward { get; private set; }
    public bool ApplyToAll { get; private set; }
    public bool ApplyToSelected { get; private set; }
    public bool ApplyFromCurrent { get; private set; }
    public bool ShiftStart { get; private set; }
    public bool ShiftEnd { get; private set; }

    public ShiftTimingDialog()
    {
        InitializeComponent();
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Parse time values
            int hours = int.Parse(HoursBox.Text);
            int minutes = int.Parse(MinutesBox.Text);
            int seconds = int.Parse(SecondsBox.Text);
            int milliseconds = int.Parse(MillisecondsBox.Text);

            Offset = new TimeSpan(0, hours, minutes, seconds, milliseconds);
            
            // Direction
            ShiftForward = ForwardRadio.IsChecked == true;
            if (!ShiftForward)
            {
                Offset = -Offset;
            }

            // Apply to
            ApplyToAll = AllLinesRadio.IsChecked == true;
            ApplyToSelected = SelectedLinesRadio.IsChecked == true;
            ApplyFromCurrent = FromCurrentRadio.IsChecked == true;

            // What to shift
            ShiftStart = ShiftStartCheck.IsChecked == true;
            ShiftEnd = ShiftEndCheck.IsChecked == true;

            DialogResult = true;
            Close();
        }
        catch (FormatException)
        {
            MessageBox.Show("Please enter valid time values.", "Invalid Input", 
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static bool TryGetOptions(
        SubtitleLine? selectedLine,
        SubtitleDocument document,
        out List<SubtitleLine> linesToShift,
        out TimeSpan offset,
        out bool shiftStart,
        out bool shiftEnd)
    {
        linesToShift = new List<SubtitleLine>();
        offset = TimeSpan.Zero;
        shiftStart = false;
        shiftEnd = false;

        var dialog = new ShiftTimingDialog();
        dialog.Owner = Application.Current.MainWindow;

        if (dialog.ShowDialog() != true)
            return false;

        IEnumerable<SubtitleLine> lines;
        if (dialog.ApplyToAll)
            lines = document.Lines;
        else if (dialog.ApplyToSelected)
            lines = document.Lines.Where(l => l.IsSelected);
        else if (dialog.ApplyFromCurrent && selectedLine != null)
            lines = document.Lines.Skip(document.Lines.IndexOf(selectedLine));
        else
            lines = document.Lines;

        linesToShift = lines.ToList();
        offset = dialog.Offset;
        shiftStart = dialog.ShiftStart;
        shiftEnd = dialog.ShiftEnd;
        return true;
    }
}
