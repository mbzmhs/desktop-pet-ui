using System;
using System.Windows;

namespace DesktopPetUi;

public partial class DebugWindow : Window
{
    private System.Windows.Controls.TextBox _activeBox = null!;

    public DebugWindow()
    {
        InitializeComponent();
        _activeBox = LogBox;
    }

    public void Append(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Append(text)));
            return;
        }
        LogBox.AppendText(text);
        LogBox.AppendText(Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    /// <summary>整体替换「系统提示词」页（每次组装系统提示词时刷新）。</summary>
    public void SetSystemPrompt(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => SetSystemPrompt(text)));
            return;
        }
        SysPromptBox.Text = text;
        SysPromptBox.ScrollToHome();
    }

    /// <summary>整体替换「原始请求」页（最近一次发给 LLM 的完整请求）。</summary>
    public void SetRawRequest(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => SetRawRequest(text)));
            return;
        }
        RawBox.Text = text;
        RawBox.ScrollToHome();
    }

    private void OnTabChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (e.OriginalSource == Tabs && Tabs.SelectedItem is System.Windows.Controls.TabItem ti)
        {
            _activeBox = ti.Content as System.Windows.Controls.TextBox ?? LogBox;
        }
    }

    private void OnClear(object sender, RoutedEventArgs e) => _activeBox.Clear();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(_activeBox.Text); } catch { }
    }
}
