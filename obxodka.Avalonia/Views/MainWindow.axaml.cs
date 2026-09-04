using Avalonia.Controls;

namespace obxodka.Avalonia.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Находим кнопку по имени и вешаем на нее простое действие
        var testButton = this.FindControl<Button>("TestButton");
        if (testButton != null)
        {
            testButton.Click += (sender, args) =>
            {
                testButton.Content = "Работает!";
            };
        }
    }
}
