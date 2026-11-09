using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using AutoActivator.Config;
using AutoActivator.Services;
using AutoActivator.Utils;

namespace AutoActivator.Gui
{
    public partial class MainWindow : Window
    {
        private static readonly SolidColorBrush InactiveNavBrush = CreateFrozenBrush("#1E293B");
        private static readonly SolidColorBrush InactiveTextBrush = CreateFrozenBrush("#CBD5E1");
        private static readonly SolidColorBrush ActiveNavBrush = CreateFrozenBrush("#10B981");
        private static readonly SolidColorBrush HelpNavBrush = CreateFrozenBrush("#7C3AED");

        private CancellationTokenSource _cts;
        private bool _isProcessRunning;

        public string LastGeneratedPath { get; set; } = "";

        public MainWindow()
        {
            InitializeComponent();
            InitializeDirectories();
            UpdateSessionBadge();

            BtnMenu_Click(BtnActivation, null);
        }

        private static SolidColorBrush CreateFrozenBrush(string hex)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(hex);
            if (brush != null && brush.CanFreeze)
            {
                brush.Freeze();
            }
            return brush;
        }

        public void UpdateSessionBadge()
        {
            if (TxtSidebarUser != null)
            {
                string uid = !string.IsNullOrWhiteSpace(Settings.DbConfig.Uid) ? Settings.DbConfig.Uid : Environment.UserName;
                TxtSidebarUser.Text = $"User: {uid}";
            }
            if (TxtSidebarServer != null)
            {
                string srv = !string.IsNullOrWhiteSpace(MicroFocusApiService.ActiveServer)
                    ? $"Node: {MicroFocusApiService.ActiveServer}"
                    : "ESCWA Connected";
                TxtSidebarServer.Text = srv;
            }
        }

        /// <summary>
        /// Ensures all required local directories exist before the application starts writing files.
        /// </summary>
        private void InitializeDirectories()
        {
            try
            {
                Settings.EnsureDirectoriesExist();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"[ERROR] Failed to create directories: {ex.Message}", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Returns the active CancellationToken for the currently running background operation.
        /// </summary>
        public CancellationToken GetCancellationToken()
        {
            if (_cts == null || _cts.IsCancellationRequested)
            {
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
            }
            return _cts.Token;
        }

        /// <summary>
        /// Cancels the currently active background process if one is running.
        /// </summary>
        public void CancelCurrentProcess()
        {
            if (_cts != null && !_cts.IsCancellationRequested)
            {
                _cts.Cancel();
                TxtStatus.Text = "Canceling... The operation will stop shortly.";
                TxtStatus.Foreground = Brushes.DarkOrange;
            }
        }

        /// <summary>
        /// Routes the user to the Help view and opens a specific internal tab.
        /// Called by the "?" buttons located in the individual views.
        /// </summary>
        public void OpenHelpTargetingTab(int tabIndex)
        {
            BtnMenu_Click(BtnHelp, null);
            ViewHelp.SelectTab(tabIndex);
        }

        private void BtnMenu_Click(object sender, RoutedEventArgs e)
        {
            if (ViewActivation != null) ViewActivation.Visibility = Visibility.Collapsed;
            if (ViewExtraction != null) ViewExtraction.Visibility = Visibility.Collapsed;
            if (ViewComparison != null) ViewComparison.Visibility = Visibility.Collapsed;
            if (ViewHelp != null) ViewHelp.Visibility = Visibility.Collapsed;

            ResetNavButton(BtnActivation);
            ResetNavButton(BtnExtraction);
            ResetNavButton(BtnComparison);
            ResetNavButton(BtnHelp);

            var clickedButton = sender as System.Windows.Controls.Button;

            if (clickedButton == BtnActivation)
            {
                ViewActivation.Visibility = Visibility.Visible;
                BtnActivation.Background = ActiveNavBrush;
                BtnActivation.Foreground = Brushes.White;
            }
            else if (clickedButton == BtnExtraction)
            {
                ViewExtraction.Visibility = Visibility.Visible;
                BtnExtraction.Background = ActiveNavBrush;
                BtnExtraction.Foreground = Brushes.White;
            }
            else if (clickedButton == BtnComparison)
            {
                ViewComparison.Visibility = Visibility.Visible;
                BtnComparison.Background = ActiveNavBrush;
                BtnComparison.Foreground = Brushes.White;
            }
            else if (clickedButton == BtnHelp)
            {
                ViewHelp.Visibility = Visibility.Visible;
                BtnHelp.Background = HelpNavBrush;
                BtnHelp.Foreground = Brushes.White;
            }
        }

        private static void ResetNavButton(System.Windows.Controls.Button btn)
        {
            if (btn == null) return;
            btn.Background = InactiveNavBrush;
            btn.Foreground = InactiveTextBrush;
        }

        /// <summary>
        /// Centralized wrapper for executing long-running asynchronous tasks from the sub-views.
        /// It manages the global loading bar, cancellation token lifecycle, status text colors, and catches unhandled exceptions safely.
        /// </summary>
        public async Task RunProcessAsync(Func<Task> action)
        {
            if (_isProcessRunning)
            {
                MessageBox.Show("An operation is already in progress. Please wait for it to finish or cancel it first.",
                    "Operation in Progress", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _isProcessRunning = true;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            PrgLoading.Visibility = Visibility.Visible;
            LnkFile.Visibility = Visibility.Collapsed;
            TxtStatus.Foreground = Brushes.RoyalBlue;

            try
            {
                await action();
                UpdateSessionBadge();

                if (!string.IsNullOrEmpty(LastGeneratedPath) &&
                    (Directory.Exists(LastGeneratedPath) || File.Exists(LastGeneratedPath)))
                {
                    LnkFile.Visibility = Visibility.Visible;
                }

                if (_cts != null && _cts.IsCancellationRequested)
                {
                    TxtStatus.Foreground = Brushes.DarkOrange;
                }
                else if (TxtStatus.Foreground == Brushes.RoyalBlue)
                {
                    TxtStatus.Foreground = Brushes.ForestGreen;
                }
            }
            catch (OperationCanceledException)
            {
                TxtStatus.Text = "Operation canceled by user.";
                TxtStatus.Foreground = Brushes.DarkOrange;
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Error: {ex.Message}";
                TxtStatus.Foreground = Brushes.Crimson;
                MessageBox.Show(ex.Message, "Execution Error", MessageBoxButton.OK, MessageBoxImage.Error);

                if (!string.IsNullOrEmpty(LastGeneratedPath) &&
                    (Directory.Exists(LastGeneratedPath) || File.Exists(LastGeneratedPath)))
                {
                    LnkFile.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                PrgLoading.Visibility = Visibility.Collapsed;
                _isProcessRunning = false;
            }
        }

        /// <summary>
        /// Converts an Excel file to a CSV file using ExcelHelper (ExcelDataReader).
        /// </summary>
        public string PrepareCsvFromExcel(string excelFilePath, string environmentSuffix)
        {
            return ExcelHelper.ConvertExcelToCsv(excelFilePath, Settings.OutputDir, $"ConvertedBatch_{environmentSuffix}");
        }

        private void Hyperlink_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(LastGeneratedPath))
                {
                    Process.Start("explorer.exe", $"/select,\"{LastGeneratedPath}\"");
                }
                else if (Directory.Exists(LastGeneratedPath))
                {
                    Process.Start("explorer.exe", LastGeneratedPath);
                }
                else
                {
                    MessageBox.Show("File or folder not found. It might have been moved or deleted.", "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening path: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}