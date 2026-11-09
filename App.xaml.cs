using System;
using System.Windows;
using AutoActivator.Gui;

namespace AutoActivator
{
    public partial class App : Application
    {
        protected override void OnStartup(
            StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                // Empêche l'application de s'arrêter lorsque
                // LoginWindow se ferme après la connexion.
                ShutdownMode =
                    ShutdownMode.OnExplicitShutdown;

                var loginWindow =
                    new LoginWindow();

                bool? loginResult =
                    loginWindow.ShowDialog();

                if (loginResult != true)
                {
                    Shutdown();
                    return;
                }

                var mainWindow =
                    new MainWindow();

                MainWindow = mainWindow;

                // L'application s'arrêtera maintenant uniquement
                // lorsque MainWindow sera fermée.
                ShutdownMode =
                    ShutdownMode.OnMainWindowClose;

                mainWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.ToString(),
                    "Erreur au démarrage",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );

                Shutdown(-1);
            }
        }
    }
}