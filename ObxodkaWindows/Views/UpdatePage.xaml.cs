using ObxodkaWindows.Models;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;

namespace ObxodkaWindows
{
    public partial class UpdatePage : Page
    {
        private string _downloadUrl;

        public UpdatePage(UpdateInfo info)
        {
            InitializeComponent();

            _downloadUrl = info.Url ?? "";
            VersionLabel.Text = $"v{info.Version}";
            ChangelogText.Text = info.Changelog;

            this.Loaded += (s, e) => PlayPageAnimation();
        }

        private void PlayPageAnimation()
        {
            if (this.Content is FrameworkElement content)
            {
                content.Opacity = 0;
                var transform = new TranslateTransform(0, 30);
                content.RenderTransform = transform;

                var fade = new DoubleAnimation(1, TimeSpan.FromMilliseconds(500))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                var slide = new DoubleAnimation(0, TimeSpan.FromMilliseconds(500))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                content.BeginAnimation(UIElement.OpacityProperty, fade);
                transform.BeginAnimation(TranslateTransform.YProperty, slide);
            }
        }

        private async void OnDownloadUpdateClicked(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_downloadUrl)) return;

            try
            {
                ((Button)sender).IsEnabled = false;
                ((Button)sender).Content = "ЗАГРУЗКА...";

                string tempFile = Path.Combine(Path.GetTempPath(), "ObxodkaInstaller.exe");

                using (var client = new HttpClient())
                {
                    var response = await client.GetAsync(_downloadUrl);
                    response.EnsureSuccessStatusCode();

                    using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await response.Content.CopyToAsync(fs);
                    }
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = tempFile,
                    UseShellExecute = true,
                    Verb = "runas"
                });

                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при скачивании обновления: {ex.Message}");
                ((Button)sender).IsEnabled = true;
                ((Button)sender).Content = "ОБНОВИТЬ СЕЙЧАС";
            }
        }

        private void OnSkipClicked(object sender, RoutedEventArgs e)
        {
            if (this.NavigationService.CanGoBack)
                this.NavigationService.GoBack();
            else
                this.NavigationService.Navigate(new LoginPage());
        }
    }
}