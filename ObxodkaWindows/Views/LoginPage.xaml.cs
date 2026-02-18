using ObxodkaWindows.Models;
using ObxodkaWindows.Core;
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;

namespace ObxodkaWindows
{
    public partial class LoginPage : Page
    {
        private const string ServerUrl = "YOUR_SERVER_API_URL";
        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private bool isPasswordVisible = false;

        public LoginPage()
        {
            InitializeComponent();
            this.Loaded += OnLoaded;

            // Загружаем сохраненную сессию (OctoCore Security: только логин и пароль)
            var session = AuthManager.LoadSession();
            if (!string.IsNullOrEmpty(session.Email))
                EmailEntry.Text = session.Email;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            PlayPageAnimation();
            AnimateFade(TitleLabel, 0, 1, 500);
            AnimateFade(FormContainer, 0, 1, 500);
            AnimateFade(LoginButtonBorder, 0, 1, 500);
            AnimateFade(RegisterLabel, 0, 1, 500);
        }

        private void OnPasswordChanged(object sender, RoutedEventArgs e) =>
            PassPlaceholder.Visibility = string.IsNullOrEmpty(PasswordEntry.Password) ? Visibility.Visible : Visibility.Collapsed;

        private void OnTextChanged(object sender, TextChangedEventArgs e) =>
            PassPlaceholder.Visibility = string.IsNullOrEmpty(PasswordTextEntry.Text) ? Visibility.Visible : Visibility.Collapsed;

        private void OnPasswordEyeClicked(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.Template.FindName("EyeImg", button) is Image eyeImg)) return;
            isPasswordVisible = !isPasswordVisible;
            ToggleField(isPasswordVisible, eyeImg);
        }

        private void ToggleField(bool visible, Image eyeImg)
        {
            if (visible)
            {
                PasswordTextEntry.Text = PasswordEntry.Password;
                PasswordEntry.Visibility = Visibility.Collapsed;
                PasswordTextEntry.Visibility = Visibility.Visible;
                PasswordTextEntry.Focus();
                eyeImg.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/eye_icon.png"));
            }
            else
            {
                PasswordEntry.Password = PasswordTextEntry.Text;
                PasswordTextEntry.Visibility = Visibility.Collapsed;
                PasswordEntry.Visibility = Visibility.Visible;
                PasswordEntry.Focus();
                eyeImg.Source = new BitmapImage(new Uri("pack://application:,,,/Resources/eye_off.png"));
            }
        }

        private async void OnLoginClicked(object sender, RoutedEventArgs e)
        {
            string email = EmailEntry.Text.Trim();
            string password = isPasswordVisible ? PasswordTextEntry.Text : PasswordEntry.Password;

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                ShowError("Заполните все поля");
                return;
            }

            SetLoadingState(true);
            try
            {
                var url = $"{ServerUrl}/api/Auth/login?username={Uri.EscapeDataString(email)}&password={Uri.EscapeDataString(password)}";
                var response = await client.PostAsync(url, null);

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    var result = JsonSerializer.Deserialize<LoginResponse>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (result != null)
                    {
                        // СОХРАНЯЕМ ТОЛЬКО ДАННЫЕ ДЛЯ ВХОДА
                        // VpnLink не пишется в session.dat для безопасности данных пользователя
                        AuthManager.SaveSession(new UserSession
                        {
                            Email = email,
                            Password = password,
                            JwtToken = result.Token,
                            IsLoggedIn = true
                        });

                        // Переходим на главную. Ссылка будет подтянута в ОЗУ только при нажатии "Старт"
                        this.NavigationService.Navigate(new MainPage());
                    }
                }
                else ShowError("Неверный логин или пароль");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LOGIN ERROR] {ex.Message}");
                ShowError("Сервер недоступен");
            }
            finally { SetLoadingState(false); }
        }

        private void ShowError(string msg)
        {
            CommonErrorLabel.Text = msg;
            AnimateFade(CommonErrorLabel, 0, 1, 200);
            Shake(FormContainer);
        }

        private void Shake(UIElement element)
        {
            var shake = new DoubleAnimation(0, 10, TimeSpan.FromMilliseconds(50)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3) };
            FormShakeTransform?.BeginAnimation(TranslateTransform.XProperty, shake);
        }

        private void AnimateFade(UIElement element, double from, double to, int ms) =>
            element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)));

        private void PlayPageAnimation()
        {
            if (!(this.Content is FrameworkElement content)) return;
            content.Opacity = 0;
            var transform = new TranslateTransform(0, 50);
            content.RenderTransform = transform;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600)) { EasingFunction = ease });
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(50, 0, TimeSpan.FromMilliseconds(600)) { EasingFunction = ease });
        }

        private void SetLoadingState(bool isLoading)
        {
            MainLoginButton.IsEnabled = !isLoading;
            MainLoginButton.Content = isLoading ? "Входим..." : "Войти";
        }

        private void OnRegisterLabelTapped(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
            this.NavigationService.Navigate(new RegisterPage());
    }
}