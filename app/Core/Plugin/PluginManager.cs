using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DesktopPetUi.Plugins;

namespace DesktopPetUi.Core.Plugin;

/// <summary>宿主能力桥（App 实现）：插件经 IPluginContext 间接调用。SendChatAsync 走完整聊天管线。</summary>
internal interface IPluginHostBridge
{
    Task<bool> SendChatAsync(string text, CancellationToken ct);
    PetSnapshot GetPetInfo();
}

/// <summary>池中一个已加载的插件（Enabled=热开关；Instance/Info 为 null 表示加载或注册失败）。</summary>
public sealed class LoadedPlugin
{
    /// <summary>dll 文件名（plugin.json 键、链顺序依据）。</summary>
    public required string DllName { get; init; }

    public IPlugin? Instance { get; set; }
    public PluginInfo? Info { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>加载/注册失败原因（设置页展示）。</summary>
    public string Error { get; set; } = "";

    /// <summary>显示名：注册成功用插件自报名，否则用 dll 名。</summary>
    public string DisplayName => Info?.Name ?? Path.GetFileNameWithoutExtension(DllName);
}

/// <summary>
/// 插件池（静态单例）：启动扫描 plugins/*.dll → 实例化 IPlugin → Register（带 plugin.json 持久化设定）。
/// 文件名顺序 = 消息链顺序。任何插件异常都被隔离，不影响宿主与其他插件。
/// </summary>
public static class PluginManager
{
    private static readonly List<LoadedPlugin> _plugins = new();
    private static readonly object _lock = new();
    private static IPluginHostBridge? _bridge;
    private static string _pluginsDir = "";

    /// <summary>plugin.json（exe 目录）：{ "plugins": { "<dll名>": { "enabled": bool, "settings": {...} } } }</summary>
    private sealed class StoreEntry
    {
        public bool Enabled { get; set; } = true;
        public Dictionary<string, JsonElement> Settings { get; set; } = new();
    }
    private sealed class StoreFile
    {
        public Dictionary<string, StoreEntry> Plugins { get; set; } = new();
    }

    public static bool IsLoaded => _bridge != null;

    /// <summary>启动加载（UI 线程）：扫描 + 注册。已禁用的插件不 Register（设置页可重新启用）。仅宿主 App 调用。</summary>
    internal static void LoadAll(string pluginsDir, IPluginHostBridge bridge)
    {
        lock (_lock) _plugins.Clear();
        _bridge = bridge;
        _pluginsDir = pluginsDir;

        var store = LoadStore();
        if (!Directory.Exists(pluginsDir))
        {
            Log.Info("plugins: 目录不存在 " + pluginsDir);
            return;
        }

        foreach (var dll in Directory.GetFiles(pluginsDir, "*.dll").OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.GetFileName(dll);
            var key = Path.GetFileNameWithoutExtension(dll);
            var entry = store.Plugins.TryGetValue(key, out var e) ? e : null;
            var enabled = entry?.Enabled ?? true; // 未记录过的默认启用
            var lp = new LoadedPlugin { DllName = file };

            try
            {
                var asm = Assembly.LoadFrom(dll);
                Type? impl = null;
                try
                {
                    impl = asm.GetTypes().FirstOrDefault(IsPluginType);
                }
                catch (ReflectionTypeLoadException rtle)
                {
                    // 常见原因：插件引用了不兼容版本的 PluginApi（基类型解析失败）——能救的类型继续找
                    impl = rtle.Types?.FirstOrDefault(IsPluginType);
                    if (impl == null) Log.Error("plugins: " + file + " 类型加载失败（可能 PluginApi 版本不匹配）：" + rtle.Message);
                }
                if (impl == null)
                {
                    Log.Info("plugins: " + file + " 无 IPlugin 实现，跳过");
                    continue;
                }

                var inst = (IPlugin)Activator.CreateInstance(impl)!;
                lp.Instance = inst;
                if (!enabled)
                {
                    lp.Enabled = false;
                    Log.Info("plugins: " + key + " 已禁用（plugin.json），未注册");
                }
                else
                {
                    var settings = entry?.Settings ?? new Dictionary<string, JsonElement>();
                    var info = inst.Register(new PluginContext(key, bridge), settings);
                    if (info == null)
                    {
                        lp.Error = "Register 返回 null";
                        Log.Error("plugins: " + key + " 注册失败（Register 返回 null）");
                    }
                    else
                    {
                        lp.Info = info;
                        var tools = info.Tools is { Count: > 0 } t ? "，工具: " + string.Join(",", t.Select(x => x.Name)) : "";
                        Log.Info("plugins: " + key + " 注册成功 v" + info.Version + "（" + info.Author + "）" + tools);
                    }
                }
            }
            catch (Exception ex)
            {
                lp.Instance = null;
                lp.Error = ex.Message;
                Log.Error("plugins: 加载 " + file + " 失败", ex);
            }

            lock (_lock) _plugins.Add(lp);
        }
    }

    /// <summary>退出清理：逐个 Shutdown（异常隔离）。</summary>
    public static void ShutdownAll()
    {
        foreach (var p in All.Where(p => p.Instance != null && p.Info != null))
        {
            try { p.Instance!.Shutdown(); }
            catch (Exception ex) { Log.Error("plugins: " + p.DisplayName + " Shutdown 异常", ex); }
        }
    }

    public static IReadOnlyList<LoadedPlugin> All
    {
        get { lock (_lock) return _plugins.ToList(); }
    }

    /// <summary>活动插件（启用 && 注册成功），文件名顺序 = 链顺序。</summary>
    private static List<LoadedPlugin> ActiveList() =>
        All.Where(p => p.Enabled && p.Instance != null && p.Info != null).ToList();

    private static bool IsPluginType(Type? t) =>
        t != null && !t.IsAbstract && !t.IsInterface && typeof(IPlugin).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null;

    /// <summary>消息链：LLM 回复流式结束后、工具解析前，按文件名顺序逐插件传递。异常保留上一段文本继续传。</summary>
    public static string RunReplyChain(string reply, ReplyContext ctx)
    {
        var active = ActiveList();
        if (active.Count == 0) return reply;
        foreach (var p in active)
        {
            try
            {
                var r = p.Instance!.PreprocessReply(reply, ctx);
                if (!string.IsNullOrEmpty(r)) reply = r;
            }
            catch (Exception ex)
            {
                Log.Error("plugins: " + p.Info!.Name + " PreprocessReply 异常（保留上一段文本继续链）", ex);
            }
        }
        return reply;
    }

    /// <summary>工具路由：[tool] 的 name 命中哪个活动插件的 ToolNames 就分发给谁（null=非插件工具）。</summary>
    public static LoadedPlugin? FindToolHandler(string name) =>
        ActiveList().FirstOrDefault(p => p.Info!.ToolNames?.Contains(name, StringComparer.OrdinalIgnoreCase) == true);

    /// <summary>该工具名是否属于某个活动插件（内置工具之外的合法性判定用）。</summary>
    public static bool IsPluginTool(string name) => FindToolHandler(name) != null;

    /// <summary>活动插件的工具描述行（拼进 systemPrompt 的 AVAILABLE TOOLS；无则返回 ""）。每次请求现取，禁用即失效。</summary>
    public static string PromptToolLines()
    {
        var lines = new List<string>();
        foreach (var p in ActiveList())
            if (p.Info!.Tools != null)
                foreach (var t in p.Info.Tools)
                    lines.Add("- " + t.Name + "(" + t.Parameters + "): " + t.Description);
        return string.Join("\n", lines);
    }

    /// <summary>执行插件工具（异常隔离，错误文本回喂模型）。</summary>
    public static async Task<string> ExecutePluginToolAsync(LoadedPlugin p, string name, System.Text.Json.Nodes.JsonObject args, string reason, CancellationToken ct)
    {
        var call = new ToolCall
        {
            Name = name,
            Reason = reason,
            Args = JsonSerializer.SerializeToElement(args),
        };
        try
        {
            return await p.Instance!.ExecuteToolAsync(call, ct);
        }
        catch (Exception ex)
        {
            Log.Error("plugins: " + p.Info?.Name + " 工具 " + name + " 执行异常", ex);
            return "错误：插件工具「" + name + "」执行失败：" + ex.Message;
        }
    }

    /// <summary>按显示名或 dll 名找插件。</summary>
    public static LoadedPlugin? Find(string key) =>
        All.FirstOrDefault(p => string.Equals(p.DisplayName, key, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Path.GetFileNameWithoutExtension(p.DllName), key, StringComparison.OrdinalIgnoreCase));

    /// <summary>设定列表（设置页渲染用）。</summary>
    public static IReadOnlyList<SettingDef> GetSettings(string key)
    {
        var p = Find(key);
        if (p?.Instance == null || !p.Enabled) return Array.Empty<SettingDef>();
        try { return p.Instance.GetSettings() ?? Array.Empty<SettingDef>(); }
        catch (Exception ex) { Log.Error("plugins: " + p.DisplayName + " GetSettings 异常", ex); return Array.Empty<SettingDef>(); }
    }

    /// <summary>更新设定：插件校验（不合规返回明确错误）；成功后宿主持久化到 plugin.json。</summary>
    public static SettingResult UpdateSetting(string key, string settingName, JsonElement value)
    {
        var p = Find(key);
        if (p?.Instance == null || !p.Enabled) return new SettingResult(false, "插件未加载或已禁用");
        SettingResult r;
        try { r = p.Instance.UpdateSetting(settingName, value); }
        catch (Exception ex) { return new SettingResult(false, "更新设定异常：" + ex.Message); }
        if (r.Ok) SaveSetting(key, settingName, value);
        return r;
    }

    /// <summary>热启用/禁用：禁用→Shutdown 并摘除（链/路由/prompt 立即不再含它）；启用→重新 Register（带持久化设定）。无需重启。</summary>
    public static void SetEnabled(string key, bool enabled)
    {
        var p = Find(key);
        if (p?.Instance == null || p.Enabled == enabled) return;
        p.Enabled = enabled;
        try
        {
            if (!enabled)
            {
                if (p.Info != null)
                {
                    p.Instance.Shutdown();
                    p.Info = null;
                }
                Log.Info("plugins: " + key + " 已禁用");
            }
            else
            {
                var settings = LoadStore().Plugins.TryGetValue(key, out var e) ? e.Settings : new Dictionary<string, JsonElement>();
                var info = p.Instance.Register(new PluginContext(key, _bridge!), settings);
                if (info == null)
                {
                    p.Enabled = false;
                    p.Error = "Register 返回 null";
                    Log.Error("plugins: " + key + " 重新启用失败（Register 返回 null）");
                }
                else
                {
                    p.Info = info;
                    Log.Info("plugins: " + key + " 已启用");
                }
            }
        }
        catch (Exception ex)
        {
            if (enabled) { p.Enabled = false; p.Error = ex.Message; }
            Log.Error("plugins: 切换 " + key + " 异常", ex);
        }
        SaveEnabled(key, p.Enabled && p.Info != null);
    }

    // ---------------- plugin.json ----------------

    private static string StorePath => Path.GetFullPath(Path.Combine(_pluginsDir, "..", "plugin.json"));

    private static StoreFile LoadStore()
    {
        try
        {
            if (File.Exists(StorePath))
                return JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(StorePath)) ?? new StoreFile();
        }
        catch (Exception ex) { Log.Error("plugins: plugin.json 读取失败（按空处理）", ex); }
        return new StoreFile();
    }

    private static void SaveStore(StoreFile store)
    {
        try { File.WriteAllText(StorePath, JsonSerializer.Serialize(store, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Log.Error("plugins: plugin.json 写入失败", ex); }
    }

    private static void SaveSetting(string key, string settingName, JsonElement value)
    {
        var store = LoadStore();
        if (!store.Plugins.TryGetValue(key, out var e)) store.Plugins[key] = e = new StoreEntry();
        e.Settings[settingName] = value;
        SaveStore(store);
    }

    private static void SaveEnabled(string key, bool enabled)
    {
        var store = LoadStore();
        if (!store.Plugins.TryGetValue(key, out var e)) store.Plugins[key] = e = new StoreEntry();
        e.Enabled = enabled;
        SaveStore(store);
    }

    private sealed class PluginContext(string name, IPluginHostBridge bridge) : IPluginContext
    {
        public Task<bool> SendChatAsync(string text, CancellationToken ct) => bridge.SendChatAsync(text, ct);
        public PetSnapshot GetPetInfo() => bridge.GetPetInfo();
        public void Log(string message) => DesktopPetUi.Log.Info("[plugin:" + name + "] " + message); // 全限定：避免与成员方法 Log 自引用
    }
}
