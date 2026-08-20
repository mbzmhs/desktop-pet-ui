using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

        /// <summary>角色列表项：Folder 为文件夹名（唯一标识），Display 为 character.json 的 name 显示名。</summary>
    private sealed class CharItem
    {
        public string Folder { get; init; } = "";
        public string Display { get; init; } = "";
        public override string ToString() => Display;
    }

    private void ReloadList()
    {
        CharacterList.Items.Clear();
        foreach (var folder in _config.ListCharacters())
        {
            CharacterList.Items.Add(new CharItem { Folder = folder, Display = _config.CharacterDisplayName(folder) });
        }
        var current = _config.Character.Current;
        CharacterList.SelectedItem = CharacterList.Items.OfType<CharItem>()
            .FirstOrDefault(x => string.Equals(x.Folder, current, StringComparison.OrdinalIgnoreCase));
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
        if (CharacterList.SelectedItem is CharItem item)
        {
            var name = item.Folder;
            _selected = name;
            LoadCharPreview(name);
            var profile = CharacterProfile.Load(ProfilePath(name));
            NameText.Text = _config.CharacterDisplayName(name);
            PromptBox.Text = profile.Llm.SystemPrompt ?? "";
            TemperatureBox.Text = profile.Llm.Temperature is double t ? t.ToString("0.###") : "0.7";
            MaxTokensBox.Text = profile.Llm.MaxTokens is int m ? m.ToString() : "";
            CharTextLangBox.SelectedIndex = CharTagIndex(CharTextLangBox, profile.Tts?.TextLang ?? "auto");
            CharTtsProviderBox.SelectedIndex = CharTagIndex(CharTtsProviderBox, profile.Tts?.Provider ?? "none");
            CharVoiceBox.Text = profile.Tts?.VoiceId ?? "";
            var charProv = (CharTtsProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "none";
            CharSpeedSlider.Value = Math.Clamp(profile.Tts?.SpeedFactor ?? 1.0, CharSpeedSlider.Minimum, CharSpeedSlider.Maximum);
            CharSpeedValueText.Text = CharSpeedSlider.Value.ToString("0.00");
            CharSpeedSlider.IsEnabled = !string.Equals(charProv, "none", StringComparison.OrdinalIgnoreCase);
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
        var currentDisplay = _config.EffectiveCharacterName;
        CurrentLabel.Text = string.IsNullOrEmpty(name)
            ? (string.IsNullOrEmpty(current) ? "" : "当前角色：" + currentDisplay)
            : (name == current ? "（当前角色）" : "当前角色：" + currentDisplay);
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
        UpdateScalePreview();
    }

    private void OnCharScaleValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CharScaleValueText != null)
            CharScaleValueText.Text = CharScaleSlider.Value.ToString("0.00");
        UpdateScalePreview();
    }

    /// <summary>把当前缩放滑条/继承选项实时应用到正在显示的立绘上，便于预览。仅当编辑的角色正是当前角色时生效。</summary>
    private void UpdateScalePreview()
    {
        if (_selected == null ||
            !string.Equals(_selected, _config.Character.Current, StringComparison.OrdinalIgnoreCase))
            return;
        var scale = CharScaleInheritCheck.IsChecked == true ? _config.Character.Scale : CharScaleSlider.Value;
        App.PreviewCharacterScale(scale);
    }

    private void OnCharSpeedValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CharSpeedValueText != null)
            CharSpeedValueText.Text = CharSpeedSlider.Value.ToString("0.00");
    }

    private int _charVoiceRefreshSeq;

    private async void OnCharTtsProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        var prov = (CharTtsProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "none";
        if (CharSpeedSlider != null)
            CharSpeedSlider.IsEnabled = !string.Equals(prov, "none", StringComparison.OrdinalIgnoreCase);
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
        var profile = CharacterProfile.Load(ProfilePath(_selected));
        if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = _selected;
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
        if (hasTts && !string.Equals(prov, "none", StringComparison.OrdinalIgnoreCase))
        {
            tts.SpeedFactor = CharSpeedSlider.Value;
            hasTts = true;
        }
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
        ContextMaxTokensBox.Text = _config.Chat.ContextMaxTokens.ToString();
        ArchiveMaxEntriesBox.Text = _config.Chat.ArchiveMaxEntries.ToString();
        ProactiveIntervalBox.Text = _config.Chat.ProactiveIntervalSec.ToString("0.###");
        IdleIntervalBox.Text = _config.Character.IdleIntervalSec.ToString("0.###");
        BubbleReserveBox.Text = _config.Character.BubbleReserve.ToString("0.###");
        ProactiveCheck.IsChecked = _config.Chat.Proactive;
        BuildAgentScreenChecks();
        CrossFadeCheck.IsChecked = _config.Character.CrossFade;
        ReadInnerThoughtsCheck.IsChecked = _config.Chat.ReadInnerThoughts;
        StreamEnabledCheck.IsChecked = _config.Chat.StreamEnabled;
        GlobalUserAddressBox.Text = _config.Chat.UserAddress;
        AgentCheck.IsChecked = _config.Chat.Agent.Enabled;
        AgentMaxStepsBox.Text = _config.Chat.Agent.MaxSteps.ToString();
        AgentPsTimeoutBox.Text = _config.Chat.Agent.PsTimeoutSec.ToString("0.#");
        AgentReadLinesBox.Text = _config.Chat.Agent.ReadFileMaxLines.ToString();
        // 工作目录留空时显示默认值（程序所在目录），保存后即变为显式配置
        AgentWorkDirBox.Text = string.IsNullOrWhiteSpace(_config.Chat.Agent.WorkDir)
            ? System.AppContext.BaseDirectory.TrimEnd('\\')
            : _config.Chat.Agent.WorkDir;
        AgentWorkDirPermCombo.SelectedIndex = TagIndex(AgentWorkDirPermCombo, _config.Chat.Agent.WorkDirPerm);
        AgentOtherDirPermCombo.SelectedIndex = TagIndex(AgentOtherDirPermCombo, _config.Chat.Agent.OtherDirPerm);
        PsAutoPolicyCombo.SelectedIndex = TagIndex(PsAutoPolicyCombo, _config.Chat.Agent.PsAutoPolicy);
        // 直接绑定配置里的活集合（ObservableCollection）：增删即时刷新 UI，且与确认弹窗的"信任该目录"共享同一实例
        var tdirs = _config.Chat.Agent.TrustedDirs ??= new ObservableCollection<string>();
        TrustedDirsList.ItemsSource = tdirs;
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
            var prev = (ApiModelBox.Text ?? "").Trim();
            ApiModelBox.Items.Clear();
            foreach (var m in models) ApiModelBox.Items.Add(m.Id);
            // 保持之前的选择；若它不在本次列表里（换了服务器/模型已删）则选第一个
            if (string.IsNullOrWhiteSpace(prev) || !models.Any(m => string.Equals(m.Id, prev, StringComparison.OrdinalIgnoreCase)))
                prev = models[0].Id;
            ApiModelBox.Text = prev;
            LlamaClient.StoreModelContext(baseUrl, prev, models); // 记录各模型自报的上下文上限
            var withCtx = models.Count(m => m.MaxContextTokens != null);
            ShowStatus("获取到 " + models.Count + " 个模型" + (withCtx > 0 ? $"（{withCtx} 个提供了上下文上限）" : "（该 API 未提供上下文上限，按设置预算运行）"));
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

            if (int.TryParse(ContextMaxTokensBox.Text?.Trim(), out var cmt) && cmt >= 0)
                _config.Chat.ContextMaxTokens = cmt;
            if (int.TryParse(ArchiveMaxEntriesBox.Text?.Trim(), out var ame) && ame >= 0)
                _config.Chat.ArchiveMaxEntries = ame;
            if (double.TryParse(ProactiveIntervalBox.Text?.Trim(), out var pi) && pi > 0)
                _config.Chat.ProactiveIntervalSec = pi;
            if (double.TryParse(IdleIntervalBox.Text?.Trim(), out var ii) && ii > 0)
                _config.Character.IdleIntervalSec = ii;
            if (double.TryParse(BubbleReserveBox.Text?.Trim(), out var br) && br >= 0)
                _config.Character.BubbleReserve = br;

            _config.Chat.Proactive = ProactiveCheck.IsChecked == true;
            _config.Character.CrossFade = CrossFadeCheck.IsChecked == true;
            _config.Chat.ReadInnerThoughts = ReadInnerThoughtsCheck.IsChecked == true;
            _config.Chat.StreamEnabled = StreamEnabledCheck.IsChecked == true;
            _config.Character.BubbleDurationSec = BubbleDurationSlider.Value;
            _config.Chat.UserAddress = GlobalUserAddressBox.Text?.Trim() ?? "";

            _config.Save();
            App.RefreshAll();
            var ep = _config.EffectiveLlm(); // 端点/模型可能刚改过：重新查询其自报的上下文上限
            LlamaClient.RefreshModelContextAsync(ep.Url, ep.Model, ep.ApiKey);
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

    private void OnSaveAgent(object sender, RoutedEventArgs e)
    {
        try
        {
            _config.Chat.Agent.Enabled = AgentCheck.IsChecked == true;
            if (int.TryParse(AgentMaxStepsBox.Text?.Trim(), out var ms) && ms >= 0)
                _config.Chat.Agent.MaxSteps = ms; // 0 = 不限步数
            if (double.TryParse(AgentPsTimeoutBox.Text?.Trim(), out var pts) && pts >= 5 && pts <= 300)
                _config.Chat.Agent.PsTimeoutSec = pts;
            if (int.TryParse(AgentReadLinesBox.Text?.Trim(), out var rml) && rml > 0)
                _config.Chat.Agent.ReadFileMaxLines = rml;
            _config.Chat.Agent.WorkDir = AgentWorkDirBox.Text?.Trim() ?? "";
            if (AgentWorkDirPermCombo.SelectedItem is ComboBoxItem wi)
                _config.Chat.Agent.WorkDirPerm = (wi.Tag as string) ?? "auto";
            if (AgentOtherDirPermCombo.SelectedItem is ComboBoxItem oi)
                _config.Chat.Agent.OtherDirPerm = (oi.Tag as string) ?? "auto";
            if (PsAutoPolicyCombo.SelectedItem is ComboBoxItem pp)
                _config.Chat.Agent.PsAutoPolicy = (pp.Tag as string) ?? "dual";
            // TrustedDirs 与 UI 是同一个集合，无需回写
            _config.Chat.Agent.AgentScreens = AgentScreensPanel.Children.OfType<System.Windows.Controls.CheckBox>()
                .Where(c => c.IsChecked == true)
                .Select(c => (int)c.Tag!)
                .ToList();

            _config.Save();
            App.RefreshAll();
            Log.Info("Agent settings saved");
            ShowStatus("Agent 设置已保存并生效");
        }
        catch (Exception ex)
        {
            Log.Error("Save agent settings failed", ex);
            ShowStatus("保存失败：" + ex.Message, ok: false);
        }
    }

    /// <summary>按当前检测到的显示器动态生成「Agent 观察屏幕」复选框（1-based，主屏标记）。</summary>
    private void BuildAgentScreenChecks()
    {
        AgentScreensPanel.Children.Clear();
        var selected = _config.Chat.Agent.AgentScreens ?? new List<int>();
        System.Windows.Forms.Screen[] screens;
        try { screens = System.Windows.Forms.Screen.AllScreens; }
        catch { return; }
        for (var i = 0; i < screens.Length; i++)
        {
            var idx = i + 1;
            var screen = screens[i];
            var dev = screen.DeviceName.Split('\\').Last();
            AgentScreensPanel.Children.Add(new System.Windows.Controls.CheckBox
            {
                Content = MakeScreenLabel(idx, screen, dev),
                IsChecked = selected.Contains(idx),
                Tag = idx,
                Margin = new Thickness(0, 0, 12, 4),
                Foreground = System.Windows.Media.Brushes.Black,
            });
        }
    }

    private static string MakeScreenLabel(int idx, System.Windows.Forms.Screen s, string name)
        => idx + (s.Primary ? "（主屏）" : "") + " " + name + " " + s.Bounds.Width + "×" + s.Bounds.Height;

    /// <summary>用 Win 自带目录选择框选工作目录。</summary>
    private void OnAgentWorkDirBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择工作目录" };
        var cur = AgentWorkDirBox.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(cur) && Directory.Exists(cur)) dlg.InitialDirectory = cur;
        if (dlg.ShowDialog(this) == true)
            AgentWorkDirBox.Text = dlg.FolderName;
    }

    private void OnTrustedDirAdd(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择信任目录" };
        if (dlg.ShowDialog(this) != true) return;
        var list = TrustedDirsList.ItemsSource as ObservableCollection<string>;
        if (list == null) return;
        if (!list.Any(d => string.Equals(d.Trim(), dlg.FolderName.Trim(), StringComparison.OrdinalIgnoreCase)))
            list.Add(dlg.FolderName);
    }

    private void OnTrustedDirRemove(object sender, RoutedEventArgs e)
    {
        if (TrustedDirsList.SelectedItem is string s)
            (TrustedDirsList.ItemsSource as ObservableCollection<string>)?.Remove(s);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}