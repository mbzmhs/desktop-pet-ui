using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopPetUi.Core;
using DesktopPetUi.Core.Agent;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using Button = System.Windows.Controls.Button;
using Border = System.Windows.Controls.Border;
using Orientation = System.Windows.Controls.Orientation;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Cursors = System.Windows.Input.Cursors;
using Rect = System.Windows.Rect;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace DesktopPetUi;

public partial class ChatWindow : Window
{
    private readonly AppConfig _config;
    private readonly ChatPipeline _pipeline;
    private readonly Func<Rect?> _petRect;

    private sealed class ConfirmCard
    {
        public ConfirmRequest Request = null!;
        public TaskCompletionSource<ConfirmResult> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FrameworkElement Card = null!; // 消息流里的活卡片（未决=按钮，已决=结果行）
        public bool Resolved;
    }

    private ConfirmCard? _pendingConfirm;
    private List<AgentOpRecord> _ops = new(); // agent 操作日志（磁盘加载 + 实时事件），仅 UI 线程读写
    private string? _opsChar;                 // 已加载操作日志所属角色（切换角色时自动重载）

    public ChatWindow(AppConfig config, ChatPipeline pipeline, Func<Rect?> petRect)
    {
        _config = config;
        _pipeline = pipeline;
        _petRect = petRect;
        InitializeComponent();

        Width = Math.Max(320, config.Chat.Ui.Width);
        Height = Math.Max(360, config.Chat.Ui.Height);
        Topmost = config.Chat.Ui.AlwaysOnTop;
        if (!double.IsNaN(config.Chat.Ui.X) && !double.IsNaN(config.Chat.Ui.Y))
        {
            Left = config.Chat.Ui.X;
            Top = config.Chat.Ui.Y;
        }
        else
        {
            // 首次打开：放到宠物附近，随后记住位置
            if (_petRect() is Rect r)
            {
                var v = GetVirtualScreen();
                Left = Math.Clamp(r.Left + r.Width / 2 - Width / 2, v.Left + 4, Math.Max(v.Left + 4, v.Right - Width - 4));
                Top = Math.Clamp(r.Top + r.Height / 2 - Height / 2, v.Top + 4, Math.Max(v.Top + 4, v.Bottom - Height - 4));
            }
            else
            {
                Left = -10000;
                Top = -10000;
            }
        }

        _pipeline.Status = SetStatus;
        _pipeline.HistoryChanged += RebuildMessages;
        _pipeline.OpAdded += OnOpAdded;
        _pipeline.UsageChanged += () => Dispatcher.Invoke(UpdateUsageLabel); // usage 在后台线程更新
        _pipeline.CompressingChanged += v => Dispatcher.Invoke(() => OnCompressing(v)); // 压缩期间提示+锁定输入
        SizeChanged += (s, e) => SaveLayout(); // 拖角缩放后落盘
        TitleText.Text = "和" + (string.IsNullOrEmpty(App.Config.EffectiveCharacterName) ? "宠物" : App.Config.EffectiveCharacterName) + "聊天";
        RebuildMessages();
    }

    /// <summary>显示并聚焦（位置使用记忆值，不再跟随宠物）。</summary>
    public void ShowForInput()
    {
        TitleText.Text = "和" + (string.IsNullOrEmpty(App.Config.EffectiveCharacterName) ? "宠物" : App.Config.EffectiveCharacterName) + "聊天";
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal; // 最小化状态下 Show() 只会在任务栏亮一下，需先还原
        ClampToScreen();
        Show();
        Activate();
        RebuildMessages();
        UpdateUsageLabel();
        if (_pipeline.IsRunning) return;
        InputBox.Focus();
        Keyboard.Focus(InputBox);
        SetStatus("");
    }

    // ---------------- 上下文占用显示 ----------------

    /// <summary>标题栏实时显示：最近一次真实 prompt_tokens / 设置里的 token 预算（≥70% 黄、≥90% 红）。</summary>
    private void UpdateUsageLabel()
    {
        if (UsageText == null) return;
        var used = _pipeline.LastPromptTokens;
        var budget = _pipeline.EffectiveContextBudget(); // 用户设置 ∩ 模型实际上限
        if (used <= 0 || budget <= 0)
        {
            UsageText.Visibility = Visibility.Collapsed;
            return;
        }
        var pct = used / (double)budget * 100.0;
        UsageText.Text = $"Context ≈{FmtTok(used)}/{FmtTok(budget)} · {pct:0}%";
        UsageText.Foreground = new SolidColorBrush(pct >= 90 ? Color.FromRgb(0xE0, 0x60, 0x50)
                                    : pct >= 70 ? Color.FromRgb(0xD8, 0xA8, 0x4A)
                                    : Color.FromRgb(0x7A, 0xA0, 0x80));
        UsageText.Visibility = Visibility.Visible;
    }

    private static string FmtTok(int t) => t >= 1000 ? (Math.Round(t / 100.0) / 10.0).ToString("0.#") + "k" : t.ToString();

    // ---------------- 压缩期间锁定输入 ----------------

    /// <summary>历史压缩进行中：状态行提示并禁用输入/发送；结束后若管线仍在跑则回到"思考中"。</summary>
    private void OnCompressing(bool compressing)
    {
        InputBox.IsEnabled = !compressing;
        SendBtn.IsEnabled = !compressing;
        if (compressing)
        {
            SetStatus("正在整理记忆（压缩历史）…");
        }
        else
        {
            SetStatus(_pipeline.IsRunning ? "思考中…" : "");
        }
    }

    // ---------------- 位置/尺寸记忆 ----------------

    /// <summary>标题栏拖拽由 WindowChrome 原生处理（含双击最大化/还原），松开后落盘位置。</summary>
    private void OnTitleBarMouseUp(object sender, MouseButtonEventArgs e) => SaveLayout();

    // ---------------- 最小化 / 最大化-还原（与正常 Windows 窗体一致） ----------------

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnTodoClick(object sender, RoutedEventArgs e) => App.ShowTodoWindow();

    private void OnMaxRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        try
        {
            if (MaxBtn != null)
                MaxBtn.Content = WindowState == WindowState.Maximized ? "" : ""; // 还原 □ / 最大化 ❐（Segoe MDL2）
        }
        catch { /* 忽略 */ }
        if (WindowState == WindowState.Normal)
            ClampToScreen(); // 还原后确保回到屏幕可见区域
    }

    private void SaveLayout()
    {
        if (WindowState != WindowState.Normal) return; // 最大化/最小化几何不入库，还原后仍回记忆位置
        _config.Chat.Ui.X = Left;
        _config.Chat.Ui.Y = Top;
        _config.Chat.Ui.Width = Width;
        _config.Chat.Ui.Height = Height;
        try { _config.Save(); } catch { /* 忽略保存失败 */ }
    }

    private void ClampToScreen()
    {
        var v = GetVirtualScreen();
        Left = Math.Clamp(Left, v.Left + 4, Math.Max(v.Left + 4, v.Right - Math.Min(Width, 120)));
        Top = Math.Clamp(Top, v.Top + 4, Math.Max(v.Top + 4, v.Bottom - Math.Min(Height, 80)));
    }

    private Rect GetVirtualScreen()
    {
        try
        {
            var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
            var dpi = VisualTreeHelper.GetDpi(this);
            var s = dpi.DpiScaleX;
            return new Rect(vs.Left / s, vs.Top / s, vs.Width / s, vs.Height / s);
        }
        catch
        {
            var wa = SystemParameters.WorkArea;
            return new Rect(wa.Left, wa.Top, wa.Width, wa.Height);
        }
    }

    // ---------------- 消息流 ----------------

    private void RebuildMessages()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RebuildMessages));
            return;
        }
        var name = string.IsNullOrEmpty(App.Config.EffectiveCharacterName) ? "宠物" : App.Config.EffectiveCharacterName;

        // 角色切换后重新加载该角色的持久化操作日志（agent_ops.json）
        var curChar = _config.Character?.Current ?? "";
        if (curChar != _opsChar)
        {
            _opsChar = curChar;
            _ops = new List<AgentOpRecord>(AgentOpLog.Load(_config));
        }

        MsgPanel.Children.Clear();

        // 历史消息 + agent 操作记录，按时间戳合并排序（工具往返也进历史了：协议消息渲染成紧凑行，不占气泡）
        var history = _pipeline.History; // 加锁快照
        var firstTs = history.Count > 0 ? history.Min(m => m.Timestamp) : DateTime.MaxValue;
        var items = new List<(DateTime Ts, FrameworkElement El)>();
        foreach (var m in history)
        {
            var isUser = m.Role == "user";
            var el = IsProtocolMessage(m)
                ? BuildExchangeLine(m)
                : BuildBubble(isUser, isUser ? "你" : name, m.Timestamp, ToDisplay(m.Content));
            items.Add((m.Timestamp, el));
        }
        // 早于第一条聊天记录的 agent 操作不进窗（清空记录后不残留一排孤行）；数据仍在 agent_ops.json，记忆管理器可见可清
        foreach (var op in _ops.Where(o => o.Ts >= firstTs))
            items.Add((op.Ts, BuildOpLine(op)));
        // 稳定排序：时间戳相同（同一毫秒的工具往返）保持插入顺序
        var ordered = items.Select((it, i) => new { it.El, Ts = it.Ts, I = i })
                           .OrderBy(x => x.Ts).ThenBy(x => x.I)
                           .ToList();
        foreach (var o in ordered) MsgPanel.Children.Add(o.El);

        if (_pendingConfirm != null && !_pendingConfirm.Resolved)
            MsgPanel.Children.Add(_pendingConfirm.Card); // 未决确认卡片保持在消息流末尾
        ScrollToEnd();
    }

    /// <summary>剥离情绪标签（内置 + TTS 自定义），避免 [happy] 之类出现在显示文本里。</summary>
    private string ToDisplay(string? content)
    {
        var s = content ?? "";
        if (s.Length == 0) return "";
        var tags = new List<string>(ChatEmotion.Emotions);
        if (_pipeline.AvailableEmotions != null)
            foreach (var t in _pipeline.AvailableEmotions)
                if (!tags.Any(x => x.Equals(t, StringComparison.OrdinalIgnoreCase))) tags.Add(t);
        if (tags.Count == 0) return s;
        var rx = new Regex(@"\[(?:" + string.Join("|", tags.Select(Regex.Escape)) + @")\]", RegexOptions.Compiled);
        s = rx.Replace(s, " ");
        return Regex.Replace(s, "[ \\t]{2,}", " ").Trim();
    }

    /// <summary>QQ 风格气泡：用户=右侧蓝底，角色=左侧灰底；下方小字时间戳。</summary>
    private FrameworkElement BuildBubble(bool isUser, string who, DateTime ts, string displayText)
    {
        var body = MarkdownRenderer.Render(displayText);
        var bubble = new Border
        {
            Background = isUser ? MakeBrush("#3D7EBF") : MakeBrush("#34383F"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 7, 10, 7),
            Child = body,
        };
        var stack = new StackPanel
        {
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = Math.Max(220, MsgPanel.ActualWidth * 0.82),
            Margin = new Thickness(0, 5, 0, 5),
        };
        stack.Children.Add(bubble);
        var tsText = ts.Year >= 2000 ? who + "  " + ts.ToString("MM-dd HH:mm") : who;
        stack.Children.Add(new TextBlock
        {
            Text = tsText,
            FontSize = 10.5,
            Foreground = MakeBrush("#777"),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(2, 2, 2, 0),
        });
        return stack;
    }

    /// <summary>agent 操作裁定事件（后台线程触发）：追加到日志并重建消息流，操作行按时间戳落回原位置。</summary>
    private void OnOpAdded(AgentOpRecord rec)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnOpAdded(rec)));
            return;
        }
        _ops.Add(rec);
        RebuildMessages();
    }

    // ---------------- 窗口内权限确认 ----------------

    /// <summary>
    /// 聊天窗可见时接管权限确认（宠物气泡不再弹出）；不可见/已有未决确认返回 null，调用方回退到宠物气泡。
    /// </summary>
    public Task<ConfirmResult>? TryShowConfirmAsync(ConfirmRequest req)
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.Invoke(() => TryShowConfirmAsync(req)); // 窗口属性必须在 UI 线程读
        if (!IsVisible || _pendingConfirm != null) return null;

        var card = new ConfirmCard { Request = req };
        BuildConfirmCard(card);
        _pendingConfirm = card;
        MsgPanel.Children.Add(card.Card);
        ScrollToEnd();
        Activate(); // 提亮窗口提醒用户
        return card.Tcs.Task;
    }

    private void BuildConfirmCard(ConfirmCard card)
    {
        var req = card.Request;
        var root = new StackPanel
        {
            MaxWidth = Math.Max(260, MsgPanel.ActualWidth * 0.92),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 8),
        };

        var border = new Border
        {
            Background = MakeBrush("#2A2D33"),
            BorderBrush = RiskBrush(req.Risk),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
        };

        var inner = new StackPanel { Orientation = Orientation.Vertical };

        // 标题行 + 风险徽标
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var badge = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 1.5, 8, 1.5),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Background = RiskBadgeBrush(req.Risk),
        };
        badge.Child = new TextBlock { Text = RiskLabel(req.Risk), FontSize = 12, Foreground = Brushes.White };
        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(req.Title) ? "权限确认" : req.Title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = MakeBrush("#EEE"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(badge, 0);
        Grid.SetColumn(title, 1);
        titleRow.Children.Add(badge);
        titleRow.Children.Add(title);
        inner.Children.Add(titleRow);

        if (!string.IsNullOrWhiteSpace(req.RiskNote))
            inner.Children.Add(new TextBlock
            {
                Text = "风险说明：" + req.RiskNote,
                FontSize = 12,
                Foreground = MakeBrush("#999"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });

        if (!string.IsNullOrWhiteSpace(req.Detail))
            inner.Children.Add(new Border
            {
                Background = MakeBrush("#26282C"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 7, 0, 0),
                Child = new TextBlock
                {
                    Text = CapText(req.Detail, 2000),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12.5,
                    Foreground = MakeBrush("#D8DEE4"),
                    LineHeight = 17,
                    TextWrapping = TextWrapping.Wrap,
                },
            });

        // 按钮行
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(MakeConfirmButton("确认", "#3A7A4A", () => ResolveConfirm(card, allowed: true, trust: false)));
        buttons.Children.Add(MakeConfirmButton("取消", "#8A5A5A", () => ResolveConfirm(card, allowed: false, trust: false), leftMargin: 8));
        if (!string.IsNullOrWhiteSpace(req.TrustableDir))
            buttons.Children.Add(MakeConfirmButton("信任该目录", "#5A6A8A", () => ResolveConfirm(card, allowed: true, trust: true), leftMargin: 8));
        inner.Children.Add(buttons);

        border.Child = inner;
        root.Children.Add(border);
        card.Card = root;
    }

    private Button MakeConfirmButton(string text, string bgHex, Action onClick, double leftMargin = 0)
    {
        var b = new Button
        {
            Content = text,
            Background = MakeBrush(bgHex),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(14, 4, 14, 4),
            Margin = new Thickness(leftMargin, 0, 0, 0),
            Cursor = Cursors.Hand,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    private void ResolveConfirm(ConfirmCard card, bool allowed, bool trust)
    {
        if (card.Resolved) return;
        card.Resolved = true;
        try { card.Card.IsEnabled = false; } catch { } // 点下任意选项的瞬间整卡失效，其余按钮立即不可再点

        var doTrust = trust && allowed && !string.IsNullOrWhiteSpace(card.Request.TrustableDir);
        if (doTrust)
        {
            // 与宠物气泡 OnConfirmTrust 一致：把目标所在目录真正写入信任名单并落盘
            var dir = card.Request.TrustableDir!;
            try
            {
                var list = _config.Chat.Agent.TrustedDirs ??= new System.Collections.ObjectModel.ObservableCollection<string>();
                if (!list.Any(d => string.Equals((d ?? "").Trim(), dir.Trim(), StringComparison.OrdinalIgnoreCase)))
                    list.Add(dir);
                _config.Save();
                Log.Info("Agent trust dir added: " + dir);
            }
            catch (Exception ex) { Log.Error("Add trusted dir failed", ex); }
        }

        card.Tcs.TrySetResult(new ConfirmResult { Allowed = allowed, TrustFolder = doTrust });
        if (_pendingConfirm == card) _pendingConfirm = null;

        // 操作日志事件（OpAdded → RebuildMessages）会紧接着把此卡片替换为规范操作行并持久化，无需手动收敛
        ScrollToEnd();
    }

    /// <summary>窗口关闭/隐藏时若有未决确认，按拒绝处理（与宠物气泡超时语义一致）。</summary>
    private void RejectPendingConfirm()
    {
        var card = _pendingConfirm;
        if (card == null || card.Resolved) return;
        ResolveConfirm(card, allowed: false, trust: false);
    }

    // ---------------- 输入 ----------------

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            RejectPendingConfirm();
            Hide();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            Submit();
        }
    }

    private void OnSendClick(object sender, RoutedEventArgs e) => Submit();

    private void Submit()
    {
        var text = InputBox.Text?.Trim();
        // 压缩期间输入框已禁用，这里再兜底一次（例如 Enter 事件先于 IsEnabled 生效的边界）
        if (string.IsNullOrEmpty(text) || _pipeline.IsRunning || _pipeline.IsCompressing) return;
        InputBox.Clear();
        _ = RunAsync(text);
    }

    private async Task RunAsync(string text)
    {
        SetStatus("发送中…");
        await _pipeline.RunAsync(text, App.PetWindow!);
        UpdateStopButton(); // 兜底：停止/出错路径的 Status 回调可能先于 IsRunning=false
        // QQ 式常驻窗口：回复后不自动隐藏，Esc/关闭按钮收起
    }

    private void SetStatus(string msg)
    {
        // 确认点击后 agent 续体在线程池线程上跑，Status 可能从后台线程回调
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => { StatusText.Text = msg; UpdateStopButton(); }));
            return;
        }
        StatusText.Text = msg;
        UpdateStopButton();
    }

    /// <summary>运行中显示红色"停止"按钮；管线结束后隐藏（RunAsync 返回时也兜底刷新一次）。</summary>
    private void UpdateStopButton()
    {
        if (StopBtn == null) return;
        StopBtn.Visibility = _pipeline.IsRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => _pipeline.Stop();

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        RejectPendingConfirm(); // 收起窗口时未决确认按拒绝（与宠物气泡超时语义一致）
        Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        RejectPendingConfirm();
        base.OnClosed(e);
    }



    // ---------------- 小工具 ----------------

    private static string RiskLabel(string risk) => risk switch
    {
        "low" => "低风险",
        "medium" => "中风险",
        "high" => "高风险",
        _ => "需确认",
    };

    private static Brush RiskBadgeBrush(string risk) => risk switch
    {
        "low" => MakeBrush("#3A7A4A"),
        "medium" => MakeBrush("#B07D2E"),
        "high" => MakeBrush("#B03A3A"),
        _ => MakeBrush("#5A6A8A"),
    };

    private static Brush RiskBrush(string risk) => risk switch
    {
        "low" => MakeBrush("#446B50"),
        "medium" => MakeBrush("#7A5C2E"),
        "high" => MakeBrush("#8A4444"),
        _ => MakeBrush("#4A5568"),
    };

    /// <summary>是否 agent 协议消息（[tool] 调用 / [result] [error] [note] 反馈 / [system] 系统标记），区别于普通对话。</summary>
    private static bool IsProtocolMessage(ChatMessage m)
    {
        var c = (m.Content ?? "").TrimStart();
        if (m.Role == "user")
            return c.StartsWith("[result]", StringComparison.OrdinalIgnoreCase)
                || c.StartsWith("[error]", StringComparison.OrdinalIgnoreCase)
                || c.StartsWith("[note]", StringComparison.OrdinalIgnoreCase);
        return c.IndexOf("[tool]", StringComparison.OrdinalIgnoreCase) >= 0
            || c.StartsWith("[system]", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>工具往返紧凑行：小字号等宽、低对比，贴在消息流里不喧宾夺主。</summary>
    private FrameworkElement BuildExchangeLine(ChatMessage m)
    {
        var c = (m.Content ?? "").Trim();
        string prefix, text;
        if (m.Role == "user")
        {
            if (c.StartsWith("[note]", StringComparison.OrdinalIgnoreCase)) { prefix = "◈"; text = c.Substring(6).Trim(); }
            else if (c.StartsWith("[error]", StringComparison.OrdinalIgnoreCase)) { prefix = "!"; text = c.Substring(7).Trim(); }
            else if (c.StartsWith("[result]", StringComparison.OrdinalIgnoreCase)) { prefix = "↩"; text = c.Substring(8).Trim(); }
            else { prefix = "↩"; text = c; }
        }
        else if (c.StartsWith("[system]", StringComparison.OrdinalIgnoreCase))
        {
            prefix = "✦"; // 系统信息（如手动停止标记）：紧凑行，不占宠物气泡
            text = c.Substring(9).Trim();
        }
        else
        {
            var nm = Regex.Match(c, "\"name\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            prefix = "⚙";
            text = nm.Success ? nm.Groups[1].Value : c;
        }
        return new TextBlock
        {
            Text = prefix + " " + CapText(text, 120).Replace("\n", " ⏎ "),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = MakeBrush(m.Role == "user" ? "#667080" : "#8A93A0"),
            MaxWidth = Math.Max(240, MsgPanel.ActualWidth * 0.9),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 0, 1),
        };
    }

    /// <summary>agent 操作记录行（区别于对话气泡）：裁定徽标 + 等宽命令详情 + 时间戳，灰底小卡。</summary>
    private FrameworkElement BuildOpLine(AgentOpRecord op)
    {
        var (badgeText, badgeBgHex) = op.Verdict switch
        {
            "allowed" => ("✔ 已允许", "#3A7A4A"),
            "denied" => ("✘ 已拒绝", "#8A5A5A"),
            _ => ("⚙ 自动放行", "#5A6A8A"),
        };
        var note = string.IsNullOrWhiteSpace(op.Note) ? "" : "（" + op.Note + "）";
        var detail = CapText(string.IsNullOrWhiteSpace(op.Detail) ? op.Title : op.Detail, 200).Replace("\n", " ⏎ ");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 1.5, 8, 1.5),
            Background = MakeBrush(badgeBgHex),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        badge.Child = new TextBlock { Text = badgeText + note, FontSize = 11.5, Foreground = Brushes.White };
        Grid.SetColumn(badge, 0);

        var detailText = new TextBlock
        {
            Text = (string.IsNullOrWhiteSpace(op.Title) ? "" : op.Title + " · ") + detail,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = MakeBrush("#9AA3AC"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(detailText, 1);

        var ts = new TextBlock
        {
            Text = op.Ts.Year >= 2000 ? op.Ts.ToString("MM-dd HH:mm") : "",
            FontSize = 10.5,
            Foreground = MakeBrush("#667"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(ts, 2);

        grid.Children.Add(badge);
        grid.Children.Add(detailText);
        grid.Children.Add(ts);

        return new Border
        {
            Background = MakeBrush("#26282C"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 5, 10, 5),
            MaxWidth = Math.Max(260, MsgPanel.ActualWidth * 0.92),
            Margin = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = grid,
        };
    }

    private static string CapText(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "\n…（已截断）");

    private static Brush MakeBrush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private void ScrollToEnd()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            MsgScroll.ScrollToEnd();
            if (MsgPanel.Children.Count > 0)
                MsgScroll.UpdateLayout();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }
}
