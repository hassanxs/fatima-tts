
namespace FatimaTTS.Services;

public class ThemeService
{
    private readonly SettingsService _settings;

    public ThemeService(SettingsService settings)
    {
        _settings = settings;
    }

    public void Apply(string theme)
    {
        var dicts = Application.Current.Resources.MergedDictionaries;

        // Remove only theme color dictionaries — never Styles.xaml
        var existing = dicts
            .Where(d => d.Source is not null &&
                        (d.Source.OriginalString.Contains("LightTheme") ||
                         d.Source.OriginalString.Contains("DarkTheme")))
            .ToList();
        foreach (var d in existing) dicts.Remove(d);

        // Ensure Styles.xaml is still present (safety check)
        bool hasStyles = dicts.Any(d =>
            d.Source?.OriginalString.Contains("Styles.xaml") == true);
        if (!hasStyles)
            dicts.Insert(0, new ResourceDictionary
            {
                Source = new Uri("Themes/Styles.xaml", UriKind.Relative)
            });

        // Add chosen theme last so its colors override Styles.xaml defaults
        var uri = theme == "Light"
            ? new Uri("Themes/LightTheme.xaml", UriKind.Relative)
            : new Uri("Themes/DarkTheme.xaml",  UriKind.Relative);

        dicts.Add(new ResourceDictionary { Source = uri });
    }

    public void ApplyAndSave(string theme)
    {
        Apply(theme);
        var settings = _settings.Load();
        settings.Theme = theme;
        _settings.Save(settings);
    }

    public string LoadSavedTheme()
    {
        return _settings.Load().Theme;
    }
}
