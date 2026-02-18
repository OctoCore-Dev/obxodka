using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using ObxodkaWindows.Core;
using Microsoft.Web.WebView2.Core;
using MessageBox = System.Windows.MessageBox;

namespace ObxodkaWindows
{
    public partial class PaymentPage : Page
    {
        public PaymentPage()
        {
            InitializeComponent();
            InitializeAsync();
        }

        private async void InitializeAsync()
        {
            try
            {
                // Используем общую папку приложения для данных браузера
                string userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Obxodka", "WebView2Data");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);

                await PayWebView.EnsureCoreWebView2Async(env);

                PayWebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                PayWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                PayWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 15, 15, 16);

                PayWebView.CoreWebView2.CookieManager.DeleteAllCookies();

                var session = AuthManager.LoadSession();

                string baseUrl = "YOUR_SERVER_API_URL";

                string autoLoginUrl = $"{baseUrl}/Account/AutoLoginAndPay?" +
                                     $"username={Uri.EscapeDataString(session.Email ?? "")}&" +
                                     $"key={Uri.EscapeDataString(session.Password ?? "")}";

                PayWebView.Source = new Uri(autoLoginUrl);
                PayWebView.NavigationCompleted += OnNavigationCompleted;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка инициализации кассы: {ex.Message}");
            }
        }

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                string hideScrollbarCss = @"
                    var style = document.createElement('style');
                    style.innerHTML = `
                        ::-webkit-scrollbar { display: none !important; width: 0px !important; }
                        body { 
                            -ms-overflow-style: none !important; 
                            scrollbar-width: none !important; 
                            overflow-y: auto !important; 
                            overflow-x: hidden !important; 
                        }
                    `;
                    document.head.appendChild(style);";

                await PayWebView.ExecuteScriptAsync(hideScrollbarCss);

                // Проверка на успех оплаты по URL
                if (PayWebView.Source.ToString().Contains("Success"))
                {
                    MessageBox.Show("Оплата завершена успешно!", "OctoCore Pay", MessageBoxButton.OK, MessageBoxImage.Information);
                    NavigationService?.GoBack();
                }
            }

            // Плавное скрытие экрана загрузки
            if (LoadingOverlay != null && LoadingOverlay.Visibility == Visibility.Visible)
            {
                DoubleAnimation fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(500));
                fadeOut.Completed += (s, args) => LoadingOverlay.Visibility = Visibility.Collapsed;
                LoadingOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
        }
    }
}