using System.Text.Json;

namespace DesktopPetUi.Plugins;

/// <summary>
/// 插件入口接口。宿主启动时扫描 plugins/*.dll，找到实现了本接口的公共类型（需有无参公共构造函数）并实例化，
/// 然后调用 <see cref="Register"/>。除 Register 外的成员均可在后台线程被调用——不要在其中触碰 WPF/WinForms UI。
/// </summary>
public interface IPlugin
{
    /// <summary>
    /// 注册（宿主启动时自动调用，UI 线程）。<paramref name="settings"/> 是本插件已持久化的设定
    /// （plugin.json；首次使用或无设定时为空字典），插件应据此初始化自身状态。
    /// 返回插件信息表示注册成功；返回 null 表示注册失败（宿主跳过该插件并记日志）。
    /// </summary>
    PluginInfo? Register(IPluginContext ctx, IReadOnlyDictionary<string, JsonElement> settings);

    /// <summary>
    /// 消息链预处理：LLM 回复流式结束以后、[tool] 解析/工具调用之前，按 plugins 目录文件名顺序逐插件传递，
    /// 每个插件收到上一段的输出并返回处理结果（可直接原样返回）。本插件抛异常时宿主保留上一段文本继续传，不会中断链。
    /// </summary>
    string PreprocessReply(string reply, ReplyContext ctx);

    /// <summary>
    /// 工具执行：当 [tool] 调用的 name 在本插件 <see cref="PluginInfo.ToolNames"/> 中时由宿主分发到这里。
    /// 直接执行（不弹权限确认），每次调用记入 agent_ops.json（verdict=plugin）。返回文本作为 [result] 回喂给模型。
    /// </summary>
    Task<string> ExecuteToolAsync(ToolCall call, CancellationToken ct);

    /// <summary>设定列表（名称/说明/数值类型）。宿主设置页据此渲染控件。</summary>
    IReadOnlyList<SettingDef> GetSettings();

    /// <summary>
    /// 更新设定：宿主只传名称+值。值不符合插件规定（类型错、超范围等）时返回 Ok=false 并附明确错误说明；
    /// 成功时插件应自行持久化/应用新值，宿主随后把该值写入 plugin.json。
    /// </summary>
    SettingResult UpdateSetting(string name, JsonElement value);

    /// <summary>禁用插件或宿主退出时调用（UI 线程），做资源清理。</summary>
    void Shutdown();
}

/// <summary>注册成功时返回的插件信息。</summary>
public sealed class PluginInfo
{
    /// <summary>唯一标识（plugin.json 的键、日志前缀）。建议与 dll 文件名一致。</summary>
    public required string Name { get; init; }

    public required string Version { get; init; }

    public required string Author { get; init; }

    /// <summary>功能介绍（设置页展示）。</summary>
    public string Description { get; init; } = "";

    /// <summary>注入 systemPrompt 的工具定义（可空=不提供工具）。与内置工具同风格拼进 AVAILABLE TOOLS 一节。</summary>
    public IReadOnlyList<ToolDefinition>? Tools { get; init; }

    /// <summary>本插件负责执行的工具名清单（可空）。[tool] 调用的 name 命中其中任一项时路由到 <see cref="IPlugin.ExecuteToolAsync"/>。</summary>
    public IReadOnlyList<string>? ToolNames { get; init; }
}

/// <summary>工具定义：拼进系统提示词的一行 "- name(params): description"。</summary>
public sealed class ToolDefinition
{
    /// <summary>工具名（全局唯一，勿与内置工具重名）。</summary>
    public required string Name { get; init; }

    /// <summary>用途说明（模型据此决定何时调用）。</summary>
    public required string Description { get; init; }

    /// <summary>参数说明文本，如 "path, content?"（? 表示可选）。</summary>
    public string Parameters { get; init; } = "";
}
