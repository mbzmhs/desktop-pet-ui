using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopPetUi.Core;

namespace DesktopPetUi;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _selected;

    public SettingsWindow(AppConfig config)
    {
        _config = config;
        InitializeComponent();
        ReloadList();
        LoadGlobal();
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); StatusText.Text = ""; };
    }

    private void ShowStatus(string text, bool ok = true)
    {
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(ok
            ? System.Windows.Media.Color.FromRgb(0x6C, 0xB8, 0x6C)
            : System.Windows.Media.Color.FromRgb(0xE8, 0x6C, 0x6C));
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void ReloadList()
    {
        CharacterList.Items.Clear();
        foreach (var name in _config.ListCharacters())
        {
            CharacterList.Items.Add(name);
        }
        var current = _config.Character.Current;
        CharacterList.SelectedItem = current;
        if (string.IsNullOrEmpty(current)) SetCurrentLabel("");
    }

    private string ProfilePath(string name) =>
        Path.Combine(_config.CharacterDir, name, "character.json");

    private void LoadCharPreview(string name)
    {
        CharPreviewImage.Source = null;
        CharPreviewEmptyText.Visibility = Visibility.Visible;
        try
        {
            var dir = Path.Combine(_config.CharacterDir, name, "idle");
            if (!Directory.Exists(dir)) return;
            var files = Directory.GetFiles(dir, "*.png");
            if (files.Length == 0) return;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(files[0], UriKind.Absolute);
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            CharPreviewImage.Source = bmp;
            CharPreviewEmptyText.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // 预览加载失败时保持占位提示
        }
    }

    private void OnCharacterSelected(object sender, SelectionChangedEventArgs e)
    {
        if (CharacterList.SelectedItem is string name)
        {
            _selected = name;
            LoadCharPreview(name);
            var profile = CharacterProfile.Load(ProfilePath(name));
            NameText.Text = name;
            PromptBox.Text = profile.Llm.SystemPrompt ?? "";
            TemperatureBox.Text = profile.Llm.Temperature is double t ? t.ToString("0.###") : "0.7";
            MaxTokensBox.Text = profile.Llm.MaxTokens is int m ? m.ToString() : "";
            CharTextLangBox.SelectedIndex = CharTagIndex(CharTextLangBox, profile.Tts?.TextLang ?? "auto");
            CharTtsProviderBox.SelectedIndex = CharTagIndex(CharTtsProviderBox, profile.Tts?.Provider ?? "none");
            CharVoiceBox.Text = profile.Tts?.VoiceId ?? "";
            _ = RefreshCharVoicesAsync();
            CharProactiveTempBox.Text = profile.ProactiveTemperature is double p ? p.ToString("0.###") : "0.7";
            CharUserAddressBox.Text = profile.UserAddress ?? "";
            if (profile.Scale is double s && s > 0)
            {
                CharScaleInheritCheck.IsChecked = false;
                CharScaleSlider.Value = Math.Clamp(s, CharScaleSlider.Minimum, CharScaleSlider.Maximum);
            }
            else
            {
                CharScaleInheritCheck.IsChecked = true;
            }
            CharScaleSlider.IsEnabled = CharScaleInheritCheck.IsChecked != true;
            CharScaleValueText.Text = CharScaleSlider.Value.ToString("0.00");
            SaveButton.IsEnabled = true;
            SetCurrentButton.IsEnabled = true;
            SetCurrentLabel(name);
        }
        else
        {
            _selected = null;
            SaveButton.IsEnabled = false;
            SetCurrentButton.IsEnabled = false;
        }
    }

    private void SetCurrentLabel(string name)
    {
        var current = _config.Character.Current;
        CurrentLabel.Text = string.IsNullOrEmpty(name)
            ? (string.IsNullOrEmpty(current) ? "" : "当前角色：" + current)
            : (name == current ? "（当前角色）" : "当前角色：" + current);
    }

    private static int CharTagIndex(System.Windows.Controls.ComboBox box, string? tag)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem it &&
                string.Equals(it.Tag as string, tag ?? "", StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 0;
    }

    private void OnCharScaleInheritChanged(object sender, RoutedEventArgs e)
    {
        CharScaleSlider.IsEnabled = CharScaleInheritCheck.IsChecked != true;
        CharScaleValueText.Text = CharScaleSlider.Value.ToString("0.00");
    }

    private void OnCharScaleValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CharScaleValueText != null)
            CharScaleValueText.Text = CharScaleSlider.Value.ToString("0.00");
    }

    private int _charVoiceRefreshSeq;

    private async void OnCharTtsProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        await RefreshCharVoicesAsync(selectFirst: true);
    }

    private async Task RefreshCharVoicesAsync(bool selectFirst = false)
    {
        var seq = ++_charVoiceRefreshSeq;
        var provider = (CharTtsProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        if (string.IsNullOrEmpty(provider)) provider = _config.Chat.Tts.Provider;
        var current = CharVoiceBox.Text;
        var items = new List<ComboBoxItem>();
        if (string.Equals(provider, "windows", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var v in TtsClient.GetInstalledWindowsVoices())
                items.Add(new ComboBoxItem { Content = v, Tag = v });
        }
        else if (string.Equals(provider, "gptsovits", StringComparison.OrdinalIgnoreCase))
        {
            var url = _config.Chat.Tts.Url?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    var voices = await Task.Run(() => TtsClient.GetAvailableVoicesAsync(url));
                    foreach (var (id, display) in voices)
                        items.Add(new ComboBoxItem { Content = display, Tag = id });
                }
                catch (Exception ex)
                {
                    Log.Error("GetAvailableVoicesAsync failed", ex);
                    ShowStatus("读取音色失败：" + ex.Message, ok: false);
                }
            }
            else
            {
                ShowStatus("请在全局设置里填写 TTS 地址", ok: false);
            }
        }
        if (seq != _charVoiceRefreshSeq) return;
        CharVoiceBox.Items.Clear();
        foreach (var it in items) CharVoiceBox.Items.Add(it);
        if (selectFirst)
        {
            CharVoiceBox.Text = "";
            if (items.Count > 0) CharVoiceBox.SelectedIndex = 0;
        }
        else
        {
            CharVoiceBox.Text = current;
        }
        if (string.Equals(provider, "gptsovits", StringComparison.OrdinalIgnoreCase))
            ShowStatus("已从 tts-server 读取 " + items.Count + " 个音色");
    }

    private void OnSetCurrent(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        App.SwitchCharacter(_selected);
        SetCurrentLabel(_selected);
        ShowStatus("已切换到角色：" + _selected);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var profile = new CharacterProfile { Name = _selected };
        profile.Llm.SystemPrompt = PromptBox.Text ?? "";

        if (double.TryParse(TemperatureBox.Text?.Trim(), out var t))
            profile.Llm.Temperature = t;
        else
            profile.Llm.Temperature = null;

        if (int.TryParse(MaxTokensBox.Text?.Trim(), out var m))
            profile.Llm.MaxTokens = m;
        else
            profile.Llm.MaxTokens = null;

        var tts = new CharacterTtsConfig();
        var hasTts = false;
        var prov = (CharTtsProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "none";
        if (!string.IsNullOrEmpty(prov)) { tts.Provider = prov; hasTts = true; }
        var lang = (CharTextLangBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        if (!string.IsNullOrEmpty(lang)) { tts.TextLang = lang; hasTts = true; }
        string? voice = null;
        if (CharVoiceBox.SelectedItem is ComboBoxItem vIt && vIt.Tag is string vTag && !string.IsNullOrWhiteSpace(vTag))
            voice = vTag.Trim();
        else if (!string.IsNullOrWhiteSpace(CharVoiceBox.Text))
            voice = CharVoiceBox.Text.Trim();
        if (!string.IsNullOrEmpty(voice)) { tts.VoiceId = voice; hasTts = true; }
        profile.Tts = hasTts ? tts : null;

        if (double.TryParse(CharProactiveTempBox.Text?.Trim(), out var pt))
            profile.ProactiveTemperature = pt;
        else
            profile.ProactiveTemperature = null;

        profile.UserAddress = string.IsNullOrWhiteSpace(CharUserAddressBox.Text)
            ? null
            : CharUserAddressBox.Text.Trim();

        if (CharScaleInheritCheck.IsChecked == true)
            profile.Scale = null;
        else
            profile.Scale = CharScaleSlider.Value;

        profile.Save(ProfilePath(_selected));
        _config.LoadActiveCharacter();
        SetCurrentLabel(_selected);
        if (string.Equals(_selected, _config.Character.Current, StringComparison.OrdinalIgnoreCase))
            App.SwitchCharacter(_selected);
        ShowStatus("已保存角色设置：" + _selected);
    }

    private void LoadGlobal()
    {
        ProviderBox.SelectedIndex = TagIndex(ProviderBox, _config.Chat.Provider);
        ApiKeyBox.Password = _config.Chat.ApiKey ?? "";
        ApiBaseUrlBox.Text = _config.Chat.ApiBaseUrl ?? "";
        ApiModelBox.Text = _config.Chat.ApiModel ?? "";
ExtraParamsBox.Text = CurrentProviderExtra();
        ProxyModeBox.SelectedIndex = TagIndex(ProxyModeBox, _config.Chat.Proxy.Mode);
        ProxyAddressBox.Text = _config.Chat.Proxy.Address ?? "";
        ProxyAddressBox.IsEnabled = string.Equals(_config.Chat.Proxy.Mode, "custom", StringComparison.OrdinalIgnoreCase);
        TtsUrlBox.Text = _config.Chat.Tts.Url ?? "";
        TtsStreamingCheck.IsChecked = _config.Chat.Tts.Streaming;
        ContextLengthBox.Text = _config.Chat.ContextLength.ToString();
        ProactiveIntervalBox.Text = _config.Chat.ProactiveIntervalSec.ToString("0.###");
        IdleIntervalBox.Text = _config.Character.IdleIntervalSec.ToString("0.###");
        BubbleReserveBox.Text = _config.Character.BubbleReserve.ToString("0.###");
        ProactiveCheck.IsChecked = _config.Chat.Proactive;
        ScreenAwareCheck.IsChecked = _config.Chat.ScreenAware;
        ScreenAwareChanceBox.Text = _config.Chat.ScreenAwareChance.ToString("0.##");
        CrossFadeCheck.IsChecked = _config.Character.CrossFade;
        GlobalUserAddressBox.Text = _config.Chat.UserAddress;
        BubbleDurationSlider.Value = Math.Clamp(_config.Character.BubbleDurationSec, BubbleDurationSlider.Minimum, BubbleDurationSlider.Maximum);
        BubbleDurationValueText.Text = BubbleDurationSlider.Value.ToString("0.#");
    }

    private static int TagIndex(System.Windows.Controls.ComboBox box, string? tag)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem it &&
                string.Equals(it.Tag as string, tag ?? "", StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 0;
    }

    private string CurrentProvider() =>
        (ProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "openai";

    private void OnProxyModeChanged(object sender, SelectionChangedEventArgs e)
    {
        var mode = (ProxyModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "system";
        ProxyAddressBox.IsEnabled = string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase);
    }

    private string CurrentProviderExtra()
    {
        var p = CurrentProvider();
        return _config.Chat.ProviderExtraParams.TryGetValue(p, out var v) ? v ?? "" : "";
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExtraParamsBox != null) ExtraParamsBox.Text = CurrentProviderExtra();
    }

    private async void OnFetchApiModels(object sender, RoutedEventArgs e)
    {
        try
        {
            var baseUrl = ApiBaseUrlBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = _config.Chat.Llama.Url;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                ShowStatus("请先填写 API 地址", ok: false);
                return;
            }
            var apiKey = ApiKeyBox.Password?.Trim() ?? "";
            ShowStatus("获取模型中…");
            var models = await Task.Run(() => LlamaClient.FetchModelsAsync(baseUrl, apiKey));
            if (models.Count == 0)
            {
                ShowStatus("未获取到模型，请检查地址 / API Key", ok: false);
                return;
            }
            var prev = ApiModelBox.Text;
            ApiModelBox.Items.Clear();
            foreach (var m in models) ApiModelBox.Items.Add(m);
            if (string.IsNullOrWhiteSpace(prev)) prev = models[0];
            ApiModelBox.Text = prev;
            ShowStatus("获取到 " + models.Count + " 个模型");
        }
        catch (Exception ex)
        {
            ShowStatus("获取失败：" + ex.Message, ok: false);
        }
    }

    private void OnSaveGlobal(object sender, RoutedEventArgs e)
    {
        try
        {
            var provider = CurrentProvider();
            _config.Chat.Provider = provider;
            _config.Chat.ApiKey = ApiKeyBox.Password?.Trim() ?? "";
            _config.Chat.ApiBaseUrl = ApiBaseUrlBox.Text?.Trim() ?? "";
            _config.Chat.ApiModel = ApiModelBox.Text?.Trim() ?? "";
            var extra = ExtraParamsBox.Text?.Trim() ?? "";
            if (_config.Chat.ProviderExtraParams.ContainsKey(provider))
                _config.Chat.ProviderExtraParams[provider] = extra;
            else
                _config.Chat.ProviderExtraParams.Add(provider, extra);
            _config.Chat.Proxy.Mode = (ProxyModeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "system";
            _config.Chat.Proxy.Address = ProxyAddressBox.Text?.Trim() ?? "";

            if (!string.IsNullOrWhiteSpace(TtsUrlBox.Text)) _config.Chat.Tts.Url = TtsUrlBox.Text.Trim();
            _config.Chat.Tts.Streaming = TtsStreamingCheck.IsChecked == true;

            if (int.TryParse(ContextLengthBox.Text?.Trim(), out var cl) && cl > 0)
                _config.Chat.ContextLength = cl;
            if (double.TryParse(ProactiveIntervalBox.Text?.Trim(), out var pi) && pi > 0)
                _config.Chat.ProactiveIntervalSec = pi;
            if (double.TryParse(IdleIntervalBox.Text?.Trim(), out var ii) && ii > 0)
                _config.Character.IdleIntervalSec = ii;
            if (double.TryParse(BubbleReserveBox.Text?.Trim(), out var br) && br >= 0)
                _config.Character.BubbleReserve = br;

            _config.Chat.Proactive = ProactiveCheck.IsChecked == true;
            _config.Chat.ScreenAware = ScreenAwareCheck.IsChecked == true;
            if (double.TryParse(ScreenAwareChanceBox.Text?.Trim(), out var sac) && sac >= 0 && sac <= 1)
                _config.Chat.ScreenAwareChance = sac;
            _config.Character.CrossFade = CrossFadeCheck.IsChecked == true;
            _config.Character.BubbleDurationSec = BubbleDurationSlider.Value;
            _config.Chat.UserAddress = GlobalUserAddressBox.Text?.Trim() ?? "";

            _config.Save();
            App.RefreshAll();
            Log.Info("Global settings saved");
            ShowStatus("全局设置已保存并生效");
        }
        catch (Exception ex)
        {
            Log.Error("Save global failed", ex);
            ShowStatus("保存失败：" + ex.Message, ok: false);
        }
    }

    private void OnBubbleDurationValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BubbleDurationValueText != null)
            BubbleDurationValueText.Text = BubbleDurationSlider.Value.ToString("0.#");
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}