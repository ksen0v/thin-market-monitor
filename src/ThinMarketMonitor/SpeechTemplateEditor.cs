using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ThinMarketMonitor;

internal sealed class SpeechTemplateEditor : Border
{
    private readonly Settings settings;
    private readonly Action save;
    private readonly Dictionary<SignalModule, SignalSpeechOptions> drafts;
    private readonly ComboBox modules = new()
    {
        ItemsSource = new[] { "Импульс цены", "Импульс объёма" }, Width = 158
    };
    private readonly ComboBox precision = new()
    {
        ItemsSource = new[] { "Целые", "До десятых", "До сотых" }, Width = 108
    };
    private readonly TextBox template = new()
    {
        Height = 68, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top,
        Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 8, 0, 6)
    };
    private readonly ComboBox fields = new()
    {
        Width = 258, DisplayMemberPath = nameof(SignalSpeech.Field.Option), Margin = new Thickness(0, 0, 6, 0)
    };
    private readonly Button insert = new() { Content = "Вставить" };
    private readonly Button listen = new() { Content = "Прослушать", Margin = new Thickness(0, 0, 6, 4) };
    private readonly Button reset = new() { Content = "По умолчанию", Margin = new Thickness(0, 0, 6, 4) };
    private readonly Button apply = new() { Content = "Сохранить", Margin = new Thickness(0, 0, 6, 4) };
    private readonly TextBlock example = Terminal.Copy("", muted: false);
    private readonly TextBlock status = Terminal.Copy("");
    private SignalModule module;
    private bool loading;
    private string phrase = "";

    public SpeechTemplateEditor(Settings settings, Action save, Action<string> previewVoice)
    {
        this.settings = settings;
        this.save = save;
        drafts = Enum.GetValues<SignalModule>().ToDictionary(value => value, settings.SpeechFor);
        BorderBrush = Terminal.Brush("Outline");
        BorderThickness = new Thickness(0, 1, 0, 0);
        Margin = new Thickness(0, 12, 0, 8);
        Padding = new Thickness(0, 10, 0, 0);
        var panel = new StackPanel();
        Child = panel;
        var heading = Terminal.Copy("Текст озвучки", muted: false);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new Thickness(0, 0, 0, 7);
        panel.Children.Add(heading);

        var selectors = new WrapPanel();
        panel.Children.Add(selectors);
        modules.Margin = new Thickness(0, 0, 12, 0);
        selectors.Children.Add(modules);
        var rounding = new StackPanel { Orientation = Orientation.Horizontal };
        selectors.Children.Add(rounding);
        var roundingLabel = Terminal.Copy("Округление %");
        roundingLabel.Margin = new Thickness(0, 0, 6, 0);
        roundingLabel.VerticalAlignment = VerticalAlignment.Center;
        rounding.Children.Add(roundingLabel);
        rounding.Children.Add(precision);
        AutomationProperties.SetName(modules, "Тип сигнала для озвучки");
        AutomationProperties.SetName(precision, "Точность процента в речи");
        AutomationProperties.SetName(template, "Шаблон озвучки");
        AutomationProperties.SetName(fields, "Поля сигнала");
        panel.Children.Add(template);
        var insertion = new WrapPanel();
        insertion.Children.Add(fields);
        insertion.Children.Add(insert);
        panel.Children.Add(insertion);

        var sample = Terminal.Rule(new Thickness(1));
        sample.Margin = new Thickness(0, 8, 0, 8);
        sample.Padding = new Thickness(8, 5, 8, 6);
        sample.Background = Terminal.Brush("Plot");
        var sampleContent = new StackPanel();
        sampleContent.Children.Add(Terminal.Copy("Пример озвучки"));
        sampleContent.Children.Add(new ScrollViewer
        {
            Content = example, MaxHeight = 88, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        sample.Child = sampleContent;
        panel.Children.Add(sample);

        apply.Style = Terminal.Style("PrimaryButton");
        var actions = new WrapPanel();
        actions.Children.Add(apply);
        actions.Children.Add(listen);
        actions.Children.Add(reset);
        panel.Children.Add(actions);
        status.Visibility = Visibility.Collapsed;
        panel.Children.Add(status);

        modules.SelectionChanged += (_, _) => Load((SignalModule)modules.SelectedIndex);
        template.TextChanged += (_, _) => UpdateDraft();
        precision.SelectionChanged += (_, _) => UpdateDraft();
        insert.Click += (_, _) =>
        {
            if (fields.SelectedItem is not SignalSpeech.Field field) return;
            int caret = template.SelectionStart + field.Token.Length;
            template.SelectedText = field.Token;
            template.Focus();
            template.Select(caret, 0);
        };
        listen.Click += (_, _) => previewVoice(phrase);
        reset.Click += (_, _) =>
        {
            drafts[module] = SignalSpeechOptions.Default;
            Load(module);
        };
        apply.Click += (_, _) =>
        {
            if (!apply.IsEnabled) return;
            settings.SetSpeech(module, drafts[module]);
            save();
            UpdatePreview();
            ShowStatus("Сохранено.");
        };
        modules.SelectedIndex = (int)SignalModule.Price;
    }

    public bool CanPreview
    {
        get;
        set
        {
            field = value;
            UpdateListen();
        }
    }

    public bool TrySavePending()
    {
        var changed = drafts.Where(item => item.Value != settings.SpeechFor(item.Key)).ToArray();
        foreach (var (kind, draft) in changed)
        {
            if (SignalSpeech.TryFormat(SignalSpeech.Example(kind), draft, out _, out _)) continue;
            modules.SelectedIndex = (int)kind;
            return false;
        }
        if (changed.Length == 0) return true;
        foreach (var (kind, draft) in changed) settings.SetSpeech(kind, draft);
        save();
        UpdatePreview();
        return true;
    }

    public void FocusTemplate() => template.Focus();

    private void Load(SignalModule kind)
    {
        module = kind;
        var draft = drafts[module];
        loading = true;
        template.Text = draft.Text;
        precision.SelectedIndex = draft.PercentDecimals;
        fields.ItemsSource = SignalSpeech.FieldsFor(kind).ToArray();
        fields.SelectedIndex = 0;
        loading = false;
        UpdatePreview();
    }

    private void UpdateDraft()
    {
        if (loading) return;
        string text = template.Text.Trim();
        drafts[module] = new(text == SignalSpeech.DefaultTemplate ? null : text, precision.SelectedIndex);
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var draft = drafts[module];
        bool valid = SignalSpeech.TryFormat(SignalSpeech.Example(module), draft, out phrase, out var error);
        example.Text = valid ? phrase : error;
        example.Foreground = Terminal.Brush(valid ? "Text" : "Falling");
        bool changed = draft != settings.SpeechFor(module);
        apply.IsEnabled = valid && changed;
        reset.IsEnabled = draft != SignalSpeechOptions.Default;
        ShowStatus(changed ? "Есть несохранённые изменения." : "");
        UpdateListen();
    }

    private void UpdateListen()
    {
        listen.IsEnabled = CanPreview && phrase.Length != 0;
        listen.ToolTip = CanPreview ? null : "Загрузите нейроголос или включите русский голос Windows.";
        ToolTipService.SetShowOnDisabled(listen, true);
    }

    private void ShowStatus(string message)
    {
        status.Text = message;
        status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
