using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace AutoActivator.Utils
{
    public enum OperationTerminalState
    {
        Success,
        Warning,
        Cancelled,
        Failed
    }

    /// <summary>
    /// Contrôleur UI unifié pilotant une ProgressBar déterminée (0 % -> 100 % fluide)
    /// et un chronomètre temps réel (Stopwatch + DispatcherTimer à 100 ms) avec conservation du temps final.
    /// </summary>
    public sealed class OperationProgressController
    {
        private static readonly SolidColorBrush RunningBrush = CreateFrozenBrush("#2563EB");
        private static readonly SolidColorBrush SuccessBrush = CreateFrozenBrush("#2563EB");
        private static readonly SolidColorBrush WarningBrush = CreateFrozenBrush("#2563EB");
        private static readonly SolidColorBrush CancelledBrush = CreateFrozenBrush("#64748B");
        private static readonly SolidColorBrush ErrorBrush = CreateFrozenBrush("#DC2626");

        private readonly Dispatcher _dispatcher;
        private readonly ProgressBar _progressBar;
        private readonly Run? _statusRun;
        private readonly TextBlock? _statusTextBlock;
        private readonly TextBlock? _processedText;
        private readonly TextBlock? _plannedText;
        private readonly TextBlock? _elapsedText;
        private readonly TextBlock? _percentageText;
        private readonly Stopwatch _stopwatch = new Stopwatch();
        private readonly DispatcherTimer _timer;

        private double _completedUnits;
        private double _totalUnits;
        private bool _showEta;
        private int _pendingUiUpdate;
        private PendingState? _latestPendingState;

        private sealed record PendingState(
            double CompletedUnits,
            double TotalUnits,
            string? ProcessedDisplay,
            string? PlannedDisplay,
            string? Message,
            bool ShowEta);

        public OperationProgressController(
            Dispatcher dispatcher,
            ProgressBar progressBar,
            Run? statusRun,
            TextBlock? processedText,
            TextBlock? plannedText,
            TextBlock? elapsedText,
            TextBlock? percentageText,
            TextBlock? statusTextBlock = null)
        {
            _progressBar = progressBar ?? throw new ArgumentNullException(nameof(progressBar));
            _dispatcher = dispatcher ?? progressBar.Dispatcher;
            _statusRun = statusRun;
            _statusTextBlock = statusTextBlock;
            _processedText = processedText;
            _plannedText = plannedText;
            _elapsedText = elapsedText;
            _percentageText = percentageText;

            _timer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _timer.Tick += OnTimerTick;
        }

        public OperationProgressController(
            ProgressBar progressBar,
            Run? statusRun,
            TextBlock? processedText,
            TextBlock? plannedText,
            TextBlock? elapsedText,
            TextBlock? percentageText,
            TextBlock? statusTextBlock = null)
            : this(
                progressBar?.Dispatcher ?? throw new ArgumentNullException(nameof(progressBar)),
                progressBar,
                statusRun,
                processedText,
                plannedText,
                elapsedText,
                percentageText,
                statusTextBlock)
        {
        }

        public TimeSpan Elapsed => _stopwatch.Elapsed;

        private static SolidColorBrush CreateFrozenBrush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            if (brush.CanFreeze)
            {
                brush.Freeze();
            }
            return brush;
        }

        public void Reset(string readyMessage = "Prêt")
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.InvokeAsync(() => Reset(readyMessage));
                return;
            }

            _timer.Stop();
            _stopwatch.Reset();
            _completedUnits = 0;
            _totalUnits = 0;
            _showEta = false;
            _latestPendingState = null;

            _progressBar.IsIndeterminate = false;
            _progressBar.Minimum = 0;
            _progressBar.Maximum = 1000;
            _progressBar.Value = 0;
            _progressBar.Foreground = RunningBrush;

            SetStatusMessage(readyMessage);
            if (_processedText != null) _processedText.Text = "0";
            if (_plannedText != null) _plannedText.Text = "0";
            if (_percentageText != null) _percentageText.Text = "0%";
            if (_elapsedText != null) _elapsedText.Text = "  0.0s";
        }

        /// <summary>
        /// Démarre une nouvelle opération (en mode indéterminé si totalUnits &lt;= 0, déterminé sinon)
        /// et réinitialise immédiatement le chronomètre temps réel.
        /// </summary>
        public void Start(string initialMessage, double totalUnits = 0, bool showEta = false)
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.InvokeAsync(() => Start(initialMessage, totalUnits, showEta));
                return;
            }

            if (totalUnits <= 0)
            {
                StartIndeterminate(initialMessage);
                _showEta = showEta;
                return;
            }

            _completedUnits = 0;
            _totalUnits = totalUnits;
            _showEta = showEta;
            _latestPendingState = null;

            _stopwatch.Restart();
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }

            ApplyProgressOnUi(new PendingState(
                0,
                totalUnits,
                "0",
                ((int)Math.Round(totalUnits)).ToString(CultureInfo.InvariantCulture),
                initialMessage,
                showEta));
        }

        /// <summary>
        /// Démarre immédiatement le chronomètre temps réel en mode indéterminé (courte phase d'initialisation uniquement).
        /// </summary>
        public void StartIndeterminate(string initialMessage)
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.InvokeAsync(() => StartIndeterminate(initialMessage));
                return;
            }

            _completedUnits = 0;
            _totalUnits = 0;
            _showEta = false;
            _latestPendingState = null;

            _stopwatch.Restart();
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }

            _progressBar.Minimum = 0;
            _progressBar.Maximum = 1000;
            _progressBar.Value = 0;
            _progressBar.IsIndeterminate = true;
            _progressBar.Foreground = RunningBrush;

            SetStatusMessage(initialMessage);
            if (_processedText != null) _processedText.Text = "0";
            if (_plannedText != null) _plannedText.Text = "...";
            if (_percentageText != null) _percentageText.Text = "0%";
            UpdateElapsedDisplay(isRunning: true);
        }

        /// <summary>
        /// Raccourci fluide pour signaler l'avancement (conserve automatiquement l'option ETA active).
        /// </summary>
        public void Report(
            double completedUnits,
            double totalUnits,
            string? message = null,
            bool? showEta = null,
            string? processedDisplay = null,
            string? plannedDisplay = null)
        {
            ReportProgress(
                completedUnits,
                totalUnits,
                processedDisplay: processedDisplay,
                plannedDisplay: plannedDisplay,
                message: message,
                showEta: showEta ?? _showEta);
        }

        /// <summary>
        /// Met à jour la barre de progression déterminée (0 % -> 100 % fluide) sans saturer le thread UI.
        /// </summary>
        public void ReportProgress(
            double completedUnits,
            double totalUnits,
            string? processedDisplay = null,
            string? plannedDisplay = null,
            string? message = null,
            bool showEta = false)
        {
            var state = new PendingState(
                Math.Max(0, completedUnits),
                Math.Max(1, totalUnits),
                processedDisplay,
                plannedDisplay,
                message,
                showEta);

            if (_dispatcher.CheckAccess())
            {
                ApplyProgressOnUi(state);
                return;
            }

            _latestPendingState = state;
            if (Interlocked.Exchange(ref _pendingUiUpdate, 1) == 0)
            {
                _dispatcher.InvokeAsync(() =>
                {
                    Interlocked.Exchange(ref _pendingUiUpdate, 0);
                    PendingState? snapshot = _latestPendingState;
                    if (snapshot != null)
                    {
                        ApplyProgressOnUi(snapshot);
                    }
                }, DispatcherPriority.Render);
            }
        }

        public void UpdateMessageOnly(string message)
        {
            if (_dispatcher.CheckAccess())
            {
                SetStatusMessage(message);
                return;
            }

            _dispatcher.InvokeAsync(() => SetStatusMessage(message), DispatcherPriority.Background);
        }

        public void Complete(
            string finalMessage,
            bool warning = false,
            int? finalProcessed = null,
            int? finalTotal = null)
        {
            Complete(
                finalMessage,
                warning ? OperationTerminalState.Warning : OperationTerminalState.Success,
                finalProcessed,
                finalTotal);
        }

        public void Cancel(
            string finalMessage,
            int? finalProcessed = null,
            int? finalTotal = null)
        {
            Complete(finalMessage, OperationTerminalState.Cancelled, finalProcessed, finalTotal);
        }

        public void Fail(
            string finalMessage,
            int? finalProcessed = null,
            int? finalTotal = null)
        {
            Complete(finalMessage, OperationTerminalState.Failed, finalProcessed, finalTotal);
        }

        public void Complete(
            string finalMessage,
            OperationTerminalState terminalState,
            int? finalProcessed = null,
            int? finalTotal = null)
        {
            if (!_dispatcher.CheckAccess())
            {
                _dispatcher.Invoke(() => Complete(finalMessage, terminalState, finalProcessed, finalTotal));
                return;
            }

            _stopwatch.Stop();
            _timer.Stop();
            _latestPendingState = null;
            _showEta = false;

            _progressBar.IsIndeterminate = false;
            _progressBar.Minimum = 0;
            _progressBar.Maximum = 1000;

            if (finalTotal.HasValue && finalTotal.Value > 0)
            {
                _totalUnits = finalTotal.Value;
                if (_plannedText != null)
                {
                    _plannedText.Text = finalTotal.Value.ToString(CultureInfo.InvariantCulture);
                }
            }

            if (finalProcessed.HasValue && finalProcessed.Value >= 0)
            {
                _completedUnits = finalProcessed.Value;
                if (_processedText != null)
                {
                    _processedText.Text = finalProcessed.Value.ToString(CultureInfo.InvariantCulture);
                }
            }

            switch (terminalState)
            {
                case OperationTerminalState.Success:
                    _progressBar.Foreground = SuccessBrush;
                    _progressBar.Value = 1000;
                    if (_percentageText != null) _percentageText.Text = "100%";
                    if (!finalProcessed.HasValue && _totalUnits > 0 && _processedText != null)
                    {
                        _processedText.Text = ((int)Math.Round(_totalUnits)).ToString(CultureInfo.InvariantCulture);
                    }
                    break;

                case OperationTerminalState.Warning:
                    _progressBar.Foreground = WarningBrush;
                    _progressBar.Value = _totalUnits > 0
                        ? Math.Clamp(_completedUnits / _totalUnits * 1000.0, 0, 1000)
                        : 1000;
                    if (_percentageText != null)
                    {
                        double pct = _totalUnits > 0 ? Math.Clamp(_completedUnits / _totalUnits * 100.0, 0, 100) : 100.0;
                        _percentageText.Text = $"{pct:F0}%";
                    }
                    break;

                case OperationTerminalState.Cancelled:
                    _progressBar.Foreground = CancelledBrush;
                    if (_totalUnits > 0)
                    {
                        double ratio = Math.Clamp(_completedUnits / _totalUnits, 0.0, 1.0);
                        _progressBar.Value = ratio * 1000.0;
                        if (_percentageText != null) _percentageText.Text = $"{ratio * 100.0:F0}%";
                    }
                    break;

                case OperationTerminalState.Failed:
                    _progressBar.Foreground = ErrorBrush;
                    if (_totalUnits > 0)
                    {
                        double ratio = Math.Clamp(_completedUnits / _totalUnits, 0.0, 1.0);
                        _progressBar.Value = Math.Max(40.0, ratio * 1000.0);
                        if (_percentageText != null) _percentageText.Text = $"{ratio * 100.0:F0}%";
                    }
                    break;
            }

            SetStatusMessage(finalMessage);
            UpdateElapsedDisplay(isRunning: false);
        }

        private void ApplyProgressOnUi(PendingState state)
        {
            if (!_stopwatch.IsRunning)
            {
                _stopwatch.Start();
            }
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }

            _completedUnits = Math.Min(state.CompletedUnits, state.TotalUnits);
            _totalUnits = state.TotalUnits;
            _showEta = state.ShowEta;

            double ratio = state.TotalUnits > 0
                ? Math.Clamp(_completedUnits / state.TotalUnits, 0.0, 1.0)
                : 0.0;

            _progressBar.IsIndeterminate = false;
            _progressBar.Minimum = 0;
            _progressBar.Maximum = 1000;
            _progressBar.Value = ratio * 1000.0;
            _progressBar.Foreground = RunningBrush;

            if (_processedText != null)
            {
                _processedText.Text = state.ProcessedDisplay
                    ?? ((int)Math.Floor(_completedUnits + 1e-6)).ToString(CultureInfo.InvariantCulture);
            }

            if (_plannedText != null)
            {
                _plannedText.Text = state.PlannedDisplay
                    ?? ((int)Math.Round(state.TotalUnits)).ToString(CultureInfo.InvariantCulture);
            }

            if (_percentageText != null)
            {
                _percentageText.Text = $"{ratio * 100.0:F0}%";
            }

            if (!string.IsNullOrWhiteSpace(state.Message))
            {
                SetStatusMessage(state.Message);
            }

            UpdateElapsedDisplay(isRunning: true);
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            UpdateElapsedDisplay(isRunning: _stopwatch.IsRunning);
        }

        private void SetStatusMessage(string message)
        {
            string safe = string.IsNullOrWhiteSpace(message) ? "Prêt" : message;
            if (_statusRun != null)
            {
                _statusRun.Text = safe;
            }
            if (_statusTextBlock != null)
            {
                _statusTextBlock.Text = safe;
            }
        }

        private void UpdateElapsedDisplay(bool isRunning)
        {
            if (_elapsedText == null)
            {
                return;
            }

            TimeSpan elapsed = _stopwatch.Elapsed;
            string formattedElapsed = FormatDuration(elapsed);

            if (isRunning &&
                _showEta &&
                _totalUnits > 1 &&
                _completedUnits >= 1.0 &&
                _completedUnits < _totalUnits &&
                elapsed.TotalSeconds >= 0.4)
            {
                double remainingUnits = Math.Max(0.0, _totalUnits - _completedUnits);
                double secondsPerUnit = elapsed.TotalSeconds / _completedUnits;
                TimeSpan eta = TimeSpan.FromSeconds(secondsPerUnit * remainingUnits);
                _elapsedText.Text = $"  {formattedElapsed} (ETA ~{FormatShortEta(eta)})";
            }
            else
            {
                _elapsedText.Text = $"  {formattedElapsed}";
            }
        }

        public static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1.0)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:00}:{1:00}:{2:00}.{3:0}",
                    (int)duration.TotalHours,
                    duration.Minutes,
                    duration.Seconds,
                    duration.Milliseconds / 100);
            }

            if (duration.TotalMinutes >= 1.0)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:00}:{1:00}.{2:0}",
                    duration.Minutes,
                    duration.Seconds,
                    duration.Milliseconds / 100);
            }

            return string.Format(CultureInfo.InvariantCulture, "{0:0.0}s", duration.TotalSeconds);
        }

        private static string FormatShortEta(TimeSpan eta)
        {
            if (eta.TotalMinutes >= 1.0)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0}m{1:00}s", (int)eta.TotalMinutes, eta.Seconds);
            }

            return string.Format(CultureInfo.InvariantCulture, "{0:0}s", Math.Max(1.0, eta.TotalSeconds));
        }
    }
}
