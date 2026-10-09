using System.Windows;

namespace NovaManager;

public partial class FeedbackDialog : Window
{
    private readonly Func<string, Task<string>> submitFeedback;

    public FeedbackDialog(string title, string prompt, Func<string, Task<string>> submit)
    {
        InitializeComponent();
        Title = title;
        FeedbackDialogTitleText.Text = title;
        FeedbackDialogPromptText.Text = prompt;
        submitFeedback = submit;
        Loaded += (_, _) => FeedbackDialogInput.Focus();
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        var description = FeedbackDialogInput.Text.Trim();
        if (description.Length == 0)
        {
            FeedbackDialogStatusText.Text = "Enter a description before sending.";
            FeedbackDialogInput.Focus();
            return;
        }

        FeedbackDialogSubmitButton.IsEnabled = false;
        FeedbackDialogStatusText.Text = "Sending to GitHub…";
        try
        {
            var issueUrl = await submitFeedback(description);
            FeedbackDialogStatusText.Text = $"Submitted successfully: {issueUrl}";
            DialogResult = true;
        }
        catch (Exception exception)
        {
            FeedbackDialogStatusText.Text = $"Could not submit the report. {exception.Message}";
            FeedbackDialogSubmitButton.IsEnabled = true;
        }
    }
}
