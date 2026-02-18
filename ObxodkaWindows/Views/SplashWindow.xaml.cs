using System.Windows;
using Application = System.Windows.Application;

namespace ObxodkaWindows
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
            StartLoading();
        }

        private async void StartLoading()
        {
            await Task.Delay(2000);

            if (Application.Current is App myApp)
            {
                myApp.LaunchMainWindow();
            }

            this.Close();
        }
    }
}