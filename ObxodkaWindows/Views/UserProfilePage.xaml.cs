using ObxodkaWindows.Models;
using ObxodkaWindows.Core;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Point = System.Windows.Point;

namespace ObxodkaWindows
{
    public partial class UserProfilePage : Page
    {
        private long remainingSeconds = 0;

        private static readonly HttpClient _httpClient = new HttpClient
        {
            BaseAddress = new Uri("YOUR_SERVER_API_URL"),
            Timeout = TimeSpan.FromSeconds(15)
        };

        public UserProfilePage()
        {
            InitializeComponent();
            this.Loaded += OnPageLoaded;
        }

        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            PlayPageAnimation();
            await LoadUserData();
        }

        private async Task LoadUserData()
        {
            try
            {
                var session = AuthManager.LoadSession();
                string email = session.Email ?? "";
                string jwt = session.JwtToken ?? "";

                if (EmailLabel != null)
                    EmailLabel.Text = !string.IsNullOrEmpty(email) ? email : "Гость";

                if (string.IsNullOrEmpty(email)) return;

                if (!string.IsNullOrEmpty(jwt))
                    _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

                var response = await _httpClient.PostAsync($"/api/Vpn/ping?username={Uri.EscapeDataString(email)}", null);

                if (response.IsSuccessStatusCode)
                {
                    var data = await response.Content.ReadFromJsonAsync<VpnStatusResponse>();
                    if (data != null)
                    {
                        remainingSeconds = data.RemainingSeconds;
                        UpdateBalanceUI();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PROFILE LOAD ERROR] {ex.Message}");
            }
        }

        private void UpdateBalanceUI()
        {
            long tokens = remainingSeconds / 3600;
            long restSeconds = remainingSeconds % 3600;
            TimeSpan t = TimeSpan.FromSeconds(restSeconds);

            if (TokenAmountLabel != null)
                TokenAmountLabel.Text = string.Format("{0}T / {1:D2}:{2:D2}", tokens, t.Minutes, t.Seconds);
        }

        private async void OnLogoutClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                var session = AuthManager.LoadSession();
                string email = session.Email ?? "";
                if (!string.IsNullOrEmpty(email))
                {
                    await _httpClient.PostAsync($"/api/Vpn/stop?username={Uri.EscapeDataString(email)}", null);
                }
            }
            catch { }

            AuthManager.ClearSession();
            this.NavigationService.Navigate(new LoginPage());
        }

        private void OnBuyTokensClicked(object sender, RoutedEventArgs e)
        {
            this.NavigationService.Navigate(new PaymentPage());
        }

        private void OnChangePasswordClicked(object sender, RoutedEventArgs e)
        {
            this.NavigationService.Navigate(new ChangePasswordPage());
        }

        private void OnDeleteAccountClicked(object sender, RoutedEventArgs e)
        {
            this.NavigationService.Navigate(new DeleteAccountPage());
        }

        private void PlayPageAnimation()
        {
            double startY = 40;
            TimeSpan duration = TimeSpan.FromMilliseconds(700);
            IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            void AnimateElement(UIElement element, TranslateTransform transform, int delayMs)
            {
                if (element == null || transform == null) return;

                element.BeginAnimation(UIElement.OpacityProperty, null);
                transform.BeginAnimation(TranslateTransform.YProperty, null);

                element.Opacity = 0;
                transform.Y = startY;

                var fade = new DoubleAnimation(1, duration)
                {
                    BeginTime = TimeSpan.FromMilliseconds(delayMs),
                    EasingFunction = ease
                };
                var slide = new DoubleAnimation(0, duration)
                {
                    BeginTime = TimeSpan.FromMilliseconds(delayMs),
                    EasingFunction = ease
                };

                element.BeginAnimation(UIElement.OpacityProperty, fade);
                transform.BeginAnimation(TranslateTransform.YProperty, slide);
            }

            AnimateElement(HeaderTitle, HeaderTrans, 0);
            AnimateElement(BalanceCard, BalanceTrans, 100);
            AnimateElement(AccountCard, AccountTrans, 200);
            AnimateElement(BottomButtons, ButtonsTrans, 300);

            DoubleAnimation pulse = new DoubleAnimation(1.0, 1.05, TimeSpan.FromSeconds(2))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            ScaleTransform pulseScale = new ScaleTransform();
            TokenAmountLabel.RenderTransform = pulseScale;
            TokenAmountLabel.RenderTransformOrigin = new Point(0, 0.5);
            pulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            pulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }
    }
}