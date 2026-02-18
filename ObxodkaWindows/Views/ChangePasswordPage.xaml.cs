using ObxodkaWindows.Core;
using System;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Image = System.Windows.Controls.Image;

namespace ObxodkaWindows
{
    public partial class ChangePasswordPage : Page
    {
        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private const string ServerUrl = "YOUR_SERVER_API_URL";

        private bool isOldPassVisible = false;
        private bool isNewPassVisible = false;

        public ChangePasswordPage()
        {
            InitializeComponent();
            this.Loaded += (s, e) => PlayPageAnimation();
        }

        private async void OnSavePasswordClicked(object sender, RoutedEventArgs e)
        {
            // OctoCore Security: Загружаем сессию с новыми именами полей
            var session = AuthManager.LoadSession();
            string email = session.Email ?? "";

            string oldP = isOldPassVisible ? OldPasswordText.Text : OldPasswordBox.Password;
            string newP = isNewPassVisible ? NewPasswordText.Text : NewPasswordBox.Password;

            if (string.IsNullOrWhiteSpace(oldP) || string.IsNullOrWhiteSpace(newP))
            {
                MessageBox.Show("Заполните все поля ввода.");
                return;
            }

            if (newP.Length < 6)
            {
                MessageBox.Show("Новый пароль должен быть не менее 6 символов.");
                return;
            }

            try
            {
                // Формируем запрос к API
                var url = $"{ServerUrl}/api/Auth/change-password?username={Uri.EscapeDataString(email)}&oldPassword={Uri.EscapeDataString(oldP)}&newPassword={Uri.EscapeDataString(newP)}";
                var response = await client.PostAsync(url, null);

                if (response.IsSuccessStatusCode)
                {
                    MessageBox.Show("Пароль успешно обновлен!", "OctoCore", MessageBoxButton.OK, MessageBoxImage.Information);

                    session.Password = newP;
                    AuthManager.SaveSession(session);

                    if (this.NavigationService.CanGoBack)
                        this.NavigationService.GoBack();
                }
                else
                {
                    MessageBox.Show("Ошибка: проверьте правильность старого пароля.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Сервер недоступен: {ex.Message}");
            }
        }

        private void OnOldPasswordEyeClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Template.FindName("OldEyeImg", btn) is Image img)
            {
                isOldPassVisible = !isOldPassVisible;
                if (isOldPassVisible)
                {
                    OldPasswordText.Text = OldPasswordBox.Password;
                    OldPasswordBox.Visibility = Visibility.Collapsed;
                    OldPasswordText.Visibility = Visibility.Visible;
                    OldPasswordText.Focus();
                    img.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/eye_icon.png"));
                }
                else
                {
                    OldPasswordBox.Password = OldPasswordText.Text;
                    OldPasswordText.Visibility = Visibility.Collapsed;
                    OldPasswordBox.Visibility = Visibility.Visible;
                    OldPasswordBox.Focus();
                    img.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/eye_off.png"));
                }
            }
        }

        private void OnNewPasswordEyeClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Template.FindName("NewEyeImg", btn) is Image img)
            {
                isNewPassVisible = !isNewPassVisible;
                if (isNewPassVisible)
                {
                    NewPasswordText.Text = NewPasswordBox.Password;
                    NewPasswordBox.Visibility = Visibility.Collapsed;
                    NewPasswordText.Visibility = Visibility.Visible;
                    NewPasswordText.Focus();
                    img.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/eye_icon.png"));
                }
                else
                {
                    NewPasswordBox.Password = NewPasswordText.Text;
                    NewPasswordText.Visibility = Visibility.Collapsed;
                    NewPasswordBox.Visibility = Visibility.Visible;
                    NewPasswordBox.Focus();
                    img.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/eye_off.png"));
                }
            }
        }

        private void OnOldPasswordChanged(object sender, RoutedEventArgs e) => UpdateOldPlaceholder();
        private void OnOldTextChanged(object sender, TextChangedEventArgs e) => UpdateOldPlaceholder();
        private void UpdateOldPlaceholder() =>
            OldPassPlaceholder.Visibility = (string.IsNullOrEmpty(OldPasswordBox.Password) && string.IsNullOrEmpty(OldPasswordText.Text)) ? Visibility.Visible : Visibility.Collapsed;

        private void OnNewPasswordChanged(object sender, RoutedEventArgs e) => UpdateNewPlaceholder();
        private void OnNewTextChanged(object sender, TextChangedEventArgs e) => UpdateNewPlaceholder();
        private void UpdateNewPlaceholder() =>
            NewPassPlaceholder.Visibility = (string.IsNullOrEmpty(NewPasswordBox.Password) && string.IsNullOrEmpty(NewPasswordText.Text)) ? Visibility.Visible : Visibility.Collapsed;

        private void PlayPageAnimation()
        {
            if (!(this.Content is FrameworkElement content)) return;
            content.Opacity = 0;
            var transform = new TranslateTransform(0, 30);
            content.RenderTransform = transform;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600)) { EasingFunction = ease });
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(600)) { EasingFunction = ease });
        }
    }
}