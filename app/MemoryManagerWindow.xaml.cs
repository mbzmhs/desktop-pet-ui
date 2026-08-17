using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using DesktopPetUi.Core;

namespace DesktopPetUi;

public sealed class MemoryEntry
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public string DisplayRole { get; set; } = "";
    public string Time { get; set; } = "";
}

public partial class MemoryManagerWindow : Window
{
    private readonly AppConfig _config;
    private readonly ChatPipeline _pipeline;
    private readonly Action _save;

    public MemoryManagerWindow(AppConfig config, ChatPipeline pipeline, Action save)
    {
        _config = config;
        _pipeline = pipeline;
        _save = save;
        InitializeComponent();
        _pipeline.HistoryChanged += RefreshFromPipeline;
        Closed += (_, _) => _pipeline.HistoryChanged -= RefreshFromPipeline;
        Refresh();
    }

    private void RefreshFromPipeline()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshFromPipeline));
            return;
        }
        Refresh();
    }

    private string CharacterName()
    {
        var name = _config.EffectiveCharacterName;
        return string.IsNullOrWhiteSpace(name) ? "宠物" : name;
    }

    private void Refresh()
    {
        var name = CharacterName();
        SummaryBox.Text = _pipeline.Summary ?? "";
        ManualAssistantLabel.Text = name;
        HistoryList.Items.Clear();
        foreach (var m in _pipeline.History)
        {
            var isUser = m.Role == "user";
            var ts = m.Timestamp.Year >= 2000 ? m.Timestamp.ToString("MM-dd HH:mm") : "";
            HistoryList.Items.Add(new MemoryEntry
            {
                Role = m.Role,
                Content = m.Content,
                DisplayRole = isUser ? "你" : (m.Role == "assistant" ? name : m.Role),
                Time = ts,
            });
        }
        CountText.Text = "历史记录（" + _pipeline.History.Count + " 条） · 当前角色：" + name;
        StatusText.Text = "";
    }

    private void ShowStatus(string text, bool ok = true)
    {
        StatusText.Text = text;
        StatusText.Foreground = new System.Windows.Media.SolidColorBrush(ok
            ? System.Windows.Media.Color.FromRgb(0x2E, 0x8B, 0x57)
            : System.Windows.Media.Color.FromRgb(0xE8, 0x6C, 0x6C));
    }

    private void OnDeleteEntry(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is MemoryEntry entry)
            HistoryList.Items.Remove(entry);
    }

    private void OnAddManual(object sender, RoutedEventArgs e)
    {
        var userText = ManualUserBox.Text.Trim();
        var assistantText = ManualAssistantBox.Text.Trim();
        if (userText.Length == 0 && assistantText.Length == 0)
        {
            ShowStatus("请输入至少一条对话内容", ok: false);
            return;
        }
        var now = DateTime.Now;
        if (userText.Length > 0)
            HistoryList.Items.Add(new MemoryEntry
            {
                Role = "user",
                Content = userText,
                DisplayRole = "你",
                Time = now.ToString("MM-dd HH:mm"),
            });
        if (assistantText.Length > 0)
            HistoryList.Items.Add(new MemoryEntry
            {
                Role = "assistant",
                Content = assistantText,
                DisplayRole = CharacterName(),
                Time = now.ToString("MM-dd HH:mm"),
            });
        ManualUserBox.Clear();
        ManualAssistantBox.Clear();
        ManualUserBox.Focus();
        HistoryList.ScrollIntoView(HistoryList.Items[HistoryList.Items.Count - 1]);
        ShowStatus("已添加，请点「保存修改」写入记忆");
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        var res = System.Windows.MessageBox.Show(
            "确定要清空当前角色（" + CharacterName() + "）的全部记忆吗？\n将同时删除摘要和所有历史记录，此操作不可撤销。",
            "清空记忆",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.Yes) return;
        _pipeline.SetSummary(null);
        _pipeline.SetHistory(Array.Empty<ChatMessage>());
        _save();
        Refresh();
        ShowStatus("已清空记忆");
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            _pipeline.SetSummary(SummaryBox.Text);
            var list = new List<ChatMessage>();
            foreach (var item in HistoryList.Items)
            {
                if (item is MemoryEntry en && !string.IsNullOrWhiteSpace(en.Content))
                {
                    var role = string.IsNullOrWhiteSpace(en.Role) ? "assistant" : en.Role.Trim();
                    list.Add(new ChatMessage { Role = role, Content = en.Content, Timestamp = DateTime.Now });
                }
            }
            _pipeline.SetHistory(list);
            _save();
            Refresh();
            ShowStatus("已保存到记忆文件");
        }
        catch (Exception ex)
        {
            ShowStatus("保存失败：" + ex.Message, ok: false);
        }
    }

    private async void OnCompress(object sender, RoutedEventArgs e)
    {
        if (_pipeline.History.Count == 0)
        {
            ShowStatus("没有可压缩的历史记录", ok: false);
            return;
        }
        CompressButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        ShowStatus("正在压缩记忆…");
        var ok = await _pipeline.CompressNowAsync();
        _save();
        Refresh();
        ShowStatus(ok ? "压缩完成：历史记录已合并为摘要" : "压缩失败", ok);
        CompressButton.IsEnabled = true;
        SaveButton.IsEnabled = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}