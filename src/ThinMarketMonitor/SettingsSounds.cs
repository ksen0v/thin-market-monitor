using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ThinMarketMonitor;

internal sealed partial class SettingsPanel
{
    private readonly DispatcherTimer mixSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Button? voiceMixPreview;

    private void SaveSoundMix()
    {
        mixSaveTimer.Stop();
        saveNotifications();
    }

    private StackPanel SoundOptions()
    {
        var mix = settings.Audio;
        var panel = new StackPanel();
        panel.Children.Add(Terminal.Heading("Микшер звука"));
        mixSaveTimer.Tick += (_, _) => SaveSoundMix();
        AddChannel("Общая громкость", mix.Master, value => mix.Master = value);
        AddEffect("Импульс цены", settings.PriceSignalTone, tone => settings.PriceSignalTone = tone,
            mix.Price, value => mix.Price = value);
        AddEffect("Импульс объёма", settings.VolumeSignalTone, tone => settings.VolumeSignalTone = tone,
            mix.Volume, value => mix.Volume = value);
        AddEffect("Ценовое оповещение", settings.PriceAlertTone, tone => settings.PriceAlertTone = tone,
            mix.PriceAlert, value => mix.PriceAlert = value);
        AddEffect("Плотность стакана", settings.DensityTone, tone => settings.DensityTone = tone,
            mix.Density, value => mix.Density = value);
        var voice = AddChannel("Голос", mix.Voice, value => mix.Voice = value);
        voiceMixPreview = Audition("Голос");
        voiceMixPreview.IsEnabled = false;
        voiceMixPreview.Click += (_, _) => previewVoice(SignalSpeech.Format(SignalSpeech.Example(SignalModule.Price),
            settings.SpeechFor(SignalModule.Price)));
        Grid.SetColumn(voiceMixPreview, 2);
        voice.Row.Children.Add(voiceMixPreview);
        return panel;

        void AddEffect(string name, SignalTone initialTone, Action<SignalTone> setTone, int level, Action<int> setLevel)
        {
            var (row, slider) = AddChannel(name, level, setLevel);
            var list = new ComboBox
            {
                Width = 131, MinHeight = 29, Margin = new Thickness(0, 0, 10, 0), ItemsSource = SignalSound.Options,
                DisplayMemberPath = nameof(SignalToneOption.Label), SelectedValuePath = nameof(SignalToneOption.Tone),
                SelectedValue = initialTone
            };
            AutomationProperties.SetName(list, $"Мелодия · {name}");
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedValue is not SignalTone tone) return;
                setTone(tone);
                SaveSoundMix();
            };
            row.Children.Add(list);
            var preview = Audition(name);
            preview.Click += (_, _) =>
            {
                if (list.SelectedValue is SignalTone tone) previewSound(tone, (int)slider.Value);
            };
            Grid.SetColumn(preview, 2);
            row.Children.Add(preview);
        }

        (Grid Row, Slider Slider) AddChannel(string name, int initial, Action<int> change)
        {
            var caption = Terminal.Copy(name, muted: false);
            caption.Margin = new Thickness(0, 10, 0, 3);
            panel.Children.Add(caption);
            var row = new Grid();
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var controls = new DockPanel { MinHeight = 29 };
            var amount = Terminal.Copy($"{initial}%");
            amount.Width = 38;
            amount.VerticalAlignment = VerticalAlignment.Center;
            amount.TextAlignment = TextAlignment.Right;
            amount.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(amount, Dock.Right);
            controls.Children.Add(amount);
            var slider = new Slider
            {
                Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true,
                SmallChange = 1, LargeChange = 10, Value = initial, VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(slider, name == "Общая громкость" ? name : $"Громкость · {name}");
            slider.ValueChanged += (_, _) =>
            {
                int percent = (int)slider.Value;
                amount.Text = $"{percent}%";
                change(percent);
                mixSaveTimer.Stop();
                mixSaveTimer.Start();
            };
            controls.Children.Add(slider);
            Grid.SetColumn(controls, 1);
            row.Children.Add(controls);
            panel.Children.Add(row);
            return (row, slider);
        }

        static Button Audition(string name)
        {
            var button = Terminal.IconButton("play", $"Прослушать · {name}");
            button.Width = 32;
            button.MinHeight = 29;
            button.Padding = new Thickness(0);
            button.Margin = new Thickness(8, 0, 0, 0);
            return button;
        }
    }
}
