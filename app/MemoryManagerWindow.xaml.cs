using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using DesktopPetUi.Core;
using DesktopPetUi.Core.Agent;
using Color = System.Windows.Media.Color;

namespace DesktopPetUi;

public sealed class MemoryEntry
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public string DisplayRole { get; set; } = "";
    public string Time { get; set; } = "";
    /// <summary>原始时间戳：保存时原样写回，避免聊天窗按时间戳合并排序时顺序错乱。</summary>
    public DateTime Ts { get; set; }
}

/// <summary>Agent 操作记录行的显示模型（区别于对话条目：裁定徽标 + 等宽详情）。</summary>
public sealed class MemoryOpEntry
{
    public string VerdictText { get; set; } = "";
    public Color BadgeColor { get; set; }
    public string ToolLabel { get; set; } = "";
    public string Time { get; set; } = "";
    public string Detail { get; set; } = "";
    public string NoteLine { get; set; } = "";
    public Visibility NoteVisible { get; set; } = Visibility.Collapsed;
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
        _pipeline.OpAdded += OnOpAddedAny; // 新操作裁定 → 刷新操作记录面板
        OnTabChanged(this, null!); // 初始按当前 tab 设置底部按钮
        Closed += (_, _) =>
        {
            _pipeline.HistoryChanged -= RefreshFromPipeline;
            _pipeline.OpAdded -= OnOpAddedAny;
        };
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

    private void OnOpAddedAny(AgentOpRecord _) => RefreshFromPipeline();

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
                Ts = m.Timestamp,
            });
        }
        CountText.Text = "对话历史（" + _pipeline.History.Count + " 条） · 当前角色：" + name;
        StatusText.Text = "";
        RefreshOps();
        RefreshArchive();
    }

    /// <summary>刷新「归档记录」tab：压缩时被摘要替代的原始记录（只读）。</summary>
    private void RefreshArchive()
    {
        var list = MemoryArchive.Load(_config);
        ArchiveList.Items.Clear();
        foreach (var m in list)
        {
            var isUser = m.Role == "user";
            var ts = m.Timestamp.Year >= 2000 ? m.Timestamp.ToString("MM-dd HH:mm") : "";
            ArchiveList.Items.Add(new MemoryEntry
            {
                Role = m.Role,
                Content = m.Content,
                DisplayRole = isUser ? "你" : (m.Role == "assistant" ? CharacterName() : m.Role),
                Time = ts,
                Ts = m.Timestamp,
            });
        }
        ArchiveCountText.Text = "归档记录（" + list.Count + " 条）";
    }

    /// <summary>从持久化日志（agent_ops.json）刷新操作记录面板，按时间倒序展示。</summary>
    private void RefreshOps()
    {
        var ops = AgentOpLog.Load(_config);
        OpsList.Items.Clear();
        foreach (var op in ops)
        {
            var (verdictText, badgeColor) = op.Verdict switch
            {
                "allowed" => ("✔ 已允许", Color.FromRgb(0x3A, 0x7A, 0x4A)),
                "denied" => ("✘ 已拒绝", Color.FromRgb(0x8A, 0x5A, 0x5A)),
                _ => ("⚙ 自动放行", Color.FromRgb(0x5A, 0x6A, 0x8A)),
            };
            var note = (op.Note ?? "").Trim();
            OpsList.Items.Add(new MemoryOpEntry
            {
                VerdictText = verdictText,
                BadgeColor = badgeColor,
                ToolLabel = op.Tool + (string.IsNullOrWhiteSpace(op.Title) ? "" : " · " + op.Title),
                Time = op.Ts.Year >= 2000 ? op.Ts.ToString("MM-dd HH:mm:ss") : "",
                Detail = string.IsNullOrWhiteSpace(op.Detail) ? op.Title : op.Detail,
                NoteLine = note.Length > 0 ? ("备注：" + note) : "",
                NoteVisible = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            });
        }
        OpsCountText.Text = "（" + ops.Count + " 条 · 持久化，重启后仍在）";
    }

    private void OnClearOps(object sender, RoutedEventArgs e)
    {
        var res = System.Windows.MessageBox.Show(
            "确定要清空当前角色的全部 Agent 操作记录吗？此操作不可撤销。",
            "清空操作记录",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.Yes) return;
        AgentOpLog.Clear(_config);
        RefreshOps();
        ShowStatus("已清空操作记录");
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
                Ts = now,
            });
        if (assistantText.Length > 0)
            HistoryList.Items.Add(new MemoryEntry
            {
                Role = "assistant",
                Content = assistantText,
                DisplayRole = CharacterName(),
                Time = now.ToString("MM-dd HH:mm"),
                Ts = now,
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
            "确定要清空当前角色（" + CharacterName() + "）的全部记忆吗？\n将同时删除摘要和所有历史记录（归档记录不受影响，请在「归档记录」tab 单独清除），此操作不可撤销。",
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
                    list.Add(new ChatMessage { Role = role, Content = en.Content, Timestamp = en.Ts.Year >= 2000 ? en.Ts : DateTime.Now }); // 保留原时间戳，聊天窗排序不乱
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

    /// <summary>归档 tab 下底部只留「清空归档记录」一个按钮；当前记忆 tab 恢复原有按钮组。</summary>
    private void OnTabChanged(object? sender, System.Windows.Controls.SelectionChangedEventArgs? e)
    {
        var isArchive = ReferenceEquals(MainTabs.SelectedItem, ArchiveTab);
        MainButtons.Visibility = isArchive ? Visibility.Collapsed : Visibility.Visible;
        ArchiveClearButton.Visibility = isArchive ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>归档记录的专用清除入口（「清空记忆」不动归档）。</summary>
    private void OnClearArchive(object sender, RoutedEventArgs e)
    {
        var res = System.Windows.MessageBox.Show(
            "确定要清空当前角色的全部归档记录吗？此操作不可撤销。",
            "清空归档记录",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (res != MessageBoxResult.Yes) return;
        MemoryArchive.Clear(_config);
        RefreshArchive();
        ShowStatus("已清空归档记录");
    }
}