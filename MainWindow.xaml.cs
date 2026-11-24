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

        private CancellationTokenSource? _cts;
        private bool _isProcessRunning;
        private string _lastGeneratedPath = string.Empty;

        public bool IsProcessRunning => _isProcessRunning;

        public string LastGeneratedPath
        {
            get => _lastGeneratedPath;
            set
            {
                _lastGeneratedPath = value ?? string.Empty;
                RefreshGeneratedPathBar();
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            InitializeDirectories();
            UpdateSessionBadge();

            BtnMenu_Click(BtnActivation, new RoutedEventArgs());
        }

        private static SolidColorBrush CreateFrozenBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }
            return brush;
        }

        public void UpdateSessionBadge()
        {
            if (TxtSidebarUser != null)
            {
                string uid = !string.IsNullOrWhiteSpace(Settings.DbConfig.Uid)
                    ? Settings.DbConfig.Uid
                    : Environment.UserName;
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
                MessageBox.Show(
                    $"[ERROR] Failed to create directories: {ex.Message}",
                    "Initialization Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
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
                try
                {
                    _cts.Cancel();
                }
                catch
                {
                    // Ignore if already disposed
                }

                if (Dispatcher.CheckAccess())
                {
                    TxtStatus.Text = "Arrêt demandé... Annulation propre des requêtes et étapes en cours.";
                    TxtStatus.Foreground = Brushes.DarkOrange;
                }
                else
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        TxtStatus.Text = "Arrêt demandé... Annulation propre des requêtes et étapes en cours.";
                        TxtStatus.Foreground = Brushes.DarkOrange;
                    });
                }
            }
        }

        public void RefreshGeneratedPathBar()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.InvokeAsync(RefreshGeneratedPathBar);
                return;
            }

            if (PanelOutputFileActions == null)
            {
                return;
            }

            string path = _lastGeneratedPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                string fileName = Path.GetFileName(path);
                if (fileName.Length > 44)
                {
                    fileName = fileName.Substring(0, 41) + "...";
                }

                if (TxtOpenGeneratedFileLabel != null)
                {
                    TxtOpenGeneratedFileLabel.Text = $"📄 Ouvrir : {fileName}";
                }
                if (BtnOpenGeneratedFile != null)
                {
                    BtnOpenGeneratedFile.Visibility = Visibility.Visible;
                    BtnOpenGeneratedFile.ToolTip = $"Ouvrir directement le fichier :\n{path}";
                }
                if (BtnRevealGeneratedFolder != null)
                {
                    BtnRevealGeneratedFolder.ToolTip = $"Afficher dans le dossier :\n{path}";
                }
                PanelOutputFileActions.Visibility = Visibility.Visible;
            }
            else if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                if (BtnOpenGeneratedFile != null)
                {
                    BtnOpenGeneratedFile.Visibility = Visibility.Collapsed;
                }
                if (BtnRevealGeneratedFolder != null)
                {
                    BtnRevealGeneratedFolder.ToolTip = $"Ouvrir le dossier :\n{path}";
                }
                PanelOutputFileActions.Visibility = Visibility.Visible;
            }
            else
            {
                PanelOutputFileActions.Visibility = Visibility.Collapsed;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
            catch
            {
                // Ignore disposal exceptions on window close
            }

            base.OnClosed(e);
        }

        /// <summary>
        /// Routes the user to the Help view and opens a specific internal tab.
        /// Called by the "?" buttons located in the individual views.
        /// </summary>
        public void OpenHelpTargetingTab(int tabIndex)
        {
            BtnMenu_Click(BtnHelp, new RoutedEventArgs());
            ViewHelp?.SelectTab(tabIndex);
        }

        /// <summary>
        /// Navigates directly to the SQL Data Extraction module and pre-fills the contract or Demand ID.
        /// </summary>
        public void NavigateToExtractionWithContract(string contractOrDemand, bool isDemandId, string env)
        {
            BtnMenu_Click(BtnExtraction, new RoutedEventArgs());
            ViewExtraction?.LoadContractFromExternal(contractOrDemand, isDemandId, env);
        }

        /// <summary>
        /// Navigates directly to the Contract Activation module and pre-fills the contract or Demand ID.
        /// </summary>
        public void NavigateToActivationWithContract(string contractOrDemand, bool isDemandId, string env)
        {
            BtnMenu_Click(BtnActivation, new RoutedEventArgs());
            ViewActivation?.LoadContractFromExternal(contractOrDemand, isDemandId, env);
        }

        /// <summary>
        /// Navigates directly to the Deep Comparison module and pre-fills the latest extracted CSV.
        /// </summary>
        public void NavigateToComparisonWithLatest(string? targetCsvPath)
        {
            BtnMenu_Click(BtnComparison, new RoutedEventArgs());
            ViewComparison?.LoadLatestExtractionForComparison(targetCsvPath);
        }

        private void BtnSidebarOutput_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(Settings.OutputDir);
                Process.Start(new ProcessStartInfo(Path.GetFullPath(Settings.OutputDir))
                {
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to open Output folder: {ex.Message}", "Folder Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnSidebarBaselines_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(Settings.BaselineDir);
                Process.Start(new ProcessStartInfo(Path.GetFullPath(Settings.BaselineDir))
                {
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to open Baselines folder: {ex.Message}", "Folder Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnMenu_Click(object? sender, RoutedEventArgs? e)
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

            if (clickedButton == BtnActivation && ViewActivation != null && BtnActivation != null)
            {
                ViewActivation.Visibility = Visibility.Visible;
                BtnActivation.Background = ActiveNavBrush;
                BtnActivation.Foreground = Brushes.White;
            }
            else if (clickedButton == BtnExtraction && ViewExtraction != null && BtnExtraction != null)
            {
                ViewExtraction.Visibility = Visibility.Visible;
                BtnExtraction.Background = ActiveNavBrush;
                BtnExtraction.Foreground = Brushes.White;
            }
            else if (clickedButton == BtnComparison && ViewComparison != null && BtnComparison != null)
            {
                ViewComparison.Visibility = Visibility.Visible;
                BtnComparison.Background = ActiveNavBrush;
                BtnComparison.Foreground = Brushes.White;
            }
            else if (clickedButton == BtnHelp && ViewHelp != null && BtnHelp != null)
            {
                ViewHelp.Visibility = Visibility.Visible;
                BtnHelp.Background = HelpNavBrush;
                BtnHelp.Foreground = Brushes.White;
            }
        }

        private static void ResetNavButton(System.Windows.Controls.Button? btn)
        {
            if (btn == null) return;
            btn.Background = InactiveNavBrush;
            btn.Foreground = InactiveTextBrush;
        }

        /// <summary>
        /// Centralized wrapper for executing long-running asynchronous tasks from the sub-views
        /// with a single unified CancellationTokenSource.
        /// </summary>
        public Task RunProcessAsync(Func<Task> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            return RunProcessAsync(_ => action());
        }

        public async Task RunProcessAsync(Func<CancellationToken, Task> action)
        {
            ArgumentNullException.ThrowIfNull(action);

            if (_isProcessRunning)
            {
                MessageBox.Show(
                    "Une opération est déjà en cours d'exécution. Veuillez attendre sa fin ou cliquer sur Stop.",
                    "Opération en cours",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            _isProcessRunning = true;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            CancellationToken token = _cts.Token;

            TxtStatus.Foreground = Brushes.RoyalBlue;
            if (TxtStatusDot != null)
            {
                TxtStatusDot.Foreground = Brushes.RoyalBlue;
            }

            try
            {
                await action(token);
                UpdateSessionBadge();
                RefreshGeneratedPathBar();

                if (_cts != null && _cts.IsCancellationRequested)
                {
                    TxtStatus.Foreground = Brushes.DarkOrange;
                    if (TxtStatusDot != null) TxtStatusDot.Foreground = Brushes.DarkOrange;
                }
                else if (TxtStatus.Foreground == Brushes.RoyalBlue)
                {
                    TxtStatus.Foreground = Brushes.ForestGreen;
                    if (TxtStatusDot != null) TxtStatusDot.Foreground = Brushes.ForestGreen;
                }
            }
            catch (OperationCanceledException)
            {
                if (string.IsNullOrWhiteSpace(TxtStatus.Text) ||
                    TxtStatus.Text.StartsWith("Arrêt demandé", StringComparison.OrdinalIgnoreCase))
                {
                    TxtStatus.Text = "Opération arrêtée proprement par l'utilisateur.";
                }
                TxtStatus.Foreground = Brushes.DarkOrange;
                if (TxtStatusDot != null) TxtStatusDot.Foreground = Brushes.DarkOrange;
                RefreshGeneratedPathBar();
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Erreur : {ex.Message}";
                TxtStatus.Foreground = Brushes.Crimson;
                if (TxtStatusDot != null) TxtStatusDot.Foreground = Brushes.Crimson;
                RefreshGeneratedPathBar();
                MessageBox.Show(ex.Message, "Erreur d'exécution", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                RefreshGeneratedPathBar();
                _isProcessRunning = false;
            }
        }

        public static void TryOpenFileDirect(string? filePath, string contextTitle = "Ouverture de fichier")
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                MessageBox.Show(
                    "Aucun fichier n'est sélectionné.",
                    contextTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            string trimmed = filePath.Trim();
            if (!File.Exists(trimmed))
            {
                MessageBox.Show(
                    $"Le fichier est introuvable ou inaccessible (vérifiez le chemin ou la connexion VPN) :\n\n{trimmed}",
                    contextTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(Path.GetFullPath(trimmed))
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Impossible d'ouvrir le fichier :\n{trimmed}\n\nDétail : {ex.Message}",
                    contextTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void BtnOpenGeneratedFile_Click(object sender, RoutedEventArgs e)
        {
            if (File.Exists(_lastGeneratedPath))
            {
                TryOpenFileDirect(_lastGeneratedPath, "Rapport généré");
            }
            else
            {
                Hyperlink_Click(sender, e);
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
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{Path.GetFullPath(LastGeneratedPath)}\"")
                    {
                        UseShellExecute = true
                    });
                }
                else if (Directory.Exists(LastGeneratedPath))
                {
                    Process.Start(new ProcessStartInfo(Path.GetFullPath(LastGeneratedPath))
                    {
                        UseShellExecute = true,
                        Verb = "open"
                    });
                }
                else
                {
                    MessageBox.Show(
                        "File or folder not found. It might have been moved or deleted.",
                        "Not Found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening path: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
