using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
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
using TextBox = System.Windows.Controls.TextBox;
using WrapPanel = System.Windows.Controls.WrapPanel;
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

    private sealed class AskCard
    {
        public AskRequest Request = null!;
        public TaskCompletionSource<AskResult> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FrameworkElement Card = null!;
        public bool Resolved;
        public List<List<int>> Selections = new(); // 每问已选选项下标
        public List<TextBox> Inputs = new();       // 每问自由输入框
    }

    private ConfirmCard? _pendingConfirm;
    private AskCard? _pendingAsk;
    // —— 流式打字气泡（仅展示层；TTS/历史仍按整条处理）——
    private StreamTagFilter? _streamFilter; // 非 null=正在流式接收
    private string _streamRaw = "";        // 已收到的累计原始全文（算增量用）
    private string _streamShown = "";      // 气泡里已累计的显示文本（每片是"新释放部分"，必须追加而非替换）
    private FrameworkElement? _streamBubble; // 临时气泡元素（RebuildMessages 会清面板，需按需重挂）
    private TextBlock? _streamText;   // 纯文本正文：增量 .Text 更新（不整树重渲染→不闪）；Markdown 正式版只由历史重建换入
    private TextBlock? _streamHeader; // "输入中…" → [tool] 收尾后改 名字+时间戳
    private bool _streamFrozen;             // 被 [tool] 抑制/出错后冻结：保留已显示文本（去光标），等正式版落历史替换
    private DateTime _frozenTs;             // 冻结时刻：重建时按它参与时间戳排序，气泡停在正确位置而不是面板末尾
    private List<AgentOpRecord> _ops = new(); // agent 操作日志（磁盘加载 + 实时事件），仅 UI 线程读写
    private readonly HashSet<string> _expanded = new(); // 已展开的工具返回行（按消息全文为键，RebuildMessages 后状态不丢）
    private string? _opsChar;                 // 已加载操作日志所属角色（切换角色时自动重载）
    // 消息元素缓存：内容没变的消息复用已渲染的 DOM（Markdown 重渲染是每轮重建的主要耗时），
    // 键=角色|时间戳|全文|展开态；上限防膨胀，换角色整体清空。
    private readonly Dictionary<string, FrameworkElement> _msgElCache = new();

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
        // 停止按钮跟随 IsRunning：末次 Status("") 发出时 IsRunning 尚为 true，真正结束靠 finally 的这个事件——
        // 插件触发的轮次（SendEventAsync）没有调用方兜底刷新，缺了它按钮会残留且点击无效
        _pipeline.RunningChanged += _ => Dispatcher.BeginInvoke(new Action(UpdateStopButton));
        _pipeline.HistoryChanged += () => RebuildMessages();
        _pipeline.ReplyDelta += OnReplyDelta; // 流式打字（后台线程触发）
        _pipeline.ReplyStreamEnd += OnReplyStreamEnd;
        _pipeline.OpAdded += OnOpAdded;
        _pipeline.UsageChanged += () => Dispatcher.Invoke(UpdateUsageLabel); // usage 在后台线程更新
        _pipeline.CompressingChanged += v => Dispatcher.Invoke(() => OnCompressing(v)); // 压缩期间提示+锁定输入
        SizeChanged += OnLiveSizeChanged; // 拖动中检测到尺寸变化→隐藏消息面板（免逐像素重排）；松手后统一重建+落盘
        TitleText.Text = "和" + (string.IsNullOrEmpty(App.Config.EffectiveCharacterName) ? "宠物" : App.Config.EffectiveCharacterName) + "聊天";
        RebuildMessages();
    }

    /// <summary>切换角色后刷新标题栏：角色名 + Context 占用（新角色尚无 usage 时自动隐藏，等下次请求结果）。</summary>
    public void RefreshCharacterTitle()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(RefreshCharacterTitle); return; }
        TitleText.Text = "和" + (string.IsNullOrEmpty(App.Config.EffectiveCharacterName) ? "宠物" : App.Config.EffectiveCharacterName) + "聊天";
        UpdateUsageLabel(); // Restore 已把 LastPromptTokens 归零 → used<=0 时隐藏标签
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

    private void OnJobClick(object sender, RoutedEventArgs e) => App.ShowJobWindow();

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

    // —— 拖动缩放优化：Win32 WM_ENTERSIZEMOVE/EXITSIZEMOVE 标记拖动区间。
    // 拖动中消息面板整体隐藏（几十个换行 TextBlock 逐像素重排是卡顿主因），松手后按新宽度重建一次并落盘一次；
    // 纯移动（宽高不变）不隐藏，避免每次挪窗口都闪一下。
    private bool _sizeMove;
    private double _smW, _smH;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource src)
            src.AddHook(SizeMoveHook);
    }

    private IntPtr SizeMoveHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x00A0) // WM_ENTERSIZEMOVE（移动或缩放开始）
        {
            _sizeMove = true;
            _smW = Width;
            _smH = Height;
            MsgPanel.Visibility = Visibility.Visible; // 复位（上次异常退出可能残留隐藏态）
        }
        else if (msg == 0x00A2) // WM_EXITSIZEMOVE（松手）
        {
            if (!_sizeMove) return IntPtr.Zero;
            _sizeMove = false;
            var resized = Math.Abs(Width - _smW) > 0.5 || Math.Abs(Height - _smH) > 0.5;
            MsgPanel.Visibility = Visibility.Visible;
            if (resized)
            {
                _msgElCache.Clear(); // 缓存元素的 MaxWidth 是按旧宽度烘焙的，换宽后必须重渲染
                RebuildMessages(scrollToEnd: false); // 松手后才重新计算渲染（保持当前滚动位置）
            }
            SaveLayout(); // 整个拖动只落盘一次（原来每个像素都写整份 config）
        }
        return IntPtr.Zero;
    }

    private void OnLiveSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 拖动中且尺寸真的在变（区别于纯移动）：隐藏消息面板跳过逐像素布局
        if (_sizeMove && (Math.Abs(Width - _smW) > 0.5 || Math.Abs(Height - _smH) > 0.5) && MsgPanel.Visibility == Visibility.Visible)
            MsgPanel.Visibility = Visibility.Collapsed;
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

    /// <param name="scrollToEnd">false=原地重建（如点击展开/收起工具返回），不滚动到最底部。</param>
    private void RebuildMessages(bool scrollToEnd = true)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => RebuildMessages(scrollToEnd)));
            return;
        }
        var name = string.IsNullOrEmpty(App.Config.EffectiveCharacterName) ? "宠物" : App.Config.EffectiveCharacterName;

        // 角色切换后重新加载该角色的持久化操作日志（agent_ops.json）
        var curChar = _config.Character?.Current ?? "";
        if (curChar != _opsChar)
        {
            _opsChar = curChar;
            _ops = new List<AgentOpRecord>(AgentOpLog.Load(_config));
            _msgElCache.Clear(); // 换角色：消息与操作日志都换了，缓存全失效
        }
        if (_msgElCache.Count > 600) _msgElCache.Clear(); // 上限兜底（历史被压缩/清空后旧键不会再来）

        MsgPanel.Children.Clear();

        // 历史消息 + agent 操作记录，按时间戳合并排序（工具往返也进历史了：协议消息渲染成紧凑行，不占气泡）
        var history = _pipeline.History; // 加锁快照
        var firstTs = history.Count > 0 ? history.Min(m => m.Timestamp) : DateTime.MaxValue;
        var items = new List<(DateTime Ts, FrameworkElement El, string Kind)>();
        foreach (var m in history)
        {
            // 系统生成的消息不显示为对话文字：[SYSTEM] user 触发（沉默指令/事件触发）+ [SKIP] 跳过占位标记
            if (IsHiddenSystemRelay(m) || IsSkipMarker(m)) continue;
            var isUser = m.Role == "user";
            // 第三方事件（直播间弹幕等，Role="event"）：独立紧凑行，与用户蓝气泡/角色灰气泡明确区分
            var content = m.Content ?? "";
            var isEvent = m.Role == "event";
            // 缓存键含展开态：[result]/[error] 折叠/切换展开时重建为不同元素
            var key = (isUser ? "u" : "a") + "\u0001" + m.Timestamp.Ticks + "\u0001" + m.Content
                      + (_expanded.Contains(m.Content) ? "\u0002e" : "");
            var proto = !isEvent && IsProtocolMessage(m);
            FrameworkElement el;
            if (!_msgElCache.TryGetValue(key, out el))
            {
                el = isEvent
                    ? BuildEventLine(m)
                    : proto
                        ? BuildExchangeLine(m)
                        : BuildBubble(isUser, isUser ? "你" : name, m.Timestamp, ToDisplay(content));
                _msgElCache[key] = el;
            }
            items.Add((m.Timestamp, el, isEvent ? "event" : (isUser ? (proto ? "protoU" : "user") : (proto ? "toolA" : "asst"))));
        }
        // 早于第一条聊天记录的 agent 操作不进窗（清空记录后不残留一排孤行）；数据仍在 agent_ops.json，记忆管理器可见可清
        foreach (var op in _ops.Where(o => o.Ts >= firstTs))
        {
            var key = "op\u0001" + op.Ts.Ticks + "\u0001" + op.Tool + "\u0001" + op.Verdict + "\u0001" + op.Title + "\u0001" + op.Detail;
            if (!_msgElCache.TryGetValue(key, out var el))
            {
                el = BuildOpLine(op);
                _msgElCache[key] = el;
            }
            items.Add((op.Ts, el, "op:" + op.Verdict));
        }
        // 冻结气泡（[tool] 抑制/出错）：按冻结时刻参与排序，停在它本该在的位置（自动放行记录之前），
        // 而不是临时挂到面板末尾；正式版 assistant 消息落历史后（Timestamp ≥ 冻结时刻）移除临时版
        if (_streamFrozen && _streamBubble != null)
        {
            var replaced = history.Any(m => m.Role == "assistant" && m.Timestamp >= _frozenTs.AddSeconds(-1));
            if (replaced) RemoveStreamBubble();
            else items.Add((_frozenTs, _streamBubble!, "FROZEN"));
        }
        // 稳定排序：时间戳相同（同一毫秒的工具往返）保持插入顺序
        var ordered = items.Select((it, i) => new { it.El, Ts = it.Ts, Kind = it.Kind, I = i })
                           .OrderBy(x => x.Ts).ThenBy(x => x.I)
                           .ToList();
        // 布局诊断：流式/冻结气泡在场时记录面板实际顺序（时间戳+类型），排查"工具记录跑到文本上方"类问题
        if (_streamFrozen || _streamFilter != null)
            Log.Info("[layout] " + string.Join(" | ", ordered.Select(o => o.Ts.ToString("HH:mm:ss.fff") + ":" + o.Kind))
                     + (_streamBubble != null && !_streamFrozen ? " | typing@tail" : ""));
        foreach (var o in ordered) MsgPanel.Children.Add(o.El);

        if (_pendingConfirm != null && !_pendingConfirm.Resolved)
            MsgPanel.Children.Add(_pendingConfirm.Card); // 未决确认卡片保持在消息流末尾
        if (_pendingAsk != null && !_pendingAsk.Resolved)
            MsgPanel.Children.Add(_pendingAsk.Card);     // 未决提问卡片同理
        // 流式打字气泡=最新内容，保持在最末尾（Clear 后重挂）；冻结气泡在上面已按时间戳入 items 排好位。
        // 必须显式判 _streamBubble != null：?.Parent 在 null 时也是 null==null=true，会把 null Add 进面板崩掉。
        // 已被取代判定（后台时钟同域比较）：本步流起点后已有 assistant 消息落历史（工具步原文/正式版），
        // 临时气泡内容已有正式载体 → 不重挂，消除"打字气泡与正式版同屏双显"的瞬态重叠
        var startTs = _pipeline.StreamStepStartTs;
        var superseded = startTs != null && history.Any(m => m.Role == "assistant" && m.Timestamp >= startTs.Value.AddSeconds(-1));
        if (_streamBubble != null && _streamBubble.Parent == null && !superseded)
            MsgPanel.Children.Add(_streamBubble);
        if (scrollToEnd) ScrollToEnd();
    }

    /// <summary>剥离情绪标签（内置 ∪ 角色文件夹 ∪ TTS 自定义，与流式过滤同口径），避免 [happy] 之类出现在显示文本里。</summary>
    private string ToDisplay(string? content)
    {
        var s = content ?? "";
        if (s.Length == 0) return "";
        var tags = _pipeline.AllKnownEmotions();
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

    // ---------------- 流式打字气泡 ----------------

    /// <summary>流式增量（后台线程）：维护消息流末尾的临时"打字中"气泡。正式回复落历史后由 HistoryChanged 重建替换。</summary>
    private void OnReplyDelta(string soFar)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_streamFilter == null) // 新的一条流（主动搭话/重试等）：先清掉上一轮冻结的残影
            {
                if (_streamFrozen) RemoveStreamBubble();
                _streamFrozen = false;
                _streamFilter = new StreamTagFilter(_pipeline.AllKnownEmotions()); // 内置∪角色文件夹∪TTS：TTS 不可用时标签也不漏进显示
                _streamRaw = "";
                _streamShown = "";
            }
            if (soFar.Length <= _streamRaw.Length) return; // 重复/乱序片：忽略
            var piece = soFar[_streamRaw.Length..];
            _streamRaw = soFar;

            var released = _streamFilter.Feed(piece);
            if (released.Length == 0) return; // [tool] 块被吞 / 标签扣留中：显示无变化，跳过更新免无效布局
            UpdateStreamBubble(released);
        }));
    }

    /// <summary>流结束（均触发）：completed=true 正常完成 → 移除临时气泡（正式版随后由历史重建）；
    /// false 被 [tool] 抑制/出错/停止 → 正文已完整，直接渲染成正式版 Markdown 气泡（记 _frozenTs 供重建排序归位），等工具往返落历史的重建或下一条新流替换。</summary>
    private void OnReplyStreamEnd(bool completed)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (completed)
            {
                var tail = _streamFilter?.Flush(); // 释放扣留的尾部（含未决 '[' 序列）；注意只传 tail 本身（新片段），不是累计全文
                if (!string.IsNullOrEmpty(tail)) UpdateStreamBubble(tail);
                // 临时气泡延迟到背景优先级移除：让随后入队的 HistoryChanged→RebuildMessages 先跑完，
                // 正式版气泡就位后再摘临时版——消除"打字气泡消失→空白→正式气泡出现"的空窗卡顿感
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RemoveStreamBubble));
            }
            else if (_streamText != null)
            {
                var shown = ToDisplay(_streamShown).Trim();
                if (shown.Length == 0) { RemoveStreamBubble(); } // 纯工具块调用（整条回复无正文）：不留空气泡；不能 return——要走到下面清 _streamFilter，否则残留过滤器会让下次重建往面板里 Add(null) 崩掉
                else
                {
                    _streamFrozen = true;
                    // 用管线记录的后台时钟时刻（与历史 Timestamp 同时钟域）：UI 线程卡顿导致 UI Now 落后 >1s 时，
                    // 旧逻辑会把"正式版已落历史"误判为否 → 冻结纯文本气泡与正式版 Markdown 气泡同屏重叠闪烁
                    _frozenTs = _pipeline.LastToolStepEndTs ?? DateTime.Now;
                    // 正文已完整（[tool] 块已被过滤器吞掉，块两侧正文都在）：去光标冻结纯文本 + 头注改 名字+时间戳。
                    // 不做 MarkdownRenderer 整树替换（会闪）；Markdown 正式版由工具执行完 OnMessage 落历史的重建自然换入
                    _streamText.Text = shown;
                    var name = string.IsNullOrEmpty(App.Config.EffectiveCharacterName) ? "宠物" : App.Config.EffectiveCharacterName;
                    if (_streamHeader != null) _streamHeader.Text = name + "  " + _frozenTs.ToString("MM-dd HH:mm");
                }
            }
            _streamFilter = null;
            _streamRaw = "";
        }));
    }

    private void UpdateStreamBubble(string releasedText)
    {
        // Feed 返回的是"本片新释放的片段"，要追加到已累计文本后面
        _streamShown += releasedText ?? "";
        // RebuildMessages 可能刚清过面板：气泡不在树上就重新挂到末尾
        if (_streamBubble == null || _streamBubble.Parent == null)
        {
            var name = string.IsNullOrEmpty(App.Config.EffectiveCharacterName) ? "宠物" : App.Config.EffectiveCharacterName;
            _streamText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = MakeBrush("#E8ECF2"),
            };
            var bubble = new Border
            {
                Background = MakeBrush("#34383F"),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 7, 10, 7),
                Child = _streamText,
            };
            _streamHeader = new TextBlock
            {
                Text = name + "  输入中…",
                FontSize = 10.5,
                Foreground = MakeBrush("#777"),
                Margin = new Thickness(2, 2, 2, 0),
            };
            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth = Math.Max(220, MsgPanel.ActualWidth * 0.82),
                Margin = new Thickness(0, 5, 0, 5),
            };
            stack.Children.Add(bubble);
            stack.Children.Add(_streamHeader);
            _streamBubble = stack;
            MsgPanel.Children.Add(stack);
        }
        // 打字光标：尾部加闪烁块（纯文本，收尾即移除）。
        // TrimEnd：纯 TextBlock 会把尾部 \n\n 渲染成真实空行，裁掉尾空白与正式版观感一致
        _streamText!.Text = _streamShown.TrimEnd() + "▍";
        ScrollToEnd();
    }

    private void RemoveStreamBubble()
    {
        if (_streamBubble != null && _streamBubble.Parent != null)
            MsgPanel.Children.Remove(_streamBubble);
        _streamBubble = null;
        _streamText = null;
        _streamHeader = null;
        _streamFrozen = false;
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

    /// <summary>窗口关闭/隐藏时若有未决确认/提问，按拒绝处理（与宠物气泡超时语义一致）。</summary>
    private void RejectPendingConfirm()
    {
        var card = _pendingConfirm;
        if (card != null && !card.Resolved) ResolveConfirm(card, allowed: false, trust: false);
        var ask = _pendingAsk;
        if (ask != null && !ask.Resolved) ResolveAsk(ask, answered: false);
    }

    // ---------------- 窗内 opencode 式提问（选项按钮 + 自由输入，一次可多问） ----------------

    /// <summary>聊天窗可见时接管 ask_user；不可见/已有未决卡片返回 null，调用方回退宠物气泡。</summary>
    public Task<AskResult>? TryShowAskAsync(AskRequest req)
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.Invoke(() => TryShowAskAsync(req)); // 窗口属性必须在 UI 线程读
        if (!IsVisible || _pendingConfirm != null || _pendingAsk != null) return null;

        var card = new AskCard { Request = req };
        BuildAskCard(card);
        _pendingAsk = card;
        MsgPanel.Children.Add(card.Card);
        ScrollToEnd();
        Activate(); // 提亮窗口提醒用户
        return card.Tcs.Task;
    }

    private void BuildAskCard(AskCard card)
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
            BorderBrush = MakeBrush("#5A6A8A"),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
        };
        var inner = new StackPanel();

        inner.Children.Add(new TextBlock
        {
            // 有 reason 时以它作标题（模型说明的提问目的），否则默认文案
            Text = string.IsNullOrWhiteSpace(req.Title)
                ? (req.Questions.Count > 1 ? "有 " + req.Questions.Count + " 个问题需要你确认" : "想问你一个问题")
                : CapText(req.Title, 120),
            FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = MakeBrush("#EEE"), TextWrapping = TextWrapping.Wrap,
        });

        for (var i = 0; i < req.Questions.Count; i++)
        {
            int qi = i; // for 循环变量会被 lambda 共享，必须捕获每轮副本
            var q = req.Questions[i];
            var section = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            section.Children.Add(new TextBlock
            {
                Text = (req.Questions.Count > 1 ? (qi + 1) + ". " : "") + q.Question,
                FontSize = 13.5, LineHeight = 19, Foreground = MakeBrush("#DDD"), TextWrapping = TextWrapping.Wrap,
            });

            var selected = new bool[q.Options.Count];
            card.Selections.Add(new List<int>());
            if (q.Options.Count > 0)
            {
                var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                var optBtns = new List<Button>();
                for (var j = 0; j < q.Options.Count; j++)
                {
                    int idx = j;
                    var b = new Button
                    {
                        Content = q.Options[j],
                        Foreground = Brushes.White,
                        Padding = new Thickness(12, 4, 12, 4),
                        Margin = new Thickness(0, 0, 8, 6),
                        FontSize = 13,
                        Cursor = Cursors.Hand,
                    };
                    StyleAskOption(b, false);
                    b.Click += (_, _) =>
                    {
                        if (card.Resolved) return;
                        bool on = !selected[idx];
                        if (q.Multiple) selected[idx] = on;
                        else for (var k = 0; k < selected.Length; k++) selected[k] = (k == idx && on);
                        for (var k = 0; k < optBtns.Count; k++) StyleAskOption(optBtns[k], selected[k]);
                        var selList = card.Selections[qi];
                        selList.Clear();
                        for (var k = 0; k < selected.Length; k++) if (selected[k]) selList.Add(k);
                    };
                    optBtns.Add(b);
                    wrap.Children.Add(b);
                }
                section.Children.Add(wrap);
            }

            section.Children.Add(new TextBlock
            {
                Text = q.Options.Count > 0 ? "或直接输入：" : "输入回答：",
                FontSize = 11, Foreground = MakeBrush("#8A93A6"), Margin = new Thickness(0, 4, 0, 2)
            });
            var tb = new TextBox
            {
                Background = MakeBrush("#26282C"), Foreground = MakeBrush("#EEE"), CaretBrush = MakeBrush("#EEE"),
                BorderBrush = MakeBrush("#454B57"), BorderThickness = new Thickness(1),
                FontSize = 13, Padding = new Thickness(6, 4, 6, 4), MaxWidth = 320,
            };
            tb.KeyDown += (_, ke) => { if (ke.Key == Key.Enter) { ke.Handled = true; ResolveAsk(card, answered: true); } }; // 回车=提交全部
            section.Children.Add(tb);
            card.Inputs.Add(tb);
            inner.Children.Add(section);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(MakeConfirmButton("提交", "#3A7A4A", () => ResolveAsk(card, answered: true)));
        buttons.Children.Add(MakeConfirmButton("取消", "#8A5A5A", () => ResolveAsk(card, answered: false), leftMargin: 8));
        inner.Children.Add(buttons);

        border.Child = inner;
        root.Children.Add(border);
        card.Card = root;
    }

    private static void StyleAskOption(Button b, bool isSelected)
    {
        b.Background = MakeBrush(isSelected ? "#3D7EBF" : "#26282C");
        b.BorderBrush = MakeBrush(isSelected ? "#3D7EBF" : "#454B57");
        b.BorderThickness = new Thickness(1);
    }

    /// <summary>汇总答案：每问「输入框非空→用文本，否则所选选项（多选以、连接）」；answered=false 时已填部分仍带回。</summary>
    private void ResolveAsk(AskCard card, bool answered)
    {
        if (card.Resolved) return;
        card.Resolved = true;
        try { card.Card.IsEnabled = false; } catch { } // 点下任意选项的瞬间整卡失效

        var answers = new List<string>();
        for (var i = 0; i < card.Request.Questions.Count; i++)
        {
            var q = card.Request.Questions[i];
            var text = i < card.Inputs.Count ? (card.Inputs[i].Text ?? "").Trim() : "";
            if (text.Length > 0)
            {
                answers.Add(text);
                continue;
            }
            var sel = i < card.Selections.Count ? card.Selections[i] : new List<int>();
            answers.Add(sel.Count == 0 ? "" : string.Join("、", sel.OrderBy(x => x).Select(x => q.Options[x])));
        }
        card.Tcs.TrySetResult(new AskResult { Answered = answered, Answers = answers });
        if (_pendingAsk == card) _pendingAsk = null;
        ScrollToEnd();
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
        RemoveStreamBubble(); // 清掉上一轮出错遗留的冻结气泡（[tool] 抑制的会由正式版落历史时自动移除）
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

    /// <summary>清空状态栏（切换角色后清掉旧角色遗留的"已停止/出错"等提示，并刷新停止按钮）。</summary>
    public void ResetStatus() => SetStatus("");

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
    /// <summary>系统生成的 user 消息（非用户本人所说）：带 [SYSTEM] 前缀——主动搭话的沉默指令、直播间事件触发。
    /// 这类消息进模型上下文/记忆但不在聊天窗显示蓝色气泡，避免与真人发言混淆。</summary>
    private static bool IsHiddenSystemRelay(ChatMessage m)
    {
        if (m.Role != "user") return false;
        return (m.Content ?? "").TrimStart().StartsWith("[SYSTEM]", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>跳过占位标记 [no-reply]：历史里保留以维持 user/assistant 交替 + 让模型知道宠物选了沉默，但不在聊天窗显示。</summary>
    private static bool IsSkipMarker(ChatMessage m)
        => m.Role == "assistant" && (m.Content ?? "").Contains("no-reply");

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

    /// <summary>工具往返紧凑行：小字号等宽、低对比，贴在消息流里不喧宾夺主。
    /// [result]/[error] 超长内容默认折叠，点击展开全文（opencode 式），再点收起；展开状态按消息全文记在 _expanded 里，重建后不丢。</summary>
    private FrameworkElement BuildExchangeLine(ChatMessage m)
    {
        var c = (m.Content ?? "").Trim();
        string prefix, text;
        bool expandable = false;
        string _prose = ""; // [tool] 前的角色正文（仅 assistant 工具消息有值）
        if (m.Role == "user")
        {
            if (c.StartsWith("[note]", StringComparison.OrdinalIgnoreCase)) { prefix = "◈"; text = c.Substring(6).Trim(); }
            else if (c.StartsWith("[error]", StringComparison.OrdinalIgnoreCase)) { prefix = "!"; text = c.Substring(7).Trim(); expandable = true; }
            else if (c.StartsWith("[result]", StringComparison.OrdinalIgnoreCase)) { prefix = "↩"; text = c.Substring(8).Trim(); expandable = true; }
            else { prefix = "↩"; text = c; }
        }
        else if (c.StartsWith("[system]", StringComparison.OrdinalIgnoreCase))
        {
            prefix = "✦"; // 系统信息（如手动停止标记）：紧凑行，不占宠物气泡
            text = c.Substring(9).Trim();
        }
        else
        {
            // 工具调用行：name · reason（模型说明的这一步目的），一眼看出每步在干什么
            var nm = Regex.Match(c, "\"name\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            prefix = "⚙";
            text = nm.Success ? nm.Groups[1].Value : c;
            var rm = Regex.Match(c, "\"reason\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (rm.Success && !string.IsNullOrWhiteSpace(rm.Groups[1].Value))
                text += " · " + rm.Groups[1].Value;

            // [tool] 块两侧的正文（剥工具块+情绪标签）：模型可能先写开场白再发块，也可能先发明确/高危工具块
            // 再在 [/tool] 之后补确认提问——只取块前会丢后半段。放在紧凑工具行上方。
            _prose = ToDisplay(AgentRunner.StripToolBlocks(c));
        }

        const int collapsedLen = 120;
        var expanded = _expanded.Contains(c);
        bool showAll = !expandable || text.Length <= collapsedLen || expanded;

        var tb = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = MakeBrush(m.Role == "user" ? "#667080" : "#8A93A0"),
            MaxWidth = Math.Max(240, MsgPanel.ActualWidth * 0.9),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 1, 0, 1),
        };
        if (showAll)
        {
            tb.Text = prefix + " " + text;
            if (expandable && text.Length > collapsedLen && expanded)
            {
                tb.Text += "\n⌃ 点击收起";
                tb.Cursor = Cursors.Hand;
                tb.MouseLeftButtonUp += (_, _) =>
                {
                    _expanded.Remove(c);
                    RebuildMessages(scrollToEnd: false); // 原地切换，不跳底
                };
            }
        }
        else
        {
            // 折叠态：截断 + 展开提示，点击切换
            tb.Text = prefix + " " + CapText(text, collapsedLen).Replace("\n", " ⏎ ") + " ⌄ 点击展开";
            tb.Cursor = Cursors.Hand;
            tb.MouseLeftButtonUp += (_, _) =>
            {
                _expanded.Add(c);
                RebuildMessages(scrollToEnd: false); // 原地切换，不跳底
            };
        }

        if (string.IsNullOrWhiteSpace(_prose)) return tb;
        // 正文 + 工具行：正文用宠物气泡包裹（与流式打字气泡观感一致），工具行保持等宽紧凑
        var name = string.IsNullOrEmpty(App.Config.EffectiveCharacterName) ? "宠物" : App.Config.EffectiveCharacterName;
        var stack = new StackPanel();
        stack.Children.Add(BuildBubble(false, name, m.Timestamp, _prose));
        stack.Children.Add(tb);
        return stack;
    }

    /// <summary>第三方事件行（直播间弹幕/礼物等，Role="event"）：小字号低对比、左对齐带 » 前缀——
    /// 明确不是用户蓝气泡也不是角色灰气泡，视觉上也帮模型/用户分清"谁在说话"。</summary>
    private FrameworkElement BuildEventLine(ChatMessage m)
    {
        return new TextBlock
        {
            Text = "» " + (m.Content ?? ""),
            FontSize = 11,
            Foreground = MakeBrush("#7A8290"),
            MaxWidth = Math.Max(240, MsgPanel.ActualWidth * 0.9),
            HorizontalAlignment = HorizontalAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18, 2, 0, 2),
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

        // 行文本：reason（目的）· Title（动作）· detail（详情），缺哪项省哪项
        var lineText = (string.IsNullOrWhiteSpace(op.Reason) ? "" : CapText(op.Reason, 80) + " · ")
                     + (string.IsNullOrWhiteSpace(op.Title) ? "" : op.Title + " · ")
                     + detail;
        var detailText = new TextBlock
        {
            Text = lineText,
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
