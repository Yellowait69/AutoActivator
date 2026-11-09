using System;
using System.DirectoryServices.AccountManagement;
using System.Threading;
using System.Windows;
using AutoActivator.Config;
using AutoActivator.Services;

namespace AutoActivator.Gui
{
    public partial class LoginWindow : Window
    {
        public LoginWindow()
        {
            InitializeComponent();

            TxtUsername.Text = GetWindowsUsername();

            if (!string.IsNullOrWhiteSpace(TxtUsername.Text))
            {
                TxtPassword.Focus();
            }
            else
            {
                TxtUsername.Focus();
            }
        }

        /// <summary>
        /// Attempts to automatically retrieve the user's Windows ID
        /// using the Active Directory / local Windows account.
        /// </summary>
        private string GetWindowsUsername()
        {
            try
            {
                string envUser = Environment.UserName;

                if (!string.IsNullOrWhiteSpace(envUser))
                {
                    return envUser.ToUpperInvariant();
                }

                return UserPrincipal.Current?.Name?.ToUpperInvariant()
                       ?? (Settings.DbConfig.Uid ?? "");
            }
            catch
            {
                return Settings.DbConfig.Uid ?? "";
            }
        }

        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            string username = TxtUsername.Text.Trim();
            string password = TxtPassword.Password;

            if (string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(password))
            {
                MessageBox.Show(
                    "Please enter a username and password.",
                    "Input Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );

                return;
            }

            BtnLogin.IsEnabled = false;
            BtnLogin.Content = "Connecting...";

            Settings.DbConfig.Uid = username;
            Settings.DbConfig.Pwd = password;

            try
            {
                var mfService = new MicroFocusApiService();

                const string envToTest = "D";

                bool isLogged = await mfService.LogonAsync(
                    username,
                    password,
                    envToTest,
                    progressMsg =>
                        Console.WriteLine($"[Login] {progressMsg}"),
                    CancellationToken.None
                );

                if (isLogged)
                {
                    // IMPORTANT:
                    // LoginWindow a été ouvert avec ShowDialog()
                    // dans App.xaml.cs.
                    //
                    // On indique donc simplement que le login
                    // s'est terminé avec succès.
                    //
                    // App.xaml.cs créera ensuite MainWindow.
                    DialogResult = true;

                    return;
                }

                // LogonAsync a terminé normalement mais indique
                // que la connexion n'a pas réussi.
                MessageBox.Show(
                    "Unable to log in to the Micro Focus server.",
                    "Login Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );

                TxtPassword.Clear();
                TxtPassword.Focus();

                Settings.DbConfig.Pwd = "";
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show(
                    "Incorrect username or password.\n" +
                    "Access denied by the Micro Focus server.",
                    "Authentication Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );

                TxtPassword.Clear();
                TxtPassword.Focus();

                Settings.DbConfig.Pwd = "";
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Unable to connect to the Micro Focus server:\n" +
                    $"{ex.Message}\n\n" +
                    "Please verify that your VPN is connected.",
                    "Network Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );

                TxtPassword.Clear();
                TxtPassword.Focus();

                Settings.DbConfig.Pwd = "";
            }
            finally
            {
                BtnLogin.IsEnabled = true;
                BtnLogin.Content = "Sign In to Mainframe";
            }
        }
    }
}