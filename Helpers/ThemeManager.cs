namespace obxodka.Helpers;
public enum AppTheme
{
    Dark,
    Light,
    Glass,
    Fluent
}
internal static class ThemeManager
{
    public static void SetTheme(AppTheme theme)
    {
        ResourceDictionary newThemeDictionary = theme switch
        {
            AppTheme.Dark => new Resources.Styles.Themes.DarkTheme(),
            AppTheme.Light => new Resources.Styles.Themes.LightTheme(),
            AppTheme.Glass => new Resources.Styles.Themes.GlassTheme(),
            _ => new Resources.Styles.Themes.DarkTheme()
        };
        var dictionaries = Application.Current?.Resources?.MergedDictionaries;
        if (dictionaries != null)
        {
            var existingTheme = dictionaries.FirstOrDefault(d => d.GetType().Name.Contains("Theme"));
            dictionaries.Add(newThemeDictionary);
            if (existingTheme != null)
            {
                dictionaries.Remove(existingTheme);
            }
        }
        Preferences.Default.Set("SelectedTheme", (int)theme);
    }
    public static void LoadSavedTheme()
    {
        int savedThemeId = Preferences.Default.Get("SelectedTheme", (int)AppTheme.Dark);
        SetTheme((AppTheme)savedThemeId);
    }
}