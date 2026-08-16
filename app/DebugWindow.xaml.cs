using System;
using System.Windows;

namespace DesktopPetUi;

public partial class DebugWindow : Window
{
    public DebugWindow()
    {
        InitializeComponent();
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

    private void OnClear(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(LogBox.Text); } catch { }
    }
}