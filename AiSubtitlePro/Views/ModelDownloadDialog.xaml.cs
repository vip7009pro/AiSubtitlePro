using AiSubtitlePro.Core.Models;
using AiSubtitlePro.Infrastructure.AI;
using System.Windows;

namespace AiSubtitlePro.Views;

public partial class ModelDownloadDialog : Window
{
    private readonly ModelDownloader _downloader;
    private readonly WhisperModelSize _modelSize;
    private CancellationTokenSource? _cts;

    public ModelDownloadDialog(WhisperModelSize modelSize)
    {
        InitializeComponent();
        _modelSize = modelSize;
        _downloader = new ModelDownloader();
        StatusText.Text = $"Downloading {modelSize} model...";
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        _downloader.ProgressChanged += (s, args) =>
        {
            Dispatcher.Invoke(() =>
            {
                ProgressBar.Value = args.Percent;
                ProgressText.Text = args.Status; 
            });
        };

        try
        {
            await _downloader.DownloadModelAsync(_modelSize, _cts.Token);
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            DialogResult = false;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Download failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
            Close();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }
}
