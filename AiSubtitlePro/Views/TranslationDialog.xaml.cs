using AiSubtitlePro.Core.Models;
using AiSubtitlePro.Infrastructure.AI;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AiSubtitlePro.Views;

/// <summary>
/// Translation Dialog for LibreTranslate integration
/// </summary>
public partial class TranslationDialog : Window
{
    private readonly OpenRouterTranslationService _translationService;
    private readonly SubtitleDocument _document;
    private CancellationTokenSource? _cts;
    private readonly System.Diagnostics.Stopwatch _translateStopwatch = new();

    private DispatcherTimer? _elapsedTimer;
    private string _progressStatus = "";

    private bool _isTranslating;

    public SubtitleDocument? Result { get; private set; }

    public TranslationDialog(SubtitleDocument document)
    {
        InitializeComponent();
        _document = document;
        _translationService = new OpenRouterTranslationService();
        _translationService.ProgressChanged += OnProgressChanged;
        _translationService.PromptPrepared += OnPromptPrepared;
        _translationService.RawResponseReceived += OnRawResponseReceived;
    }

    private void CopyPromptButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var options = new TranslationOptions
            {
                SourceLanguage = GetSelectedTag(SourceLangCombo),
                TargetLanguage = GetSelectedTag(TargetLangCombo),
                Mode = GetSelectedMode(),
                CreateBilingual = BilingualCheck.IsChecked == true,
                PreserveAssTags = PreserveTagsCheck.IsChecked == true,
                BatchSize = _document.Lines.Count
            };

            var payloadItems = new List<object>();
            foreach (var line in _document.Lines)
            {
                var text = TranslationService.ExtractTranslatableTextPublic(line.Text, options.PreserveAssTags);
                payloadItems.Add(new { index = line.Index, text });
            }

            var system = OpenRouterTranslationService.BuildSystemPromptPublic(options);
            var user = OpenRouterTranslationService.BuildUserPromptPublic(options, payloadItems);

            var textToCopy = "--- SYSTEM ---\n" + system + "\n\n--- USER ---\n" + user;
            Clipboard.SetText(textToCopy);

            LogText.Text += $"\n✓ Copied prompt for {_document.Lines.Count} line(s) to clipboard.\n";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to copy prompt:\n{ex.Message}", "Copy Prompt", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnPromptPrepared(object? sender, OpenRouterTranslationService.PromptDebugInfo e)
    {
        Dispatcher.Invoke(() =>
        {
            DebugPromptBox.Text =
                $"Model: {e.Model}\n" +
                $"Batch: {e.BatchStartLine}-{e.BatchEndLine}\n\n" +
                "--- SYSTEM ---\n" +
                e.SystemPrompt +
                "\n\n--- USER ---\n" +
                e.UserPrompt;
        });
    }

    private void OnRawResponseReceived(object? sender, OpenRouterTranslationService.RawResponseDebugInfo e)
    {
        Dispatcher.Invoke(() =>
        {
            DebugRawResponseBox.Text =
                $"Model: {e.Model}\n" +
                $"Batch: {e.BatchStartLine}-{e.BatchEndLine}\n\n" +
                e.RawContent;
        });
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

        _translateStopwatch.Restart();
        StartElapsedTimer();

        TranslateButton.IsEnabled = false;
        CancelButton.Content = "Cancel";
        TranslateButton.Content = "Translating...";

        try
        {
            var (apiKey, model) = OpenRouterConfig.Load();
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new Exception("OpenRouter API key is missing. Please set it in AI -> OpenRouter API Key...");

            LogText.Text = "Connecting to OpenRouter...\n";
            _progressStatus = "Preparing request...";
            ProgressText.Text = $"{_progressStatus} - elapsed {FormatElapsed(_translateStopwatch.Elapsed)}";

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

            LogText.Text += $"\n✓ Translation complete! (elapsed {FormatElapsed(_translateStopwatch.Elapsed)})\n";
            _progressStatus = "Complete!";
            ProgressText.Text = $"{_progressStatus} - elapsed {FormatElapsed(_translateStopwatch.Elapsed)}";
            ProgressBar.Value = 100;

            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            LogText.Text += $"\n❌ Translation cancelled. (elapsed {FormatElapsed(_translateStopwatch.Elapsed)})";
            _progressStatus = "Cancelled";
            ProgressText.Text = $"{_progressStatus} - elapsed {FormatElapsed(_translateStopwatch.Elapsed)}";
        }
        catch (Exception ex)
        {
            LogText.Text += $"\n❌ Error: {ex.Message}";
            _progressStatus = "Error occurred";
            ProgressText.Text = $"{_progressStatus} - elapsed {FormatElapsed(_translateStopwatch.Elapsed)}";
            MessageBox.Show($"Translation failed:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StopElapsedTimer();
            _isTranslating = false;
            TranslateButton.IsEnabled = true;
            TranslateButton.Content = "Start Translation";
            CancelButton.Content = "Cancel";
        }
    }

    private void StartElapsedTimer()
    {
        StopElapsedTimer();

        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _elapsedTimer.Tick += (_, __) =>
        {
            if (_isTranslating)
                ProgressText.Text = $"{_progressStatus} - elapsed {FormatElapsed(_translateStopwatch.Elapsed)}";
        };
        _elapsedTimer.Start();
    }

    private void StopElapsedTimer()
    {
        try
        {
            if (_elapsedTimer != null)
            {
                _elapsedTimer.Stop();
                _elapsedTimer = null;
            }
        }
        catch
        {
        }
    }

    private void ApplyExternalJsonButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var options = new TranslationOptions
            {
                SourceLanguage = GetSelectedTag(SourceLangCombo),
                TargetLanguage = GetSelectedTag(TargetLangCombo),
                Mode = GetSelectedMode(),
                CreateBilingual = BilingualCheck.IsChecked == true,
                PreserveAssTags = PreserveTagsCheck.IsChecked == true,
                BatchSize = int.Parse(((ComboBoxItem)BatchSizeCombo.SelectedItem).Content.ToString()!)
            };

            var doc = _document.Clone();
            var applied = OpenRouterTranslationService.ApplyTranslationJsonToDocument(doc, options, ExternalJsonBox.Text);
            LogText.Text += $"\n✓ Applied pasted JSON to {applied} line(s).\n";
            Result = doc;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            LogText.Text += $"\n❌ Error applying pasted JSON: {ex.Message}";
            MessageBox.Show($"Failed to apply pasted JSON:\n{ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
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
            var elapsed = FormatElapsed(_translateStopwatch.Elapsed);
            _progressStatus = $"{e.Status} ({e.CompletedLines}/{e.TotalLines})";
            ProgressText.Text = $"{_progressStatus} - elapsed {elapsed}";
        });
    }

    private static string FormatElapsed(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
        return $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        StopElapsedTimer();
        _translationService.Dispose();
        base.OnClosed(e);
    }
}
