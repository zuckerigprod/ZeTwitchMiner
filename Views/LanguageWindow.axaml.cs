using Avalonia.Controls;

namespace ZeTwitchMiner.Views;

// Выбор языка при первом запуске
public partial class LanguageWindow : Window
{
    public string? Choice { get; private set; }

    public LanguageWindow()
    {
        InitializeComponent();
        Ru.Click += (_, _) => Pick("ru");
        En.Click += (_, _) => Pick("en");
    }

    private void Pick(string lang)
    {
        Choice = lang;
        Close();
    }
}
