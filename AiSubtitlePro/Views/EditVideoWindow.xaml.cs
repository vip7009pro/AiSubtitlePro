using System.Windows;
using AiSubtitlePro.ViewModels;

namespace AiSubtitlePro.Views;

public partial class EditVideoWindow : Window
{
    public EditVideoWindow()
    {
        InitializeComponent();
        DataContext = new EditVideoViewModel();
    }
}
