using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AutoActivator.Config;
using AutoActivator.Gui;

namespace AutoActivator
{
    public partial class App : Application
    {
        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            try
            {
                if (e.Args != null && e.Args.Any(arg =>
                        string.Equals(arg, "--cli", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "-cli", StringComparison.OrdinalIgnoreCase)))
                {
                    await Program.RunCliAsync(e.Args);
                    Shutdown(0);
                    return;
                }

                // Empêche l'application de s'arrêter lorsque
                // LoginWindow se ferme après la connexion.
                ShutdownMode = ShutdownMode.OnExplicitShutdown;

                var loginWindow = new LoginWindow();
                bool? loginResult = loginWindow.ShowDialog();

                if (loginResult != true)
                {
                    Shutdown();
                    return;
                }

                var mainWindow = new MainWindow();
                MainWindow = mainWindow;

                // L'application s'arrêtera maintenant uniquement
                // lorsque MainWindow sera fermée.
                ShutdownMode = ShutdownMode.OnMainWindowClose;

                mainWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.ToString(),
                    "Erreur au démarrage",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                Shutdown(-1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Efface les identifiants sensibles conservés en mémoire vive à la fermeture.
            Settings.DbConfig.Pwd = string.Empty;
            base.OnExit(e);
        }

        private static void OnDispatcherUnhandledException(
            object sender,
            DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                $"Une erreur inattendue est survenue :\n\n{e.Exception.Message}",
                "Erreur Applicative",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        }

        private static void OnUnobservedTaskException(
            object? sender,
            UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved();
        }
    }
}
