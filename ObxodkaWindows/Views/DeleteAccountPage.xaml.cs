using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;
using ObxodkaWindows.Core;
using MessageBox = System.Windows.MessageBox;

namespace ObxodkaWindows
{
    public partial class DeleteAccountPage : Page
    {
        private const string ServerUrl = "YOUR_SERVER_API_URL";
        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        public DeleteAccountPage()
        {
            InitializeComponent();
            this.Loaded += (s, e) => PlayPageAnimation();
        }

        private async void OnConfirmDeleteClicked(object sender, RoutedEventArgs e)
        {
            var session = AuthManager.LoadSession();
            string email = session.Email ?? "";

            if (string.IsNullOrEmpty(email))
            {
                MessageBox.Show("Ошибка: сессия не найдена. Пожалуйста, перезайдите.");
                return;
            }

            var result = MessageBox.Show("Вы уверены? Аккаунт и оставшееся время будут удалены навсегда!",
                                       "Удаление аккаунта", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                var url = $"{ServerUrl}/api/Auth/delete-user?username={Uri.EscapeDataString(email)}";
                var response = await client.DeleteAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    AuthManager.ClearSession();

                    MessageBox.Show("Аккаунт успешно удален. Возвращаемся на страницу входа.", "OctoCore", MessageBoxButton.OK, MessageBoxImage.Information);

                    this.NavigationService.Navigate(new LoginPage());
                }
                else
                {
                    string error = await response.Content.ReadAsStringAsync();
                    MessageBox.Show($"Ошибка сервера: {error}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось связаться с сервером: {ex.Message}");
            }
        }

        private void PlayPageAnimation()
        {
            if (!(this.Content is FrameworkElement content)) return;

            content.Opacity = 0;
            var transform = new TranslateTransform(0, 40);
            content.RenderTransform = transform;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            content.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600)) { EasingFunction = ease });

            transform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(40, 0, TimeSpan.FromMilliseconds(600)) { EasingFunction = ease });
        }
    }
}