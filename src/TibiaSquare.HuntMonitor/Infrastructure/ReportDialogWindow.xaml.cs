using System.Windows;

namespace TibiaSquare.HuntMonitor.Infrastructure;

public partial class ReportDialogWindow : Window
{
    private readonly DiagnosticReportService _reportService;

    public ReportDialogWindow(DiagnosticReportService reportService)
    {
        _reportService = reportService;
        InitializeComponent();
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        var description = DescriptionBox.Text?.Trim() ?? "";

        SendButton.IsEnabled = false;
        SendButton.Content = "Sending...";
        ErrorText.Visibility = Visibility.Collapsed;

        try
        {
            var success = await _reportService.SendReportAsync(description);
            if (success)
            {
                DialogResult = true;
                Close();
            }
            else
            {
                ErrorText.Text = "Failed to send report. Please try again.";
                ErrorText.Visibility = Visibility.Visible;
                SendButton.IsEnabled = true;
                SendButton.Content = "Send Report";
            }
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Error: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
            SendButton.IsEnabled = true;
            SendButton.Content = "Send Report";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
