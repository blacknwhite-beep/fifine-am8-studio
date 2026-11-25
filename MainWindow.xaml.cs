using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FifineAM8Studio.Audio;

namespace FifineAM8Studio;

public partial class MainWindow : Window
{
    private readonly AudioEngine _engine = new();
    private readonly FifineDriverClient _driver = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly float[] _waveBuf = new float[256];
    private readonly Slider[] _eqSliders = new Slider[10];
    private readonly TextBlock[] _eqValueLabels = new TextBlock[10];
    private bool _isInitializing = true;

    private static readonly string[] BandLabels = { "31Hz", "62Hz", "125Hz", "250Hz", "500Hz", "1kHz", "2kHz", "4kHz", "8kHz", "16kHz" };
    private static readonly string[] BandColors = { "#e91e63", "#f06292", "#9c27b0", "#673ab7", "#3f51b5", "#2196f3", "#03a9f4", "#00bcd4", "#009688", "#8bc34a" };

    // Exact FIFINE Genie EQ presets extracted via REA
    private static readonly Dictionary<string, double[]> Presets = new()
    {
        ["Flat"]         = new[] { 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 },
        ["Broadcast"]    = new[] { -12.0, 3.0, 5.0, 2.5, -2.5, -4.0, 0.0, 4.0, 5.0, 6.0 },
        ["Clarity"]      = new[] { -12.0, -4.0, -2.0, -1.0, 0.0, 0.0, 4.0, 4.0, 5.0, 0.0 },
        ["DeepVoice"]    = new[] { -12.0, 4.0, 4.0, 3.0, 0.0, -3.0, -1.0, 0.0, 0.0, 0.0 },
        ["LessNasal"]    = new[] { -12.0, 0.0, 0.0, 0.0, -2.0, -4.0, -2.0, 2.0, 4.0, 0.0 },
        ["WalkieTalkie"] = new[] { -12.0, -12.0, -12.0, -12.0, 5.0, 6.0, 6.0, 5.0, -12.0, -12.0 },
    };

    private static string SettingsPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FifineAM8Studio", "settings.json");

    public MainWindow()
    {
        InitializeComponent();
        BuildEqSliders();
        LoadSettings();
        PopulateDevices();
        _isInitializing = false;
        ApplyAllUiToDsp();
        RestartAudioEngine();

        // Hard-mute support at the real FIFINE driver level (same WebSocket
        // channel Genie uses) so Discord/OBS get silence even though they
        // capture the virtual mic directly and bypass our WASAPI DSP chain.
        _driver.Connect();
        _driver.SetMute(_engine.Dsp.Params.IsMuted);
        StatusLabel.Text = $"{_engine.StatusText} · {_driver.Status}";

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _uiTimer.Tick += (_, _) => UpdateLiveMetersAndWaveform();
        _uiTimer.Start();

        Closed += (_, _) =>
        {
            _uiTimer.Stop();
            SaveSettings();
            _engine.Dispose();
            _driver.Dispose();
        };
    }

    private void BuildEqSliders()
    {
        EqSlidersGrid.Children.Clear();
        for (int i = 0; i < 10; i++)
        {
            int idx = i;
            var col = new Grid { Margin = new Thickness(4, 0, 4, 0) };
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var valTxt = new TextBlock
            {
                Text = "0.0",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(BandColors[i])),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Grid.SetRow(valTxt, 0);
            _eqValueLabels[i] = valTxt;

            var slider = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = -12,
                Maximum = 12,
                Value = 0,
                TickFrequency = 0.5,
                IsSnapToTickEnabled = true,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            slider.ValueChanged += (_, e) =>
            {
                if (_isInitializing) return;
                _engine.Dsp.UpdateEqBand(idx, e.NewValue);
                _eqValueLabels[idx].Text = $"{e.NewValue:+0.0;-0.0;0.0}";
                DrawEqCurve();
                SaveSettings();
            };
            Grid.SetRow(slider, 1);
            _eqSliders[i] = slider;

            var freqTxt = new TextBlock
            {
                Text = BandLabels[i],
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0)
            };
            Grid.SetRow(freqTxt, 2);

            col.Children.Add(valTxt);
            col.Children.Add(slider);
            col.Children.Add(freqTxt);
            EqSlidersGrid.Children.Add(col);
        }
    }

    private void PopulateDevices()
    {
        bool prev = _isInitializing;
        _isInitializing = true;

        var caps = AudioEngine.GetCaptureDevices();
        var rends = AudioEngine.GetRenderDevices();

        ComboInput.ItemsSource = caps;
        ComboInput.DisplayMemberPath = nameof(AudioDeviceInfo.FriendlyName);
        ComboInput.SelectedItem =
            caps.FirstOrDefault(d => d.FriendlyName.Contains("fifine Microphone", StringComparison.OrdinalIgnoreCase) && !d.FriendlyName.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            ?? caps.FirstOrDefault(d => d.FriendlyName.Contains("fifine", StringComparison.OrdinalIgnoreCase))
            ?? caps.FirstOrDefault();

        var virtualChoices = new List<AudioDeviceInfo> { new("", "(Disabled)") };
        virtualChoices.AddRange(rends);
        ComboVirtualOut.ItemsSource = virtualChoices;
        ComboVirtualOut.DisplayMemberPath = nameof(AudioDeviceInfo.FriendlyName);
        ComboVirtualOut.SelectedItem =
            virtualChoices.FirstOrDefault(d => d.FriendlyName.Contains("fifine - Microphone", StringComparison.OrdinalIgnoreCase))
            ?? virtualChoices.FirstOrDefault(d => d.FriendlyName.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            ?? virtualChoices.First();

        var monChoices = new List<AudioDeviceInfo> { new("", "(Disabled)") };
        monChoices.AddRange(rends);
        ComboMonitorOut.ItemsSource = monChoices;
        ComboMonitorOut.DisplayMemberPath = nameof(AudioDeviceInfo.FriendlyName);
        // Prefer the user's real headphones/headset over the AM8's own headphone
        // jack (which is where the old default "Speakers (fifine Microphone)"
        // pointed) -- otherwise the monitor switch appears to do nothing.
        bool IsHeadphones(string n) =>
            n.Contains("Razer", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Nari", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Headphones", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Headset", StringComparison.OrdinalIgnoreCase);
        ComboMonitorOut.SelectedItem =
            monChoices.FirstOrDefault(d => !string.IsNullOrEmpty(d.Id) && IsHeadphones(d.FriendlyName) && !d.FriendlyName.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            ?? monChoices.FirstOrDefault(d => !string.IsNullOrEmpty(d.Id) && !d.FriendlyName.Contains("fifine", StringComparison.OrdinalIgnoreCase) && !d.FriendlyName.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            ?? monChoices.FirstOrDefault(d => !string.IsNullOrEmpty(d.Id))
            ?? monChoices.First();

        _isInitializing = prev;
    }

    private void RestartAudioEngine()
    {
        var cap = ComboInput.SelectedItem as AudioDeviceInfo;
        var virt = ComboVirtualOut.SelectedItem as AudioDeviceInfo;
        var mon = ComboMonitorOut.SelectedItem as AudioDeviceInfo;

        _engine.Start(cap?.Id, string.IsNullOrEmpty(virt?.Id) ? null : virt.Id, string.IsNullOrEmpty(mon?.Id) ? null : mon.Id);
        StatusLabel.Text = $"{_engine.StatusText} · {_driver.Status}";
        StatusDot.Fill = _engine.IsRunning
            ? new SolidColorBrush(Color.FromRgb(34, 197, 94))
            : new SolidColorBrush(Color.FromRgb(239, 68, 68));
    }

    private void ApplyAllUiToDsp()
    {
        var p = _engine.Dsp.Params;
        p.NoiseReductionEnabled = ToggleNoiseReduction.IsChecked == true;
        p.LowCutEnabled = ToggleLowCut.IsChecked == true;
        p.PresenceBoostEnabled = TogglePresence.IsChecked == true;
        p.CompressorEnabled = ToggleCompressor.IsChecked == true;
        p.LimiterEnabled = ToggleLimiter.IsChecked == true;
        p.LimiterCeilingDb = (float)SliderLimiter.Value;
        p.EqEnabled = ToggleEq.IsChecked == true;
        p.ReverbEnabled = ToggleReverb.IsChecked == true;
        p.MonitorEnabled = ToggleMonitor.IsChecked == true;

        p.GainDb = (float)SliderGain.Value;
        p.NoiseGateThresholdDb = (float)SliderGate.Value;
        p.CompressorAmount = (float)(SliderComp.Value / 100.0);
        p.ReverbLevel = (float)(SliderReverb.Value / 100.0);
        p.MonitorVolume = (float)(SliderMonitor.Value / 100.0);

        p.VoiceFx = FxMonster.IsChecked == true ? VoiceFxMode.Monster
                  : FxRobot.IsChecked == true ? VoiceFxMode.Robot
                  : FxMegaphone.IsChecked == true ? VoiceFxMode.Megaphone
                  : VoiceFxMode.Default;

        GainValText.Text = $"{p.GainDb:+0.0;-0.0;0.0} dB";
        GateValText.Text = $"{p.NoiseGateThresholdDb:0} dB";
        CompValText.Text = $"{SliderComp.Value:0}%";
        LimiterValText.Text = $"{SliderLimiter.Value:+0.0;-0.0;0.0} dB";
        ReverbValText.Text = $"{SliderReverb.Value:0}%";
        MonitorValText.Text = $"{SliderMonitor.Value:0}%";

        for (int i = 0; i < 10; i++)
        {
            _engine.Dsp.UpdateEqBand(i, _eqSliders[i].Value);
            _eqValueLabels[i].Text = $"{_eqSliders[i].Value:+0.0;-0.0;0.0}";
        }

        DrawEqCurve();
    }

    private void ParamChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        ApplyAllUiToDsp();
        SaveSettings();
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isInitializing) return;
        ApplyAllUiToDsp();
        SaveSettings();
    }

    private void QuickMuteBtn_Click(object sender, RoutedEventArgs e)
    {
        _engine.Dsp.Params.IsMuted = !_engine.Dsp.Params.IsMuted;
        // Mute at the FIFINE driver as well: Discord/OBS capture the virtual
        // mic from the driver, bypassing this app's DSP/WASAPI output.
        _driver.SetMute(_engine.Dsp.Params.IsMuted);
        if (_engine.Dsp.Params.IsMuted)
        {
            QuickMuteBtn.Content = "MIC MUTED (Click to Unmute)";
            QuickMuteBtn.Background = new SolidColorBrush(Color.FromRgb(185, 28, 28));
            QuickMuteBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        }
        else
        {
            QuickMuteBtn.Content = "MIC LIVE (Click to Mute)";
            QuickMuteBtn.Background = new SolidColorBrush(Color.FromRgb(35, 39, 58));
            QuickMuteBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(53, 58, 82));
        }
    }

    private void ResetAllBtn_Click(object sender, RoutedEventArgs e)
    {
        _isInitializing = true;
        ToggleNoiseReduction.IsChecked = true;
        ToggleLowCut.IsChecked = true;
        TogglePresence.IsChecked = false;
        ToggleCompressor.IsChecked = true;
        ToggleLimiter.IsChecked = true;
        ToggleEq.IsChecked = true;
        ToggleReverb.IsChecked = false;
        ToggleMonitor.IsChecked = false;
        SliderGain.Value = 0;
        SliderGate.Value = -46;
        SliderComp.Value = 45;
        SliderLimiter.Value = -3;
        SliderReverb.Value = 22;
        SliderMonitor.Value = 75;
        FxDefault.IsChecked = true;
        for (int i = 0; i < 10; i++) _eqSliders[i].Value = 0;
        _isInitializing = false;
        ApplyAllUiToDsp();
        SaveSettings();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string key && Presets.TryGetValue(key, out var gains))
        {
            _isInitializing = true;
            for (int i = 0; i < 10; i++) _eqSliders[i].Value = gains[i];
            _isInitializing = false;
            ApplyAllUiToDsp();
            SaveSettings();
        }
    }

    private void Nav_Changed(object sender, RoutedEventArgs e)
    {
        if (PanelConfig == null || PanelEq == null || PanelRouting == null) return;
        PanelConfig.Visibility = NavConfig.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelEq.Visibility = NavEq.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelRouting.Visibility = NavRouting.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavEq.IsChecked == true) DrawEqCurve();
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        PopulateDevices();
        RestartAudioEngine();
    }

    private void DeviceCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        RestartAudioEngine();
    }

    private void EqCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawEqCurve();

    private void DrawEqCurve()
    {
        if (EqCanvas == null || EqCanvas.ActualWidth < 40 || EqCanvas.ActualHeight < 40) return;
        EqCanvas.Children.Clear();

        double w = EqCanvas.ActualWidth;
        double h = EqCanvas.ActualHeight;
        double padL = 36, padR = 16, padT = 14, padB = 22;
        double plotW = w - padL - padR;
        double plotH = h - padT - padB;

        // Horizontal dB grid lines (+12, +6, 0, -6, -12)
        double[] dbLines = { 12, 6, 0, -6, -12 };
        foreach (double db in dbLines)
        {
            double y = padT + (12.0 - db) / 24.0 * plotH;
            var line = new Line
            {
                X1 = padL, X2 = w - padR, Y1 = y, Y2 = y,
                Stroke = new SolidColorBrush(db == 0 ? Color.FromRgb(75, 85, 110) : Color.FromRgb(35, 39, 54)),
                StrokeThickness = db == 0 ? 1.2 : 1
            };
            EqCanvas.Children.Add(line);

            var lbl = new TextBlock
            {
                Text = $"{db:+0;-0;0}",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(120, 130, 150))
            };
            Canvas.SetLeft(lbl, 4);
            Canvas.SetTop(lbl, y - 7);
            EqCanvas.Children.Add(lbl);
        }

        // Combined frequency response curve (#9147FF)
        double minLog = Math.Log10(20.0);
        double maxLog = Math.Log10(20000.0);
        var curvePts = new PointCollection();
        int steps = 160;
        for (int s = 0; s <= steps; s++)
        {
            double frac = (double)s / steps;
            double freq = Math.Pow(10.0, minLog + frac * (maxLog - minLog));
            double db = Math.Clamp(_engine.Dsp.EvaluateCombinedEqDb(freq), -12.0, 12.0);
            double x = padL + frac * plotW;
            double y = padT + (12.0 - db) / 24.0 * plotH;
            curvePts.Add(new Point(x, y));
        }

        var poly = new Polyline
        {
            Points = curvePts,
            Stroke = new SolidColorBrush(Color.FromRgb(145, 71, 255)),
            StrokeThickness = 3.0,
            StrokeLineJoin = PenLineJoin.Round
        };
        EqCanvas.Children.Add(poly);

        // 10 colored FIFINE band nodes
        for (int i = 0; i < 10; i++)
        {
            double frac = (Math.Log10(DspParameters.BandFrequencies[i]) - minLog) / (maxLog - minLog);
            double x = padL + frac * plotW;
            double y = padT + (12.0 - _engine.Dsp.Params.EqGainsDb[i]) / 24.0 * plotH;

            var dot = new Ellipse
            {
                Width = 10, Height = 10,
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(BandColors[i])),
                Stroke = Brushes.White,
                StrokeThickness = 1.5
            };
            Canvas.SetLeft(dot, x - 5);
            Canvas.SetTop(dot, y - 5);
            EqCanvas.Children.Add(dot);

            var fLbl = new TextBlock
            {
                Text = BandLabels[i],
                FontSize = 9.5,
                Foreground = new SolidColorBrush(Color.FromRgb(140, 148, 168))
            };
            Canvas.SetLeft(fLbl, x - 14);
            Canvas.SetTop(fLbl, h - 17);
            EqCanvas.Children.Add(fLbl);
        }
    }

    private void UpdateLiveMetersAndWaveform()
    {
        bool muted = _engine.Dsp.Params.IsMuted;
        float inDb = muted ? -60f : _engine.Dsp.InputLevelDb;
        float outDb = muted ? -60f : _engine.Dsp.OutputLevelDb;

        InputDbText.Text = muted ? "MUTED" : $"{inDb:0} dB";
        OutputDbText.Text = muted ? "MUTED" : $"{outDb:0} dB";

        double inFrac = muted ? 0.0 : Math.Clamp((inDb + 60.0) / 60.0, 0.0, 1.0);
        double outFrac = muted ? 0.0 : Math.Clamp((outDb + 60.0) / 60.0, 0.0, 1.0);
        InputMeterFill.Width = inFrac * 194.0;
        OutputMeterFill.Width = outFrac * 194.0;
        GateBadge.Text = _engine.Dsp.Params.IsMuted ? "Gate: Muted" : (_engine.Dsp.GateIsOpen ? "Gate: Open" : "Gate: Closed");

        // Render live waveform
        double w = WaveformCanvas.ActualWidth;
        double h = WaveformCanvas.ActualHeight;
        if (w < 20 || h < 10) return;

        _engine.Dsp.CopyWaveform(_waveBuf);
        WaveformCanvas.Children.Clear();
        var pts = new PointCollection(_waveBuf.Length);
        double mid = h * 0.5;
        for (int i = 0; i < _waveBuf.Length; i++)
        {
            double x = (double)i / (_waveBuf.Length - 1) * w;
            double y = mid - Math.Clamp(_waveBuf[i], -1f, 1f) * (mid - 4.0);
            pts.Add(new Point(x, y));
        }
        WaveformCanvas.Children.Add(new Polyline
        {
            Points = pts,
            Stroke = new SolidColorBrush(_engine.Dsp.Params.IsMuted ? Color.FromRgb(239, 68, 68) : Color.FromRgb(145, 71, 255)),
            StrokeThickness = 1.8
        });
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(_engine.Dsp.Params, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var loaded = JsonSerializer.Deserialize<DspParameters>(File.ReadAllText(SettingsPath));
            if (loaded == null) return;

            ToggleNoiseReduction.IsChecked = loaded.NoiseReductionEnabled;
            ToggleLowCut.IsChecked = loaded.LowCutEnabled;
            TogglePresence.IsChecked = loaded.PresenceBoostEnabled;
            ToggleCompressor.IsChecked = loaded.CompressorEnabled;
            ToggleLimiter.IsChecked = loaded.LimiterEnabled;
            ToggleEq.IsChecked = loaded.EqEnabled;
            ToggleReverb.IsChecked = loaded.ReverbEnabled;
            ToggleMonitor.IsChecked = loaded.MonitorEnabled;
            SliderGain.Value = Math.Clamp(loaded.GainDb, -24, 24);
            SliderGate.Value = Math.Clamp(loaded.NoiseGateThresholdDb, -65, -15);
            SliderComp.Value = Math.Clamp(loaded.CompressorAmount * 100.0, 0, 100);
            SliderLimiter.Value = Math.Clamp(loaded.LimiterCeilingDb, -12, -0.5);
            SliderReverb.Value = Math.Clamp(loaded.ReverbLevel * 100.0, 0, 85);
            SliderMonitor.Value = Math.Clamp(loaded.MonitorVolume * 100.0, 0, 100);
            FxMonster.IsChecked = loaded.VoiceFx == VoiceFxMode.Monster;
            FxRobot.IsChecked = loaded.VoiceFx == VoiceFxMode.Robot;
            FxMegaphone.IsChecked = loaded.VoiceFx == VoiceFxMode.Megaphone;
            if (loaded.EqGainsDb is { Length: 10 })
            {
                for (int i = 0; i < 10; i++)
                    _eqSliders[i].Value = Math.Clamp(loaded.EqGainsDb[i], -12, 12);
            }
        }
        catch { }
    }
}
