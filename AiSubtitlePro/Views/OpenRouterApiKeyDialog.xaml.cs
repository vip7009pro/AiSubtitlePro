using AiSubtitlePro.Infrastructure.AI;
using System.Windows;

namespace AiSubtitlePro.Views;

public partial class OpenRouterApiKeyDialog : Window
{
    public OpenRouterApiKeyDialog()
    {
        InitializeComponent();

        var (apiKey, model) = OpenRouterConfig.Load();
        if (!string.IsNullOrWhiteSpace(apiKey))
            ApiKeyBox.Password = apiKey;
        if (!string.IsNullOrWhiteSpace(model))
            ModelBox.Text = model;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        OpenRouterConfig.Save(ApiKeyBox.Password, ModelBox.Text);
        DialogResult = true;
        Close();
    }
}
