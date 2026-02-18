using ObxodkaWindows.Models;
using ObxodkaWindows.Core;
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
using TextBox = System.Windows.Controls.TextBox;

namespace ObxodkaWindows
{
    public partial class RegisterPage : Page
    {
        private const string ServerUrl = "YOUR_SERVER_API_URL";
        private static readonly HttpClient client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private bool isPasswordVisible = false, isRepeatPasswordVisible = false;

        public RegisterPage()
        {
            InitializeComponent();
            this.Loaded += (s, e) => {
                PlayPageAnimation();
                AnimateEntrance(TitleLabel, TitleTransform, 0);
                AnimateEntrance(FormContainer, FormTransform, 80);
                AnimateEntrance(RegisterButtonBorder, ButtonTransform, 160);
            };
        }

        private void OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox p)
                (p.Name == "PasswordEntry" ? PassPlaceholder : PassRepeatPlaceholder).Visibility =
                    string.IsNullOrEmpty(p.Password) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox t)
                (t.Name == "PasswordTextEntry" ? PassPlaceholder : PassRepeatPlaceholder).Visibility =
                    string.IsNullOrEmpty(t.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void TogglePasswordVisibility(object sender, RoutedEventArgs e) =>
            ToggleEye(sender as Button, ref isPasswordVisible, PasswordEntry, PasswordTextEntry, "EyeImg1");

        private void ToggleRepeatPasswordVisibility(object sender, RoutedEventArgs e) =>
            ToggleEye(sender as Button, ref isRepeatPasswordVisible, PasswordRepeatEntry, PasswordRepeatTextEntry, "EyeImg2");

        private void ToggleEye(Button? btn, ref bool isVisible, PasswordBox p, TextBox t, string imgName)
        {
            if (btn == null) return;
            isVisible = !isVisible;
            var img = btn.Template.FindName(imgName, btn) as Image;

            if (isVisible) { t.Text = p.Password; p.Visibility = Visibility.Collapsed; t.Visibility = Visibility.Visible; t.Focus(); }
            else { p.Password = t.Text; t.Visibility = Visibility.Collapsed; p.Visibility = Visibility.Visible; p.Focus(); }

            if (img != null) img.Source = new BitmapImage(new Uri($"pack://application:,,,/Resources/eye_{(isVisible ? "icon" : "off")}.png"));
        }

        private void OnPolicyLinkClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try
            {
                string url = "http://obxodka.one/Home/Privacy";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex) { Debug.WriteLine(ex.Message); }
        }

        private async void OnRegisterClicked(object sender, RoutedEventArgs e)
        {
            if (PolicyCheckBox.IsChecked != true)
            {
                ShowError("Примите условия политики");
                return;
            }

            string email = EmailEntry.Text.Trim();
            string pass = isPasswordVisible ? PasswordTextEntry.Text : PasswordEntry.Password;
            string confirm = isRepeatPasswordVisible ? PasswordRepeatTextEntry.Text : PasswordRepeatEntry.Password;

            if (string.IsNullOrEmpty(email) || pass.Length < 6 || pass != confirm)
            {
                ShowError("Проверьте данные и пароли (мин. 6 симв.)");
                return;
            }

            SetLoadingState(true);
            try
            {
                var regUrl = $"{ServerUrl}/api/Auth/register?username={Uri.EscapeDataString(email)}&password={Uri.EscapeDataString(pass)}";
                var regResponse = await client.PostAsync(regUrl, null);

                if (regResponse.IsSuccessStatusCode)
                {
                    var loginUrl = $"{ServerUrl}/api/Auth/login?username={Uri.EscapeDataString(email)}&password={Uri.EscapeDataString(pass)}";
                    var loginResponse = await client.PostAsync(loginUrl, null);

                    if (loginResponse.IsSuccessStatusCode)
                    {
                        var body = await loginResponse.Content.ReadAsStringAsync();
                        var result = JsonSerializer.Deserialize<LoginResponse>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (result != null)
                        {
                            AuthManager.SaveSession(new UserSession
                            {
                                Email = email,
                                Password = pass,
                                JwtToken = result.Token,
                                IsLoggedIn = true
                            });

                            this.NavigationService.Navigate(new MainPage());
                        }
                    }
                    else
                    {
                        this.NavigationService.Navigate(new LoginPage());
                    }
                }
                else
                {
                    ShowError("Email занят или ошибка сервера");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                ShowError("Ошибка связи с сервером");
            }
            finally
            {
                SetLoadingState(false);
            }
        }

        private void SetLoadingState(bool loading)
        {
            MainRegisterButton.IsEnabled = !loading;
            MainRegisterButton.Content = loading ? "Создание..." : "Создать аккаунт";
        }

        private void PlayPageAnimation()
        {
            if (!(this.Content is FrameworkElement c)) return;
            c.Opacity = 0;
            var t = new TranslateTransform(0, 50); c.RenderTransform = t;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            c.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600)) { EasingFunction = ease });
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(50, 0, TimeSpan.FromMilliseconds(600)) { EasingFunction = ease });
        }

        private async void AnimateEntrance(UIElement el, TranslateTransform tr, int delay)
        {
            await Task.Delay(delay);
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(400)));
            tr.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)) { EasingFunction = new CubicEase() });
        }

        private void ShowError(string m)
        {
            CommonErrorLabel.Text = m;
            CommonErrorLabel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
            var shake = new DoubleAnimation(0, 10, TimeSpan.FromMilliseconds(50)) { AutoReverse = true, RepeatBehavior = new RepeatBehavior(3) };
            FormTransform.BeginAnimation(TranslateTransform.XProperty, shake);
        }

        private void OnLoginLabelTapped(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
            this.NavigationService.Navigate(new LoginPage());
    }
}