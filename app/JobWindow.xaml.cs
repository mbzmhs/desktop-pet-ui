using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopPetUi.Core.Agent;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace DesktopPetUi;

/// <summary>后台任务管理器：暗色半透明（与 TodoWindow 同款外壳），实时展示 agent 的 start_powershell 任务——状态/耗时/命令/输出末尾；可手动终止运行中任务、清除已完成。</summary>
public partial class JobWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, CardRef> _cards = new(); // 每任务卡片缓存：内容没变就不重建元素（保住悬停 tooltip 的命中目标）
    private TextBlock? _emptyHint;

    private sealed class CardRef
    {
        public FrameworkElement Card = null!;
        public TextBlock Elapsed = null!;
        public string Sig = "";
        public bool CmdExpanded; // 命令全文展开态（跨重建保留）
    }

    public JobWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshCore();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        RefreshCore();
    }

    private void RefreshCore()
    {
        var jobs = JobManager.Snapshot();
        int running = 0, finished = 0;
        var desired = new List<FrameworkElement>(jobs.Count);
        foreach (var j in jobs)
        {
            if (j.Running) running++; else finished++;
            // 签名=状态指纹：内容没变→复用旧元素（只原地更新耗时），变了才重建
            var sig = j.Id + "|" + j.Running + "|" + j.ExitCode + "|" + j.TimedOut + "|" + j.Command.Length + "|" + j.Tail.Length;
            if (j.Tail.Length > 0) sig += "|" + j.Tail[^Math.Min(64, j.Tail.Length)..].GetHashCode();

            if (_cards.TryGetValue(j.Id, out var oldRef) && oldRef.Sig == sig)
            {
                UpdateElapsed(oldRef, j); // 运行中=跳动，已完成=按 EndedAt 冻结
                desired.Add(oldRef.Card);
                continue;
            }
            bool keepExpanded = false;
            if (_cards.TryGetValue(j.Id, out var replaced))
            {
                keepExpanded = replaced.CmdExpanded; // 重建后保留命令展开态
                JobPanel.Children.Remove(replaced.Card);
            }
            var (card, elapsed) = BuildCard(j, keepExpanded);
            var @ref = new CardRef { Card = card, Elapsed = elapsed, Sig = sig, CmdExpanded = keepExpanded };
            _cards[j.Id] = @ref;
            desired.Add(card);
        }

        // 已消失的任务（清除已完成后）移除卡片
        foreach (var k in _cards.Keys.Where(k => jobs.All(x => x.Id != k)).ToList())
        {
            JobPanel.Children.Remove(_cards[k].Card);
            _cards.Remove(k);
        }

        if (jobs.Count == 0)
        {
            _emptyHint ??= new TextBlock
            {
                Text = "暂无后台任务",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x73, 0x85)),
                FontSize = 12.5,
                Margin = new Thickness(2)
            };
            SyncChildren(new List<FrameworkElement> { _emptyHint });
        }
        else
        {
            if (_emptyHint != null && JobPanel.Children.Contains(_emptyHint)) JobPanel.Children.Remove(_emptyHint);
            SyncChildren(desired);
        }

        StatsText.Text = jobs.Count == 0 ? "" : running + " 运行中 / " + jobs.Count + " 总数";
        ClearBtn.Visibility = finished > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>按期望序列同步子元素：位置与引用都没变的不动（保住 hover 状态），差异位先 Remove 再 Insert
    /// （UIElementCollection 的索引器 setter 不是替换语义，直接赋值会抛"索引已在使用"）。</summary>
    private void SyncChildren(List<FrameworkElement> desired)
    {
        var n = Math.Min(desired.Count, JobPanel.Children.Count);
        for (var k = 0; k < n; k++)
            if (!ReferenceEquals(JobPanel.Children[k], desired[k]))
            {
                JobPanel.Children.RemoveAt(k);
                JobPanel.Children.Insert(k, desired[k]); // 元素若还在集合别处会自动从旧位置摘出
            }
        while (JobPanel.Children.Count > desired.Count) JobPanel.Children.RemoveAt(JobPanel.Children.Count - 1);
        while (JobPanel.Children.Count < desired.Count) JobPanel.Children.Add(desired[JobPanel.Children.Count]);
    }

    private static void UpdateElapsed(CardRef @ref, JobManager.JobSnapshot j)
    {
        var end = j.EndedAt ?? DateTime.Now; // 已完成任务按实际退出时刻冻结
        var sec = Math.Max(0, (int)(end - j.StartedAt).TotalSeconds);
        @ref.Elapsed.Text = (j.Running ? "已运行 " : "耗时 ") + FormatElapsed(sec);
    }

    private (FrameworkElement Card, TextBlock Elapsed) BuildCard(JobManager.JobSnapshot j, bool cmdExpanded)
    {
        var (stateText, stateBg) = j.Running
            ? ("运行中", "#3A7A4A")
            : j.TimedOut
                ? ("超时被终止", "#8A5A5A")
                : ("已完成 · 退出码 " + (j.ExitCode?.ToString() ?? "?"), "#4A5568");

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x23, 0x27, 0x31)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 3, 0, 3)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Orientation = Orientation.Vertical };

        // 第一行：id + 任务名(reason) + 状态徽标 + 耗时
        var topRow = new StackPanel { Orientation = Orientation.Horizontal };
        topRow.Children.Add(new TextBlock
        {
            Text = j.Id, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF2)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        });
        if (!string.IsNullOrWhiteSpace(j.Name)) // 任务名=模型 reason（超长截断）
            topRow.Children.Add(new TextBlock
            {
                Text = j.Name.Length > 40 ? j.Name[..40] + "…" : j.Name,
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xC0, 0xCC)),
                Margin = new Thickness(0, 0, 8, 0),
            });
        var badge = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 1.5, 7, 1.5),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(stateBg)),
            VerticalAlignment = VerticalAlignment.Center
        };
        badge.Child = new TextBlock { Text = stateText, FontSize = 11, Foreground = Brushes.White };
        topRow.Children.Add(badge);
        var elapsedText = new TextBlock
        {
            Text = "", // RefreshCore 首帧即填（运行中=跳动，已完成=按 EndedAt 冻结）
            FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA6)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
        };
        UpdateElapsed(new CardRef { Elapsed = elapsedText }, j);
        topRow.Children.Add(elapsedText);
        info.Children.Add(topRow);

        // 第二行：命令（等宽）。折叠态=像素级截断（TextTrimming 按窗口实际宽度省略，不依赖固定字符数）+
        // 右侧始终可见的 ⌄ 展开箭头；展开态=全文换行 + ⌃ 收起。点击文字或箭头切换。
        if (!string.IsNullOrWhiteSpace(j.Command))
        {
            const int longCmdChars = 60; // 超过此长度才认为"需要展开"（短命令直接完整显示）
            bool canToggle = j.Command.Length > longCmdChars;

            var cmd = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"), FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xAC)),
                Margin = new Thickness(0, 5, 0, 0),
            };
            Action toggle = () =>
            {
                if (!_cards.TryGetValue(j.Id, out var r)) return;
                r.CmdExpanded = !r.CmdExpanded;
                r.Sig = ""; // 强制下一帧重建本卡（保留展开态）
                RefreshCore();
            };

            if (cmdExpanded)
            {
                cmd.Text = j.Command + " ⌃";
                cmd.TextWrapping = TextWrapping.Wrap;
                cmd.Cursor = Cursors.Hand;
                cmd.MouseLeftButtonUp += (_, _) => toggle();
            }
            else
            {
                var row = new DockPanel { LastChildFill = true };
                if (canToggle)
                {
                    var arrow = new TextBlock
                    {
                        Text = " ⌄", FontSize = 12, Cursor = Cursors.Hand,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0xA0, 0xC8)),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    arrow.MouseLeftButtonUp += (_, _) => toggle();
                    DockPanel.SetDock(arrow, Dock.Right);
                    row.Children.Add(arrow); // 先加右侧箭头，剩余宽度归命令文本
                }
                cmd.Text = j.Command;
                cmd.TextWrapping = TextWrapping.NoWrap;
                if (canToggle)
                {
                    cmd.TextTrimming = TextTrimming.CharacterEllipsis; // 按像素裁剪出"…"，任何窗口宽度下都可见
                    cmd.Cursor = Cursors.Hand;
                    cmd.MouseLeftButtonUp += (_, _) => toggle();
                }
                row.Children.Add(cmd);
                info.Children.Add(row);
            }
            if (cmdExpanded) info.Children.Add(cmd);
        }

        // 第三行：输出末尾（暗底等宽小字，最多 4 行）
        if (!string.IsNullOrWhiteSpace(j.Tail))
        {
            var lines = j.Tail.Replace("\r\n", "\n").Split('\n');
            var shown = lines.Length > 4 ? string.Join("\n", lines[^4..]) : j.Tail;
            info.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1E, 0x26)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 5, 7, 5),
                Margin = new Thickness(0, 6, 0, 0),
                Child = new TextBlock
                {
                    Text = shown.TrimEnd(),
                    FontFamily = new FontFamily("Consolas"), FontSize = 11, LineHeight = 15,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xC9, 0xA0)),
                    TextWrapping = TextWrapping.NoWrap
                }
            });
        }

        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        // 运行中：红色终止按钮
        if (j.Running)
        {
            var kill = new Button
            {
                Content = "终止",
                Background = new SolidColorBrush(Color.FromRgb(0x8A, 0x44, 0x44)),
                Foreground = Brushes.White, BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 3, 12, 3), FontSize = 12,
                Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top
            };
            kill.Click += (_, _) =>
            {
                JobManager.Kill(j.Id);
                RefreshCore();
            };
            Grid.SetColumn(kill, 1);
            grid.Children.Add(kill);
        }

        card.Child = grid;
        return (card, elapsedText);
    }

    private static string FormatElapsed(int sec)
    {
        if (sec < 60) return sec + "s";
        if (sec < 3600) return (sec / 60) + "m" + (sec % 60) + "s";
        return (sec / 3600) + "h" + (sec % 3600 / 60) + "m";
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => Hide();

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        JobManager.ClearFinished();
        RefreshCore();
    }
}
