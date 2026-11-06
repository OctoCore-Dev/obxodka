namespace obxodka.Client.Models;

//элемент списка установленных приложений для раздельного туннелирования
public class AppInfoItem : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _packageName = string.Empty;
    private bool _isBypassed;
    private string? _iconPath;

    public event PropertyChangedEventHandler? PropertyChanged;

    //название приложения
    public string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged();
            }
        }
    }

    //имя пакета или путь к исполняемому файлу
    public string PackageName
    {
        get => _packageName;
        set
        {
            if (_packageName != value)
            {
                _packageName = value;
                OnPropertyChanged();
            }
        }
    }

    //флаг обхода vpn для данного приложения
    public bool IsBypassed
    {
        get => _isBypassed;
        set
        {
            if (_isBypassed != value)
            {
                _isBypassed = value;
                OnPropertyChanged();
            }
        }
    }

    //путь к иконке приложения
    public string? IconPath
    {
        get => _iconPath;
        set
        {
            if (_iconPath != value)
            {
                _iconPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IconSource));
                OnPropertyChanged(nameof(HasIcon));
                OnPropertyChanged(nameof(HasNoIcon));
            }
        }
    }

    //источник иконки для биндинга в ui
    public string? IconSource => _iconPath is { Length: > 0 } ? _iconPath : null;

    //наличие иконки
    public bool HasIcon => !string.IsNullOrEmpty(_iconPath);

    //отсутствие иконки
    public bool HasNoIcon => string.IsNullOrEmpty(_iconPath);

    //первая буква для аватарки приложения
    public string InitialLetter => !string.IsNullOrWhiteSpace(_name) ? _name.TrimStart()[0].ToString().ToUpperInvariant() : "?";

    public AppInfoItem() { }

    public AppInfoItem(string name, string packageName, bool isBypassed = false, string? iconPath = null)
    {
        _name = name;
        _packageName = packageName;
        _isBypassed = isBypassed;
        _iconPath = iconPath;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
