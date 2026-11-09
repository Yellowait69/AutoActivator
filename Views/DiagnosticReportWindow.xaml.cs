using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AutoActivator.Config;

namespace AutoActivator.Gui.Views
{
    public partial class DiagnosticReportWindow : Window
    {
        private readonly string _fullReport;
        private readonly string _contractReference;

        public DiagnosticReportWindow(
            string title,
            string subtitle,
            string status,
            string reportContent,
            string? contractReference = null)
        {
            InitializeComponent();

            _fullReport = reportContent ?? string.Empty;
            _contractReference = string.IsNullOrWhiteSpace(contractReference)
                ? "Diagnostic"
                : contractReference.Trim();

            Title = title;
            TxtReportTitle.Text = title;
            TxtReportSubtitle.Text = subtitle;
            TxtReportContent.Text = _fullReport;

            ApplyStatusBadge(status);

            PreviewKeyDown += OnWindowPreviewKeyDown;
        }

        private void ApplyStatusBadge(string? status)
        {
            string normalized = (status ?? "DIAGNOSTIC").Trim().ToUpperInvariant();
            TxtStatusBadge.Text = normalized;

            switch (normalized)
            {
                case "SUCCESS":
                case "ACTIVATED":
                    StatusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DCFCE7"));
                    StatusBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#86EFAC"));
                    TxtStatusBadge.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#15803D"));
                    break;

                case "ALREADY ACTIVE":
                case "ALREADY_ACTIVE":
                case "PENDING ASYNC LISA CREATION":
                    StatusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEF3C7"));
                    StatusBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FCD34D"));
                    TxtStatusBadge.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B45309"));
                    break;

                case "FAILED":
                case "BLOCKED":
                case "TECHNICAL_FAILURE":
                    StatusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FEE2E2"));
                    StatusBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FCA5A5"));
                    TxtStatusBadge.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B91C1C"));
                    break;

                default:
                    StatusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EFF6FF"));
                    StatusBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BFDBFE"));
                    TxtStatusBadge.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1D4ED8"));
                    break;
            }
        }

        private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                TxtSearchFilter.Focus();
                TxtSearchFilter.SelectAll();
                e.Handled = true;
            }
        }

        private void TxtSearchFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            string query = TxtSearchFilter.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(query))
            {
                TxtReportContent.Text = _fullReport;
                return;
            }

            string[] matchingLines = _fullReport
                .Replace("\r\n", "\n")
                .Split('\n')
                .Where(line => line.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();

            TxtReportContent.Text = matchingLines.Length > 0
                ? string.Join(Environment.NewLine, matchingLines)
                : $"No lines matching \"{query}\".";
        }

        private void BtnClearFilter_Click(object sender, RoutedEventArgs e)
        {
            TxtSearchFilter.Text = string.Empty;
            TxtReportContent.Text = _fullReport;
        }

        private async void BtnCopyClipboard_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_fullReport);
                string originalContent = BtnCopyClipboard.Content?.ToString() ?? "Copy to Clipboard";
                BtnCopyClipboard.Content = "Copied!";
                await Task.Delay(1500);
                BtnCopyClipboard.Content = originalContent;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Unable to copy to clipboard: {ex.Message}",
                    "Clipboard Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void BtnExportTxt_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string safeName = string.Join("_", _contractReference.Split(Path.GetInvalidFileNameChars()));
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Export Diagnostic Report",
                    Filter = "Text Report (*.txt)|*.txt|All Files (*.*)|*.*",
                    InitialDirectory = Directory.Exists(Settings.OutputDir)
                        ? Path.GetFullPath(Settings.OutputDir)
                        : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    FileName = $"Diagnostic_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
                };

                if (dialog.ShowDialog(this) == true)
                {
                    File.WriteAllText(
                        dialog.FileName,
                        _fullReport,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                    TxtFooterHint.Text = $"Saved to {dialog.FileName}";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Unable to export diagnostic report: {ex.Message}",
                    "Export Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
