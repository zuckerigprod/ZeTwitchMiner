using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Data;
using Avalonia.Data.Core;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.MarkupExtensions.CompiledBindings;

namespace ZeTwitchMiner.Core;

public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();
    public static readonly string[] Languages = ["ru", "en"];

    private Dictionary<string, string> _strings = new();
    private Dictionary<string, string> _fallback = new();

    public string Language { get; private set; } = "en";
    public event Action? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;

    // Индексатор для привязок из разметки: Loc[key]
    public string this[string key] => Get(key);

    public static string T(string key) => Instance.Get(key);
    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Instance.Get(key), args);

    public string Get(string key) =>
        _strings.TryGetValue(key, out var s) ? s : _fallback.TryGetValue(key, out var f) ? f : key;

    public static string DetectLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "ru" or "uk" or "be" or "kk" ? "ru" : "en";

    public void SetLanguage(string lang)
    {
        if (!Languages.Contains(lang)) lang = "en";
        _fallback = Read("en");
        _strings = lang == "en" ? _fallback : Read(lang);
        Language = lang;

        var culture = CultureInfo.GetCultureInfo(lang == "ru" ? "ru-RU" : "en-US");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke();
    }

    private static Dictionary<string, string> Read(string lang)
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"Lang.{lang}.json");
        if (stream is null) return new();
        return JsonSerializer.Deserialize(stream, AppJson.Default.DictionaryStringString) ?? new();
    }
}

// {l:Tr Key} в разметке. Скомпилированная привязка без рефлексии: Avalonia слушает
// PropertyChanged через слабые ссылки, поэтому закрытые окна не держатся в памяти.
public sealed class TrExtension(string key)
{
    public string Key { get; set; } = key;

    public IBinding ProvideValue(IServiceProvider _)
    {
        var key = Key;
        var property = new ClrPropertyInfo("Item[]", o => ((Loc)o).Get(key), null, typeof(string));
        var path = new CompiledBindingPathBuilder()
            .Property(property, PropertyInfoAccessorFactory.CreateInpcPropertyAccessor)
            .Build();
        return new CompiledBindingExtension(path) { Source = Loc.Instance, Mode = BindingMode.OneWay };
    }
}
