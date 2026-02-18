using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Media.Imaging;

namespace ObxodkaWindows
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            try
            {
                this.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Resources/appicon.ico"));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка загрузки иконки: {ex.Message}");
            }

            this.Loaded += (s, e) => PlayWindowEntryAnimation();

            this.MouseLeftButtonDown += (s, e) =>
            {
                try
                {
                    if (e.LeftButton == MouseButtonState.Pressed) this.DragMove();
                }
                catch { }
            };
        }

        private void PlayWindowEntryAnimation()
        {
            DoubleAnimation fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500));
            this.BeginAnimation(Window.OpacityProperty, fadeIn);
        }

        private void MainFrame_Navigated(object sender, NavigationEventArgs e)
        {
            bool shouldShowBack = MainFrame.CanGoBack &&
                                 (e.Content is UserProfilePage ||
                          e.Content is PaymentPage ||
                          e.Content is ChangePasswordPage ||
                          e.Content is DeleteAccountPage);

            if (shouldShowBack)
            {
                BackButton.Visibility = Visibility.Visible;
                AnimateButtonOpacity(BackButton, 1);
            }
            else
            {
                AnimateButtonOpacity(BackButton, 0, () => BackButton.Visibility = Visibility.Collapsed);
            }
        }

        private void AnimateButtonOpacity(UIElement element, double toOpacity, Action? onComplete = null)
        {
            DoubleAnimation anim = new DoubleAnimation(toOpacity, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };

            if (onComplete != null) anim.Completed += (s, e) => onComplete();
            element.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        private void OnBackClicked(object sender, RoutedEventArgs e)
        {
            if (MainFrame.CanGoBack) MainFrame.GoBack();
        }

        private void OnCloseClicked(object sender, RoutedEventArgs e)
        {
            DoubleAnimation fadeOut = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(350),
                EasingFunction = new PowerEase { Power = 2, EasingMode = EasingMode.EaseIn }
            };

            fadeOut.Completed += (s, args) =>
            {
                this.Hide();
                this.BeginAnimation(Window.OpacityProperty, null);
                this.Opacity = 1;
            };

            this.BeginAnimation(Window.OpacityProperty, fadeOut);
        }
    }
}