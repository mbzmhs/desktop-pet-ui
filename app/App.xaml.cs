using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopPetUi.Core;
using DesktopPetUi.Core.Agent;
using DesktopPetUi.Core.Plugin;
using DesktopPetUi.Native;
using DesktopPetUi.Plugins;

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
    private TodoWindow? _todoWindow;
    private JobWindow? _jobWindow;
    private ChatPipeline? _chatPipeline;
    private Hotkey? _hotkey;
    private NotifyIcon? _tray;
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    private DispatcherTimer? _proactiveTimer;
    private bool _proactiveEnabled;
    private readonly object _memSaveLock = new();
    private System.Threading.Timer? _memSaveTimer; // 历史变更防抖自动保存

    public static AppConfig Config { get; private set; } = null!;
    public static PetWindow? PetWindow { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Info("App starting");

        // 全局异常兜底：记录完整堆栈到 pet.log，UI 线程异常拦截住不让进程静默死亡
        DispatcherUnhandledException += (s, ev) =>
        {
            Log.Error("UI 线程未处理异常（已拦截，程序继续运行）", ev.Exception);
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            if (ev.ExceptionObject is Exception ex) Log.Error("AppDomain 未处理异常", ex);
        };
        TaskScheduler.UnobservedTaskException += (s, ev) =>
        {
            Log.Error("未观察的后台任务异常", ev.Exception);
            ev.SetObserved();
        };

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
        AgentTools.ConfigureProxy(Config.Chat.Proxy); // web_fetch 与 LLM 共用代理设置

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
                _chatPipeline.SystemPromptDebug = text => _debugWindow?.SetSystemPrompt(text);
                LlamaClient.OnRequest = (url, json) => _debugWindow?.SetRawRequest(url + "\n" + json);
                var ep0 = Config.EffectiveLlm(); // 后台查询模型自报的上下文上限（保证请求不超它而报错）
                LlamaClient.RefreshModelContextAsync(ep0.Url, ep0.Model, ep0.ApiKey);
                _chatPipeline.HistoryChanged += ScheduleMemorySave; // 历史一变就自动落盘（防抖），意外退出不丢对话
                _chatWindow = new ChatWindow(Config, _chatPipeline, () => _window?.GetWindowRect());
                // 聊天窗可见时权限确认/提问在聊天窗内完成，否则回退宠物气泡
                _window.ConfirmRedirect = req => _chatWindow!.TryShowConfirmAsync(req);
                _window.AskRedirect = req => _chatWindow!.TryShowAskAsync(req);
                // 新建 todo / 后台任务时自动弹出对应窗口（activate:false + ShowActivated=false → 不抢键盘焦点）
                AgentTools.OnTodoCreated += () => Dispatcher.BeginInvoke(() => OpenTodoWindow(activate: false));
                JobManager.OnJobStarted += () => Dispatcher.BeginInvoke(() => OpenJobWindow(activate: false));
                _window.ChatRequested = () => Dispatcher.Invoke(() =>
                {
                    _chatWindow?.ShowForInput();
                });
                SetupHotkey();
            }

            // 插件系统：扫描 plugins/*.dll 并注册（设定/启用状态在 exe 目录 plugin.json）；
            // 能力桥=代替用户发消息（走完整聊天管线）+ 获取 Pet 信息。加载失败不影响主体启动
            PluginManager.LoadAll(Path.Combine(AppContext.BaseDirectory, "plugins"), new AppPluginBridge(this));

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
            var todo = new System.Windows.Forms.ToolStripMenuItem("Todo 列表…");
            todo.Click += (_, _) => Dispatcher.Invoke(OpenTodoWindow);
            var jobs = new System.Windows.Forms.ToolStripMenuItem("后台任务…");
            jobs.Click += (_, _) => Dispatcher.Invoke(OpenJobWindow);
            _trayMenu.Items.Add(chat);
            _trayMenu.Items.Add(todo);
            _trayMenu.Items.Add(jobs);
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

    /// <summary>activate=false 供自动弹出路径（新建 todo/后台任务）：只显示不抢键盘焦点（XAML ShowActivated=false 兜底）。</summary>
    private void OpenTodoWindow(bool activate = true)
    {
        if (_todoWindow == null)
        {
            _todoWindow = new TodoWindow(Config);
            _todoWindow.Closed += (_, _) => _todoWindow = null;
        }
        _todoWindow.Show();
        if (activate) _todoWindow.Activate();
    }

    private void OpenJobWindow(bool activate = true)
    {
        if (_jobWindow == null)
        {
            _jobWindow = new JobWindow();
            _jobWindow.Closed += (_, _) => _jobWindow = null;
        }
        _jobWindow.Show();
        if (activate) _jobWindow.Activate();
    }

    /// <summary>聊天窗标题栏入口。</summary>
    public static void ShowTodoWindow()
    {
        if (Current is App app) app.OpenTodoWindow();
    }

    /// <summary>聊天窗标题栏入口。</summary>
    public static void ShowJobWindow()
    {
        if (Current is App app) app.OpenJobWindow();
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
        // 有进行中的聊天（流式/非流式）先终止并等本轮完全收尾：否则旧角色的回复/中断标记会
        // 在 Restore 之后写进新角色的历史。收尾很快（取消 HTTP 即返回），仅长 TTS 播放时触顶超时。
        _ = Task.Run(async () =>
        {
            await app._chatPipeline.StopAndWaitAsync();
            app.Dispatcher.Invoke(() =>
            {
                app.SaveChatMemory();
                PetWindow?.SetCharacter(name);
                app.LoadChatMemory();
                app._chatWindow?.RefreshCharacterTitle(); // 标题栏角色名 + Context 占用归零（等下次请求）
                app._chatWindow?.ResetStatus(); // 清掉被中止的旧轮次遗留的"已停止/出错"状态
            });
        });
    }

    public static void RefreshAll()
    {
        if (Current is not App app) return;
        LlamaClient.ConfigureProxy(Config.Chat.Proxy);
        AgentTools.ConfigureProxy(Config.Chat.Proxy); // web_fetch 与 LLM 共用代理设置
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

    /// <summary>历史变化后防抖自动保存（最后一次变更 1.5s 后）：进程被意外杀掉时最多丢 1.5s 内的对话。</summary>
    private void ScheduleMemorySave()
    {
        lock (_memSaveLock)
        {
            _memSaveTimer?.Dispose();
            _memSaveTimer = new System.Threading.Timer(_ => SaveChatMemory(), null, 1500, Timeout.Infinite);
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
            lock (_memSaveLock)
            {
                // 先写临时文件再原子改名：写到一半被杀不会留下半截损坏的 memory.json（损坏会导致整段历史加载失败）
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(mem));
                File.Move(tmp, path, overwrite: true);
            }
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
            _proactiveEnabled = Config.Chat.Enabled && Config.Chat.Proactive && Config.Chat.ProactiveIntervalSec > 0;
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
        try { PluginManager.ShutdownAll(); } catch { }
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>插件宿主能力桥：SendChatAsync 走完整聊天管线（与用户消息同路径，_gate 自然排队）；GetPetInfo 读 PetWindow。</summary>
    private sealed class AppPluginBridge(App app) : IPluginHostBridge
    {
        public Task<bool> SendChatAsync(string text, CancellationToken ct)
        {
            if (app._chatPipeline == null || App.PetWindow == null) return Task.FromResult(false);
            try
            {
                // 插件从后台线程调用：管线同步前缀会读 Window/UI 状态（WPF 线程亲和），
                // 在 UI 线程执行到首个 await 即返回，异步部分自然在线程池继续
                var win = App.PetWindow!;
                return win.Dispatcher.Invoke(() => app._chatPipeline!.RunAsync(text, win));
            }
            catch (Exception ex)
            {
                Log.Error("plugins: SendChatAsync 失败", ex);
                return Task.FromResult(false);
            }
        }

        public Task<bool> SendEventAsync(string text, string? instruction, bool allowAgent, CancellationToken ct)
        {
            if (app._chatPipeline == null || App.PetWindow == null) return Task.FromResult(false);
            try
            {
                var win = App.PetWindow!;
                return win.Dispatcher.Invoke(() => app._chatPipeline!.RunAsync(text, win, asEvent: true, allowAgent, eventInstruction: instruction));
            }
            catch (Exception ex)
            {
                Log.Error("plugins: SendEventAsync 失败", ex);
                return Task.FromResult(false);
            }
        }

        public PetSnapshot GetPetInfo()
        {
            var win = App.PetWindow;
            if (win == null) return new PetSnapshot();
            try
            {
                // WPF Window 属性（Left/Top/Width/Height 等）线程亲和：必须 UI 线程读
                if (win.Dispatcher.CheckAccess()) return win.GetSnapshot();
                return win.Dispatcher.Invoke(win.GetSnapshot);
            }
            catch { return new PetSnapshot(); }
        }
    }
}