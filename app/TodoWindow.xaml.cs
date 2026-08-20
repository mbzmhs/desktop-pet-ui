using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopPetUi.Core.Agent;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace DesktopPetUi;

/// <summary>Todo 列表窗口：暗色半透明、只读展示 agent 维护的任务进度；全部完成后出现「确定」按钮（清空并隐藏）。</summary>
public partial class TodoWindow : Window
{
    private readonly AppConfig _cfg;

    public TodoWindow(AppConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        TodoStore.Changed += OnChanged;
        Closed += (_, _) => TodoStore.Changed -= OnChanged;
        RefreshCore();
    }

    private void OnChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnChanged));
            return;
        }
        RefreshCore();
    }

    private void RefreshCore()
    {
        var items = TodoStore.Snapshot(_cfg);
        ItemPanel.Children.Clear();
        int done = 0;
        foreach (var it in items)
        {
            if (it.Done) done++;
            ItemPanel.Children.Add(BuildRow(it));
        }
        if (items.Count == 0)
        {
            ItemPanel.Children.Add(new TextBlock
            {
                Text = "暂无任务",
                Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x73, 0x85)),
                FontSize = 12.5,
                Margin = new Thickness(2)
            });
        }
        ProgressText.Text = items.Count == 0 ? "" : done + "/" + items.Count;
        ConfirmBtn.Visibility = (items.Count > 0 && done == items.Count) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static UIElement BuildRow(TodoItem it)
    {
        var dim = new SolidColorBrush(Color.FromRgb(0x7A, 0x82, 0x94));
        var bright = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF2));
        var doneBg = new SolidColorBrush(Color.FromRgb(0x3A, 0x7A, 0x4A));

        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var box = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(4),
            Background = it.Done ? doneBg : Brushes.Transparent,
            BorderBrush = it.Done ? doneBg : new SolidColorBrush(Color.FromRgb(0x5A, 0x64, 0x78)),
            BorderThickness = new Thickness(1.5),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 8, 0)
        };
        if (it.Done)
        {
            box.Child = new TextBlock
            {
                Text = "✓", Foreground = Brushes.White, FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
        }
        Grid.SetColumn(box, 0);
        grid.Children.Add(box);

        var text = new TextBlock
        {
            Text = it.Text, FontSize = 13, LineHeight = 18, TextWrapping = TextWrapping.Wrap,
            Foreground = it.Done ? dim : bright,
            TextDecorations = it.Done ? TextDecorations.Strikethrough : null
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => Hide();

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        TodoStore.Clear(_cfg);
        Hide();
    }
}
