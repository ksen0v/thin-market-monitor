using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal sealed class DensityColorPicker : Button
{
    private static readonly string[] Presets =
        ["#61BBDB", "#679CF2", "#A48DE8", "#DD85AA", "#F59986", "#EFB863", "#A5C976", "#C9C7B6"];

    private readonly Border swatch = new() { Width = 20, Height = 13, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 8, 0) };
    private Popup? popup;
    private Border? preview;
    private Slider[] channels = [];
    private Border[] ramps = [];
    private bool synchronizing;
    private Color color;

    internal DensityColorPicker()
    {
        Style = Terminal.Style("TerminalButton");
        MinHeight = 26;
        HorizontalContentAlignment = HorizontalAlignment.Left;
        var face = new StackPanel { Orientation = Orientation.Horizontal };
        face.Children.Add(swatch);
        face.Children.Add(new TextBlock { Text = "Выбрать цвет", VerticalAlignment = VerticalAlignment.Center });
        Content = face;
        Click += (_, _) =>
        {
            popup ??= BuildPopup();
            popup.IsOpen = !popup.IsOpen;
        };
        Unloaded += (_, _) => { if (popup != null) popup.IsOpen = false; };
    }

    internal string Hex
    {
        get => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        set => Select(DensityTint.Read(value).Color);
    }

    private void Select(Color value)
    {
        color = value;
        swatch.Background = DensityTint.FromColor(color);
        ToolTip = Hex;
        AutomationProperties.SetItemStatus(this, Hex);
        if (preview == null) return;
        preview.Background = swatch.Background;
        synchronizing = true;
        channels[0].Value = color.R;
        channels[1].Value = color.G;
        channels[2].Value = color.B;
        synchronizing = false;
        SetRamp(0, Color.FromRgb(0, color.G, color.B), Color.FromRgb(255, color.G, color.B));
        SetRamp(1, Color.FromRgb(color.R, 0, color.B), Color.FromRgb(color.R, 255, color.B));
        SetRamp(2, Color.FromRgb(color.R, color.G, 0), Color.FromRgb(color.R, color.G, 255));
    }

    private void SetRamp(int index, Color start, Color end)
    {
        var gradient = new LinearGradientBrush(start, end, 0);
        gradient.Freeze();
        ramps[index].Background = gradient;
    }

    private Popup BuildPopup()
    {
        var picker = new Popup { Placement = PlacementMode.Bottom, PlacementTarget = this, StaysOpen = false, AllowsTransparency = true };
        var panel = new StackPanel();
        var surface = Terminal.Rule(new Thickness(1));
        surface.Background = Terminal.Brush("Surface");
        surface.CornerRadius = new CornerRadius(5);
        surface.Padding = new Thickness(10);
        surface.Child = panel;
        picker.Child = surface;
        KeyboardNavigation.SetTabNavigation(surface, KeyboardNavigationMode.Cycle);
        surface.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Done();
            e.Handled = true;
        };

        var presets = new UniformGrid { Rows = 1, Margin = new Thickness(0, 0, -4, 8) };
        foreach (string preset in Presets)
        {
            var chip = new Button { Width = 24, Height = 23, MinHeight = 0, Padding = new Thickness(2), Margin = new Thickness(0, 0, 4, 0),
                Content = new Border { Background = DensityTint.Read(preset), Width = 17, Height = 15, CornerRadius = new CornerRadius(2) } };
            AutomationProperties.SetName(chip, $"Образец {preset}");
            chip.Click += (_, _) => Hex = preset;
            presets.Children.Add(chip);
        }
        panel.Children.Add(presets);
        var names = new[] { "Красный", "Зелёный", "Синий" };
        channels = new Slider[names.Length];
        ramps = new Border[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            var row = new DockPanel();
            var label = Terminal.Copy(names[i]);
            label.Width = 68;
            label.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);
            var track = new StackPanel();
            var slider = channels[i] = new Slider
            {
                Minimum = 0, Maximum = 255, SmallChange = 1, LargeChange = 16,
                IsSnapToTickEnabled = true, TickFrequency = 1, Height = 19,
            };
            AutomationProperties.SetName(slider, names[i]);
            track.Children.Add(slider);
            ramps[i] = new Border { Height = 3, Margin = new Thickness(3, 0, 3, 5), CornerRadius = new CornerRadius(1) };
            track.Children.Add(ramps[i]);
            row.Children.Add(track);
            panel.Children.Add(row);
        }
        var footer = new DockPanel { Margin = new Thickness(0, 7, 0, 0) };
        var done = new Button { Content = "Готово", MinHeight = 25, Margin = new Thickness(8, 0, 0, 0) };
        done.Click += (_, _) => Done();
        DockPanel.SetDock(done, Dock.Right);
        footer.Children.Add(done);
        preview = new Border { Height = 24, CornerRadius = new CornerRadius(3) };
        AutomationProperties.SetName(preview, "Выбранный цвет");
        footer.Children.Add(preview);
        panel.Children.Add(footer);
        foreach (var slider in channels)
            slider.ValueChanged += (_, _) =>
            {
                if (!synchronizing) Select(Color.FromRgb((byte)channels[0].Value, (byte)channels[1].Value, (byte)channels[2].Value));
            };
        picker.Opened += (_, _) => ((Button)presets.Children[0]).Focus();
        Select(color);
        return picker;

        void Done()
        {
            picker.IsOpen = false;
            Focus();
        }
    }
}
