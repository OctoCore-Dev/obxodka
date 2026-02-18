using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ObxodkaWindows.Core;
using ObxodkaWindows.Models;
using System.Text.Json;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using MessageBox = System.Windows.MessageBox;

namespace ObxodkaWindows
{
    public partial class MainPage : Page
    {
        private bool isConnected = false;
        private bool isBusy = false;
        private long remainingSeconds = 0;
        private CancellationTokenSource? vpnCts;

        private static readonly HttpClient _httpClient = new HttpClient
        {
            BaseAddress = new Uri("YOUR_SERVER_API_URL"),
            Timeout = TimeSpan.FromSeconds(15)
        };

        private VpnService _vpnService = new VpnService();

        private readonly Color VividRed = (Color)ColorConverter.ConvertFromString("#FF0044");
        private readonly Color DarkRed = (Color)ColorConverter.ConvertFromString("#660011");
        private readonly Color DeepOcean = (Color)ColorConverter.ConvertFromString("#003366");
        private readonly Color StatusBlue = Color.FromRgb(0, 170, 255);

        public MainPage()
        {
            InitializeComponent();
            this.Loaded += async (s, e) =>
            {
                PlayPageAnimation();
                await SyncPingWithServer();
            };
        }

        private void PlayPageAnimation()
        {
            if (this.Content is FrameworkElement content)
            {
                content.Opacity = 0;
                var transform = new TranslateTransform(0, 50);
                content.RenderTransform = transform;
                content.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);

                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                var slide = new DoubleAnimation(50, 0, TimeSpan.FromMilliseconds(600)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

                content.BeginAnimation(UIElement.OpacityProperty, fade);
                transform.BeginAnimation(TranslateTransform.YProperty, slide);
            }
        }

        private async Task<bool> SyncPingWithServer()
        {
            try
            {
                var session = AuthManager.LoadSession();
                string email = session.Email ?? "";
                string jwt = session.JwtToken ?? "";

                if (string.IsNullOrEmpty(email)) return false;

                if (!string.IsNullOrEmpty(jwt))
                    _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

                var response = await _httpClient.PostAsync($"/api/Vpn/ping?username={Uri.EscapeDataString(email)}", null);

                if (response.IsSuccessStatusCode)
                {
                    var data = await response.Content.ReadFromJsonAsync<VpnStatusResponse>();
                    if (data != null)
                    {
                        remainingSeconds = data.RemainingSeconds;
                        Dispatcher.Invoke(() => UpdateBalanceUI());
                        return data.IsActive;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[API PING ERROR] {ex.Message}");
            }
            return false;
        }

        private async Task SendStopToServer()
        {
            try
            {
                var session = AuthManager.LoadSession();
                string email = session.Email ?? "";
                await _httpClient.PostAsync($"/api/Vpn/stop?username={Uri.EscapeDataString(email)}", null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[API STOP ERROR] {ex.Message}");
            }
        }

        public async void OnConnectClicked(object sender, RoutedEventArgs e)
        {
            if (isBusy) return;
            isBusy = true;

            try
            {
                AnimateScale(ConnectButtonBorder, 0.96, 100);
                await Task.Delay(100);
                AnimateScale(ConnectButtonBorder, 1.0, 100);

                if (!isConnected)
                {
                    StatusLabel.Text = "ПОДКЛЮЧЕНИЕ...";

                    // 1. Загружаем Email и Password из защищенной сессии
                    var session = AuthManager.LoadSession();
                    if (string.IsNullOrEmpty(session.Email) || string.IsNullOrEmpty(session.Password))
                    {
                        MessageBox.Show("Сессия истекла. Пожалуйста, войдите снова.");
                        this.NavigationService.Navigate(new LoginPage());
                        return;
                    }

                    // 2. Авторизация на лету для получения VpnLink в ОЗУ
                    var loginUrl = $"/api/Auth/login?username={Uri.EscapeDataString(session.Email)}&password={Uri.EscapeDataString(session.Password)}";
                    var loginResponse = await _httpClient.PostAsync(loginUrl, null);

                    if (!loginResponse.IsSuccessStatusCode)
                    {
                        MessageBox.Show("Ошибка проверки аккаунта. Проверьте подписку.");
                        return;
                    }

                    var body = await loginResponse.Content.ReadAsStringAsync();
                    var loginResult = JsonSerializer.Deserialize<LoginResponse>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (loginResult == null || string.IsNullOrEmpty(loginResult.VpnLink))
                    {
                        MessageBox.Show("Ключ подключения не получен. Обратитесь в поддержку.");
                        return;
                    }

                    // 3. Синхронизация баланса
                    await SyncPingWithServer();
                    if (remainingSeconds <= 0)
                    {
                        MessageBox.Show("Баланс времени исчерпан. Пожалуйста, пополните счет.");
                        return;
                    }

                    // 4. Запуск движка через StandardInput (ОЗУ)
                    await _vpnService.StartVpn(loginResult.VpnLink);

                    isConnected = true;
                    vpnCts = new CancellationTokenSource();
                    UpdateUiState(true);

                    _ = ConsumeTimeLoop(vpnCts.Token);
                }
                else
                {
                    await StopVpnInternal();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка OctoCore Engine: {ex.Message}");
                UpdateUiState(false);
            }
            finally { isBusy = false; }
        }

        private async Task StopVpnInternal()
        {
            _vpnService.StopVpn();
            await SendStopToServer();

            isConnected = false;
            vpnCts?.Cancel();
            UpdateUiState(false);
        }

        private async Task ConsumeTimeLoop(CancellationToken ct)
        {
            int pingCounter = 0;

            while (!ct.IsCancellationRequested && remainingSeconds > 0)
            {
                try
                {
                    await Task.Delay(1000, ct);
                    remainingSeconds--;
                    pingCounter++;

                    if (pingCounter >= 10)
                    {
                        bool isActiveOnServer = await SyncPingWithServer();
                        pingCounter = 0;

                        if (!isActiveOnServer || remainingSeconds <= 0)
                        {
                            await Dispatcher.InvokeAsync(async () =>
                            {
                                await StopVpnInternal();
                                MessageBox.Show("Подключение остановлено: время вышло или подписка неактивна.");
                            });
                            break;
                        }
                    }

                    Dispatcher.Invoke(() => UpdateBalanceUI());
                }
                catch { break; }
            }

            if (remainingSeconds <= 0 && isConnected)
            {
                await Dispatcher.InvokeAsync(async () => await StopVpnInternal());
            }
        }

        private void UpdateBalanceUI()
        {
            long tokens = remainingSeconds / 3600;
            long restSeconds = remainingSeconds % 3600;
            TimeSpan t = TimeSpan.FromSeconds(restSeconds);

            TokenAmountLabel.Text = string.Format("{0}T / {1:D2}:{2:D2}",
                tokens,
                t.Minutes,
                t.Seconds);
        }

        private void UpdateUiState(bool connected)
        {
            TimeSpan duration = TimeSpan.FromMilliseconds(800);
            IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            if (connected)
            {
                StatusLabel.Text = "ЗАЩИЩЕНО";
                ConnectButton.Content = "СТОП";
                AnimateTextColor(StatusLabel, StatusBlue, duration, ease);
                AnimateGradientColor(GradStart, DeepOcean, duration, ease);
                AnimateGradientColor(GradEnd, VividRed, duration, ease);
            }
            else
            {
                StatusLabel.Text = "ОТКЛЮЧЕНО";
                ConnectButton.Content = "ПУСК";
                AnimateTextColor(StatusLabel, VividRed, duration, ease);
                AnimateGradientColor(GradStart, VividRed, duration, ease);
                AnimateGradientColor(GradEnd, DarkRed, duration, ease);
            }
        }

        private void AnimateScale(UIElement element, double to, int ms)
        {
            var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
            if (!(element.RenderTransform is ScaleTransform trans))
            {
                trans = new ScaleTransform();
                element.RenderTransform = trans;
            }
            element.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            trans.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            trans.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        private void AnimateTextColor(TextBlock target, Color to, TimeSpan duration, IEasingFunction ease)
        {
            if (!(target.Foreground is SolidColorBrush))
                target.Foreground = new SolidColorBrush(((SolidColorBrush)target.Foreground).Color);
            var anim = new ColorAnimation(to, duration) { EasingFunction = ease };
            target.Foreground.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }

        private void AnimateGradientColor(GradientStop target, Color to, TimeSpan duration, IEasingFunction ease)
        {
            var anim = new ColorAnimation(to, duration) { EasingFunction = ease };
            target.BeginAnimation(GradientStop.ColorProperty, anim);
        }

        private void OnAccountHeaderTapped(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            AnimateScale(ProfileCard, 0.95, 50);
            Task.Delay(50).ContinueWith(_ => Dispatcher.Invoke(() =>
            {
                AnimateScale(ProfileCard, 1.0, 50);
                this.NavigationService.Navigate(new UserProfilePage());
            }));
        }

        private void OnBuyTokensClicked(object sender, RoutedEventArgs e) =>
            this.NavigationService.Navigate(new UserProfilePage());

        private void ConnectBtn_Click(object sender, RoutedEventArgs e)
        {
            OnConnectClicked(sender, e);
        }
    }
}