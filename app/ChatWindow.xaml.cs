using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DesktopPetUi.Core;
using DesktopPetUi.Native;

namespace DesktopPetUi;

public partial class ChatWindow : Window
{
    private readonly AppConfig _config;
    private readonly ChatPipeline _pipeline;
    private readonly Func<Rect?> _petRect;

    private sealed class HistoryEntry
    {
        public string Header { get; set; } = "";
        public string Content { get; set; } = "";
        public System.Windows.Media.Brush HeaderColor { get; set; } = System.Windows.Media.Brushes.Gray;
    }

    public ChatWindow(AppConfig config, ChatPipeline pipeline, Func<Rect?> petRect)
    {
        _config = config;
        _pipeline = pipeline;
        _petRect = petRect;
        InitializeComponent();

        Width = config.Chat.Ui.Width;
        Height = Math.Max(config.Chat.Ui.Height, 320);
        Topmost = config.Chat.Ui.AlwaysOnTop;
        Left = -10000;
        Top = -10000;
        _pipeline.Status = SetStatus;
        _pipeline.HistoryChanged += RefreshHistory;
        InputBox.KeyDown += OnInputKeyDown;
        RefreshHistory();
    }

    public void ShowForInput()
    {
        var name = App.Config.Character.Current;
        HintText.Text = string.IsNullOrEmpty(name) ? "和宠物说点什么…" : "和" + name + "说点什么…";
        Position();
        Show();
        Activate();
        RefreshHistory();
        if (_pipeline.IsRunning)
        {
            return;
        }
        InputBox.Focus();
        Keyboard.Focus(InputBox);
        SetStatus("");
    }

    private void Position()
    {
        var w = Width;
        var h = Height;
        double x, y;
        if (_config.Chat.Ui.PopupFollowsPet && _petRect() is Rect r)
        {
            x = r.Left + (r.Width - w) / 2;
            y = r.Top - h - 8;
            if (y < 0) y = r.Bottom + 8;
        }
        else
        {
            var p = CursorUtil.GetPosition();
            x = p.X + 16;
            y = p.Y - h - 16;
        }
        var wa = SystemParameters.WorkArea;
        x = Math.Clamp(x, wa.Left + 4, wa.Right - w - 4);
        y = Math.Clamp(y, wa.Top + 4, wa.Bottom - h - 4);
        Left = x;
        Top = y;
    }

    private void RefreshHistory()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshHistory));
            return;
        }
        var name = App.Config.Character.Current;
        if (string.IsNullOrEmpty(name)) name = "宠物";
        var userBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7E, 0xB8, 0xF5));
        var charBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0xF0));

        var entries = new List<HistoryEntry>();
        foreach (var m in _pipeline.History)
        {
            var isUser = m.Role == "user";
            var ts = m.Timestamp.Year >= 2000 ? m.Timestamp.ToString("MM-dd HH:mm") : "";
            var who = isUser ? "你" : name;
            entries.Add(new HistoryEntry
            {
                Header = ts.Length > 0 ? who + "  " + ts : who,
                Content = m.Content ?? "",
                HeaderColor = isUser ? userBrush : charBrush,
            });
        }
        HistoryList.ItemsSource = entries;
        HistoryScroll.ScrollToEnd();
    }

    private void OnInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Submit();
        }
    }

    private void Submit()
    {
        var text = InputBox.Text?.Trim();
        if (string.IsNullOrEmpty(text) || _pipeline.IsRunning) return;
        InputBox.Clear();
        _ = RunAsync(text);
    }

    private async Task RunAsync(string text)
    {
        SetStatus("发送中…");
        var ok = await _pipeline.RunAsync(text, App.PetWindow!);
        if (ok) Hide();
    }

    private void SetStatus(string msg) => StatusText.Text = msg;
}