using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopPetUi.Core;
using DesktopPetUi.Native;

namespace DesktopPetUi;

public partial class App : System.Windows.Application
{
    private const string MutexName = "DesktopPetUi.SingleInstance";
    private Mutex? _mutex;
    private PetWindow? _window;
    private ChatWindow? _chatWindow;
    private SettingsWindow? _settingsWindow;
    private DebugWindow? _debugWindow;
    private MemoryManagerWindow? _memoryWindow;
    private ChatPipeline? _chatPipeline;
    private Hotkey? _hotkey;
    private NotifyIcon? _tray;
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    private DispatcherTimer? _proactiveTimer;
    private bool _proactiveEnabled;

    public static AppConfig Config { get; private set; } = null!;
    public static PetWindow? PetWindow { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Info("App starting");
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            Log.Info("Another instance already running, exiting");
            Shutdown();
            return;
        }

        try
        {
            var exeDir = AppContext.BaseDirectory;
            Config = AppConfig.Load(Path.Combine(exeDir, "config.json"));
            Log.Info("Config loaded from " + Config.ConfigPath);
        }
        catch (Exception ex)
        {
            Log.Error("Config load failed", ex);
            Config = new AppConfig();
        }
        if (!EnsureCharacterSelected()) return;
        Config.LoadActiveCharacter();
        Log.Info("Active character: " + Config.EffectiveCharacterName);
        LlamaClient.ConfigureProxy(Config.Chat.Proxy);

        base.OnStartup(e);

        try
        {
            _window = new PetWindow(Config);
            PetWindow = _window;
            _window.SpeechStarted += OnSpeechStarted;
            _window.SpeechFinished += OnSpeechFinished;
            SetupTray(_window);

            if (Config.Chat.Enabled)
            {
                _chatPipeline = new ChatPipeline(Config);
                _chatPipeline.DebugLog = text => _debugWindow?.Append(text);
                _chatWindow = new ChatWindow(Config, _chatPipeline, () => _window?.GetWindowRect());
                _window.ChatRequested = () => Dispatcher.Invoke(() =>
                {
                    if (Config.Chat.ScreenAware)
                        _ = _chatPipeline.ObserveScreenAsync();
                    _chatWindow?.ShowForInput();
                });
                SetupHotkey();
            }

            _window.Show();
            Log.Info("Window shown");

            LoadChatMemory();
            ConfigureProactiveTimer();
        }
        catch (Exception ex)
        {
            Log.Error("Window creation failed", ex);
            System.Windows.MessageBox.Show("启动失败：" + ex.Message, "Desktop Pet", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private bool EnsureCharacterSelected()
    {
        var chars = Config.ListCharacters();
        if (chars.Count == 0)
        {
            Log.Error("No characters found, exiting.");
            System.Windows.MessageBox.Show(
                "没有找到任何角色。\n请在程序目录的 character 文件夹下创建角色目录（内含 character.json）。",
                "Desktop Pet",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown();
            return false;
        }
        var current = Config.Character.Current ?? "";
        if (string.IsNullOrWhiteSpace(current) ||
            !chars.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            Config.Character.Current = chars[0];
            Config.Save();
            Log.Info("Auto-selected first character: " + chars[0]);
        }
        return true;
    }

    private void SetupHotkey()
    {
        try
        {
            _chatWindow!.Show();
            _chatWindow.Hide();
            var handle = new WindowInteropHelper(_chatWindow).Handle;
            var source = HwndSource.FromHwnd(handle);
            if (source == null) return;
            if (Hotkey.TryParse(Config.Chat.Hotkey.Modifiers, Config.Chat.Hotkey.Key, out var mods, out var vk))
            {
                _hotkey = new Hotkey(source, mods, vk);
                _hotkey.Pressed += () => Dispatcher.Invoke(() => _chatWindow?.ShowForInput());
                Log.Info(_hotkey.IsRegistered
                    ? $"Hotkey registered: {Config.Chat.Hotkey.Modifiers}+{Config.Chat.Hotkey.Key}"
                    : "Hotkey register failed (可能被其它程序占用)");
            }
            else
            {
                Log.Error("Hotkey config invalid: " + Config.Chat.Hotkey.Modifiers + "+" + Config.Chat.Hotkey.Key);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Hotkey setup failed", ex);
        }
    }

    private static System.Drawing.Icon ExtractAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return System.Drawing.Icon.ExtractAssociatedIcon(path) ?? System.Drawing.SystemIcons.Application;
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    private void SetupTray(PetWindow win)
    {
        _trayMenu = new System.Windows.Forms.ContextMenuStrip();
        _trayMenu.Opening += (_, _) => BuildTrayMenu();
        BuildTrayMenu();

        _tray = new NotifyIcon
        {
            Icon = ExtractAppIcon(),
            Text = "Desktop Pet",
            ContextMenuStrip = _trayMenu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) =>
        {
            if (win.Visibility == Visibility.Visible) win.Hide();
            else win.Show();
        };
    }

    private void BuildTrayMenu()
    {
        _trayMenu!.Items.Clear();
        if (Config.Chat.Enabled)
        {
            var chat = new System.Windows.Forms.ToolStripMenuItem("对话");
            chat.Click += (_, _) => _chatWindow?.ShowForInput();
            var memory = new System.Windows.Forms.ToolStripMenuItem("记忆管理器…");
            memory.Click += (_, _) => Dispatcher.Invoke(OpenMemoryWindow);
            _trayMenu.Items.Add(chat);
            _trayMenu.Items.Add(memory);
            _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        }

        var current = _window?.GetCharacter() ?? "";
        foreach (var folder in _window?.GetCharacters() ?? new System.Collections.Generic.List<string>())
        {
            var item = new System.Windows.Forms.ToolStripMenuItem(Config.CharacterDisplayName(folder)) { Checked = folder == current };
            item.Click += (_, _) => SwitchCharacter(folder);
            _trayMenu.Items.Add(item);
        }
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var settings = new System.Windows.Forms.ToolStripMenuItem("设置…");
        settings.Click += (_, _) => Dispatcher.Invoke(OpenSettingsWindow);
        var debug = new System.Windows.Forms.ToolStripMenuItem("调试窗口");
        debug.Click += (_, _) => Dispatcher.Invoke(OpenDebugWindow);
        var show = new System.Windows.Forms.ToolStripMenuItem("显示/隐藏");
        show.Click += (_, _) =>
        {
            var w = _window;
            if (w != null)
            {
                if (w.Visibility == Visibility.Visible) w.Hide();
                else w.Show();
            }
        };
        var exit = new System.Windows.Forms.ToolStripMenuItem("退出");
        exit.Click += (_, _) => Shutdown();
        _trayMenu.Items.Add(settings);
        _trayMenu.Items.Add(debug);
        _trayMenu.Items.Add(show);
        _trayMenu.Items.Add(exit);
    }

    private void OpenMemoryWindow()
    {
        if (_memoryWindow == null)
        {
            _memoryWindow = new MemoryManagerWindow(Config, _chatPipeline!, () => SaveChatMemory());
            _memoryWindow.Closed += (_, _) => _memoryWindow = null;
        }
        _memoryWindow.Show();
        _memoryWindow.Activate();
    }

    private void OpenSettingsWindow()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(Config);
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                EndCharacterScalePreview();
            };
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OpenDebugWindow()
    {
        if (_debugWindow == null)
        {
            _debugWindow = new DebugWindow();
            _debugWindow.Closed += (_, _) => _debugWindow = null;
        }
        _debugWindow.Show();
        _debugWindow.Activate();
    }

    public static void SwitchCharacter(string name)
    {
        if (Current is not App app) return;
        app.SaveChatMemory();
        PetWindow?.SetCharacter(name);
        app.LoadChatMemory();
    }

    public static void RefreshAll()
    {
        if (Current is not App app) return;
        LlamaClient.ConfigureProxy(Config.Chat.Proxy);
        app.ConfigureProactiveTimer();
        PetWindow?.ApplyWindowConfig();
        PetWindow?.RefreshIdleCycle();
    }

    public static void PreviewCharacterScale(double? scale)
    {
        if (Current is not App app || PetWindow == null) return;
        app.Dispatcher.Invoke(() => PetWindow.PreviewScale(scale));
    }

    public static void EndCharacterScalePreview()
    {
        if (Current is not App app || PetWindow == null) return;
        app.Dispatcher.Invoke(() => PetWindow.ClearScalePreview());
    }

    private string MemoryPath =>
        Path.Combine(Config.CharacterDir,
            string.IsNullOrWhiteSpace(Config.Character.Current)
                ? "memory.json"
                : Path.Combine(Config.Character.Current, "memory.json"));

    private void LoadChatMemory()
    {
        if (_chatPipeline == null) return;
        try
        {
            var path = MemoryPath;
            if (File.Exists(path))
            {
                var mem = JsonSerializer.Deserialize<MemoryFile>(File.ReadAllText(path));
                if (mem != null) _chatPipeline.Restore(mem.Summary, mem.History);
                Log.Info("Chat memory loaded: " + path);
            }
            else
            {
                _chatPipeline.Restore(null, Array.Empty<ChatMessage>());
                Log.Info("No chat memory file for this character, reset: " + path);
            }
        }
        catch (Exception ex)
        {
            Log.Error("LoadChatMemory failed", ex);
        }
    }

    private void SaveChatMemory()
    {
        if (_chatPipeline == null) return;
        try
        {
            var path = MemoryPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var mem = new MemoryFile
            {
                Summary = _chatPipeline.Summary,
                History = _chatPipeline.History.ToList(),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(mem));
        }
        catch (Exception ex)
        {
            Log.Error("SaveChatMemory failed", ex);
        }
    }

    private void ConfigureProactiveTimer()
    {
        try
        {
            if (_proactiveTimer == null)
            {
                _proactiveTimer = new DispatcherTimer();
                _proactiveTimer.Tick += OnProactiveTick;
            }
            _proactiveTimer.Stop();
            _proactiveEnabled = Config.Chat.Enabled && (Config.Chat.Proactive || Config.Chat.ScreenAware) && Config.Chat.ProactiveIntervalSec > 0;
            if (_proactiveEnabled)
            {
                _proactiveTimer.Interval = TimeSpan.FromSeconds(Config.Chat.ProactiveIntervalSec);
                _proactiveTimer.Start();
                Log.Info($"Proactive chat timer: every {Config.Chat.ProactiveIntervalSec}s");
            }
        }
        catch (Exception ex)
        {
            Log.Error("ConfigureProactiveTimer failed", ex);
        }
    }

    private void OnProactiveTick(object? sender, EventArgs e)
    {
        try
        {
            if (_chatPipeline == null || _chatPipeline.IsRunning) return;
            if (_window == null || _window.Visibility != Visibility.Visible) return;
            if (_chatWindow is { IsVisible: true }) return;
            _ = RunProactiveTickAsync();
        }
        catch (Exception ex)
        {
            Log.Error("OnProactiveTick failed", ex);
        }
    }

    private void OnSpeechStarted()
    {
        try
        {
            if (_proactiveTimer == null || !_proactiveEnabled) return;
            _proactiveTimer.Stop();
        }
        catch (Exception ex)
        {
            Log.Error("OnSpeechStarted failed", ex);
        }
    }

    private void OnSpeechFinished()
    {
        try
        {
            if (_proactiveTimer == null || !_proactiveEnabled) return;
            _proactiveTimer.Stop();
            _proactiveTimer.Start();
            Log.Info("Proactive countdown restarted after speech");
        }
        catch (Exception ex)
        {
            Log.Error("OnSpeechFinished failed", ex);
        }
    }

    private async Task RunProactiveTickAsync()
    {
        try
        {
            if (Config.Chat.ScreenAware && Random.Shared.NextDouble() < Config.Chat.ScreenAwareChance)
                await _chatPipeline!.ObserveScreenAsync();
            if (Config.Chat.Proactive)
                await _chatPipeline!.RunProactiveAsync(_window!);
        }
        catch (Exception ex)
        {
            Log.Error("RunProactiveTickAsync failed", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { SaveChatMemory(); } catch { }
        try { _proactiveTimer?.Stop(); } catch { }
        try { _window?.ShutdownSafely(); } catch { }
        try { _hotkey?.Dispose(); } catch { }
        try { _chatPipeline?.Dispose(); } catch { }
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}