using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DesktopPetUi.Core;

namespace DesktopPetUi;

public partial class CharacterSettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly PetWindow? _window;
    private readonly List<string> _characters = new();
    private bool _loading;

    public CharacterSettingsWindow(AppConfig config, PetWindow? window)
    {
        _config = config;
        _window = window;
        InitializeComponent();

        _characters.AddRange(_config.ListCharacters());
        if (_characters.Count == 0)
            _characters.Add(string.IsNullOrWhiteSpace(_config.Character.Current) ? "鲸鱼娘" : _config.Character.Current);
        CharacterBox.ItemsSource = _characters;

        var current = _config.Character.Current ?? "";
        var idx = _characters.FindIndex(c => string.Equals(c, current, StringComparison.OrdinalIgnoreCase));
        CharacterBox.SelectedIndex = Math.Max(0, idx);
        LoadProfile(_characters[Math.Max(0, idx)]);
    }

    private static string ProfilePath(AppConfig config, string name) =>
        Path.Combine(config.CharacterDir, name, "character.json");

    private void OnCharacterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CharacterBox.SelectedItem is string name) LoadProfile(name);
    }

    private void LoadProfile(string name)
    {
        _loading = true;
        try
        {
            var profile = CharacterProfile.Load(ProfilePath(_config, name));
            var tts = profile.Tts;
            SelectByTag(ProviderBox, tts?.Provider ?? "gptsovits");
            UrlBox.Text = tts?.Url ?? _config.Chat.Tts.Url;
            VoiceIdBox.Text = tts?.VoiceId ?? "";
            SelectByTag(TextLangBox, tts?.TextLang ?? "auto");
            EmotionBox.Text = tts?.Emotion ?? _config.Chat.Tts.Emotion;
            StreamingCheck.IsChecked = tts?.Streaming ?? _config.Chat.Tts.Streaming;
            var speed = tts?.SpeedFactor ?? _config.Chat.Tts.SpeedFactor;
            SpeedSlider.Value = Math.Clamp(speed, SpeedSlider.Minimum, SpeedSlider.Maximum);
            SpeedValueText.Text = SpeedSlider.Value.ToString("0.00");
            RefreshProviderUi();
        }
        finally
        {
            _loading = false;
        }
    }

    private static void SelectByTag(System.Windows.Controls.ComboBox box, string tag)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem it && string.Equals(it.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e) => RefreshProviderUi();

    private void RefreshProviderUi()
    {
        var provider = (ProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "gptsovits";
        var enabled = !string.Equals(provider, "none", StringComparison.OrdinalIgnoreCase);
        UrlBox.IsEnabled = string.Equals(provider, "gptsovits", StringComparison.OrdinalIgnoreCase);
        VoiceIdBox.IsEnabled = enabled;
        TextLangBox.IsEnabled = enabled;
        EmotionBox.IsEnabled = enabled;
        StreamingCheck.IsEnabled = string.Equals(provider, "gptsovits", StringComparison.OrdinalIgnoreCase);
        SpeedSlider.IsEnabled = enabled;
    }

    private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || SpeedValueText == null) return;
        SpeedValueText.Text = SpeedSlider.Value.ToString("0.00");
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            if (CharacterBox.SelectedItem is not string name || string.IsNullOrWhiteSpace(name))
            {
                ShowStatus("请选择角色", false);
                return;
            }
            var path = ProfilePath(_config, name);
            var profile = CharacterProfile.Load(path);
            var provider = (ProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "gptsovits";
            profile.Tts ??= new CharacterTtsConfig();
            profile.Tts.Provider = provider;
            profile.Tts.Url = string.Equals(provider, "gptsovits", StringComparison.OrdinalIgnoreCase) &&
                              !string.IsNullOrWhiteSpace(UrlBox.Text)
                ? UrlBox.Text.Trim()
                : null;
            profile.Tts.VoiceId = string.IsNullOrWhiteSpace(VoiceIdBox.Text) ? null : VoiceIdBox.Text.Trim();
            profile.Tts.TextLang = (TextLangBox.SelectedItem as ComboBoxItem)?.Tag as string;
            profile.Tts.Emotion = string.IsNullOrWhiteSpace(EmotionBox.Text) ? null : EmotionBox.Text.Trim();
            profile.Tts.Streaming = string.Equals(provider, "gptsovits", StringComparison.OrdinalIgnoreCase)
                ? StreamingCheck.IsChecked == true
                : null;
            profile.Tts.SpeedFactor = string.Equals(provider, "none", StringComparison.OrdinalIgnoreCase)
                ? null
                : SpeedSlider.Value;
            profile.Save(path);

            if (string.Equals(name, _config.Character.Current, StringComparison.OrdinalIgnoreCase))
            {
                _config.LoadActiveCharacter();
                _config.Save();
                App.RefreshAll();
            }
            ShowStatus("已保存：角色「" + name + "」语速 " + SpeedSlider.Value.ToString("0.00"));
        }
        catch (Exception ex)
        {
            Log.Error("CharacterSettings save failed", ex);
            ShowStatus("保存失败：" + ex.Message, false);
        }
    }

    private void ShowStatus(string text, bool ok = true)
    {
        StatusText.Text = text;
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(ok
            ? System.Windows.Media.Color.FromRgb(0x2E, 0x8B, 0x57)
            : System.Windows.Media.Color.FromRgb(0xE8, 0x6C, 0x6C));
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}