using AiSubtitlePro.Core.Models;
using AiSubtitlePro.Infrastructure.AI;
using System.Windows;
using System.Windows.Controls;

namespace AiSubtitlePro.Views;

/// <summary>
/// Translation Dialog for LibreTranslate integration
/// </summary>
public partial class TranslationDialog : Window
{
    private readonly OpenRouterTranslationService _translationService;
    private readonly SubtitleDocument _document;
    private CancellationTokenSource? _cts;

    private bool _isTranslating;

    public SubtitleDocument? Result { get; private set; }

    public TranslationDialog(SubtitleDocument document)
    {
        InitializeComponent();
        _document = document;
        _translationService = new OpenRouterTranslationService();
        _translationService.ProgressChanged += OnProgressChanged;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTranslating)
        {
            try { _cts?.Cancel(); } catch { }
            return;
        }

        DialogResult = false;
        Close();
    }

    private async void TranslateButton_Click(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        _isTranslating = true;

        TranslateButton.IsEnabled = false;
        CancelButton.Content = "Cancel";
        TranslateButton.Content = "Translating...";

        try
        {
            var (apiKey, model) = OpenRouterConfig.Load();
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new Exception("OpenRouter API key is missing. Please set it in AI -> OpenRouter API Key...");

            LogText.Text = "Connecting to OpenRouter...\n";
            ProgressText.Text = "Preparing request...";

            var options = new TranslationOptions
            {
                SourceLanguage = GetSelectedTag(SourceLangCombo),
                TargetLanguage = GetSelectedTag(TargetLangCombo),
                Mode = GetSelectedMode(),
                CreateBilingual = BilingualCheck.IsChecked == true,
                PreserveAssTags = PreserveTagsCheck.IsChecked == true,
                BatchSize = int.Parse(((ComboBoxItem)BatchSizeCombo.SelectedItem).Content.ToString()!)
            };

            LogText.Text += $"Translating {_document.Lines.Count} lines from {options.SourceLanguage} to {options.TargetLanguage}...\n";
            LogText.Text += $"Mode: {options.Mode}, Batch size: {options.BatchSize}\n\n";

            Result = await _translationService.TranslateDocumentAsync(_document, options, apiKey, model, _cts.Token);

            LogText.Text += $"\n✓ Translation complete!\n";
            ProgressText.Text = "Complete!";
            ProgressBar.Value = 100;

            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            LogText.Text += "\n❌ Translation cancelled.";
            ProgressText.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            LogText.Text += $"\n❌ Error: {ex.Message}";
            ProgressText.Text = "Error occurred";
            MessageBox.Show($"Translation failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isTranslating = false;
            TranslateButton.IsEnabled = true;
            TranslateButton.Content = "Start Translation";
            CancelButton.Content = "Cancel";
        }
    }

    private string GetSelectedTag(ComboBox combo)
    {
        return (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
    }

    private TranslationMode GetSelectedMode()
    {
        if (LiteralMode.IsChecked == true) return TranslationMode.Literal;
        if (ColloquialMode.IsChecked == true) return TranslationMode.Colloquial;
        return TranslationMode.Natural;
    }

    private void OnProgressChanged(object? sender, TranslationProgress e)
    {
        Dispatcher.Invoke(() =>
        {
            ProgressBar.Value = e.ProgressPercent;
            ProgressText.Text = $"{e.Status} ({e.CompletedLines}/{e.TotalLines})";
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _translationService.Dispose();
        base.OnClosed(e);
    }
}
