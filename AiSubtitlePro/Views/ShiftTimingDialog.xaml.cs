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

    /// <summary>
    /// Shows the dialog and applies the shift to the document
    /// </summary>
    public static void ShowAndApply(SubtitleDocument document, SubtitleLine? selectedLine)
    {
        var dialog = new ShiftTimingDialog();
        dialog.Owner = Application.Current.MainWindow;

        if (dialog.ShowDialog() == true)
        {
            IEnumerable<SubtitleLine> linesToShift;

            if (dialog.ApplyToAll)
            {
                linesToShift = document.Lines;
            }
            else if (dialog.ApplyToSelected)
            {
                linesToShift = document.Lines.Where(l => l.IsSelected);
            }
            else if (dialog.ApplyFromCurrent && selectedLine != null)
            {
                var currentIndex = document.Lines.IndexOf(selectedLine);
                linesToShift = document.Lines.Skip(currentIndex);
            }
            else
            {
                linesToShift = document.Lines;
            }

            TimingOperations.ShiftTiming(linesToShift, dialog.Offset, dialog.ShiftStart, dialog.ShiftEnd);
            document.IsDirty = true;
        }
    }
}
