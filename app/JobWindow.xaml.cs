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
        JobPanel.Children.Clear();
        int running = 0, finished = 0;
        foreach (var j in jobs)
        {
            if (j.Running) running++; else finished++;
            JobPanel.Children.Add(BuildCard(j));
        }
        if (jobs.Count == 0)
        {
            JobPanel.Children.Add(new TextBlock
            {
                Text = "暂无后台任务",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x73, 0x85)),
                FontSize = 12.5,
                Margin = new Thickness(2)
            });
        }
        StatsText.Text = jobs.Count == 0 ? "" : running + " 运行中 / " + jobs.Count + " 总数";
        ClearBtn.Visibility = finished > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement BuildCard(JobManager.JobSnapshot j)
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

        // 第一行：id + 状态徽标 + 耗时
        var topRow = new StackPanel { Orientation = Orientation.Horizontal };
        topRow.Children.Add(new TextBlock
        {
            Text = j.Id, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF2)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
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
        var elapsed = FormatElapsed((int)(DateTime.Now - j.StartedAt).TotalSeconds);
        topRow.Children.Add(new TextBlock
        {
            Text = (j.Running ? "已运行 " : "耗时 ") + elapsed,
            FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA6)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
        });
        info.Children.Add(topRow);

        // 第二行：命令（等宽，单行截断）
        if (!string.IsNullOrWhiteSpace(j.Command))
        {
            info.Children.Add(new TextBlock
            {
                Text = j.Command.Length > 90 ? j.Command[..90] + "…" : j.Command,
                FontFamily = new FontFamily("Consolas"), FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xAC)),
                Margin = new Thickness(0, 5, 0, 0)
            });
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
        return card;
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
