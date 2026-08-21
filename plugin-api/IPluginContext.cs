using System.Text.Json;

namespace DesktopPetUi.Plugins;

/// <summary>宿主提供给插件的能力（Register 时注入，整个生命周期有效）。</summary>
public interface IPluginContext
{
    /// <summary>
    /// 代替用户向宠物发消息：走完整聊天管线（进入历史、LLM 回复、TTS 朗读），与用户在聊天窗输入完全一致。
    /// 若当前有进行中的轮次会排队等待（不并发）。返回是否成功发出（宿主关闭聊天/被停止时为 false）。
    /// </summary>
    Task<bool> SendChatAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// 向宠物注入一条**第三方事件**（如直播间弹幕/礼物）：同样走完整管线并触发回复，但消息以"叙述者"身份
    /// 进入上下文（对模型呈现为 system 而非 user），历史与聊天窗也用独立样式——模型不会把它当成用户本人说的话。
    /// <paramref name="allowAgent"/>：本轮是否允许 agent 工具链。**默认 false**——第三方内容是不可信输入，
    /// 不应触发电脑操作等工具（防注入）；仅当插件确信内容安全时才传 true。
    /// <paramref name="instruction"/>：可选的**每事件指令**——插件针对这条具体事件告诉模型该如何处理
    /// （如"这是一份礼物，请向观众真诚道谢"）。宿主只负责把它拼进通用的事件触发词，不解释其含义；
    /// 传 null 则本轮只有宿主的通用框架。这样"每种事件怎么处理"完全由插件决定，宿主保持插件无关。
    /// 文本建议自带醒目标记前缀（如「【直播间】」）并在 system prompt 片段里解释其含义。排队语义同 <see cref="SendChatAsync"/>。
    /// </summary>
    Task<bool> SendEventAsync(string text, string? instruction = null, bool allowAgent = false, CancellationToken ct = default);

    /// <summary>获取当前 Pet 信息（角色/情绪/缩放/窗口位置等）。</summary>
    PetSnapshot GetPetInfo();

    /// <summary>写宿主日志（pet.log，带 [plugin:名称] 前缀）。</summary>
    void Log(string message);
}

/// <summary>Pet 当前状态快照（值拷贝，可安全缓存）。</summary>
public sealed class PetSnapshot
{
    /// <summary>当前角色名。</summary>
    public string Character { get; init; } = "";

    /// <summary>当前情绪标签（空=无/中性）。</summary>
    public string Emotion { get; init; } = "";

    /// <summary>立绘缩放倍数（1=原始大小）。</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>窗口在屏幕上的位置与尺寸（像素）。</summary>
    public int WindowLeft { get; init; }
    public int WindowTop { get; init; }
    public int WindowWidth { get; init; }
    public int WindowHeight { get; init; }

    /// <summary>聊天功能是否开启。</summary>
    public bool ChatEnabled { get; init; }

    /// <summary>Agent（工具）功能是否开启。</summary>
    public bool AgentEnabled { get; init; }
}

/// <summary>消息链上下文：这条回复的来源。</summary>
public sealed class ReplyContext
{
    /// <summary>"agent-step"（agent 中间步，可能含 [tool] 块）/ "final"（最终回答）/ "proactive"（主动搭话）。</summary>
    public string Source { get; init; } = "";

    /// <summary>是否为 agent 中间工具步（true 时文本里可能有 [tool]{...}[/tool]，处理时注意保留协议格式）。</summary>
    public bool IsAgentStep { get; init; }
}

/// <summary>路由给插件的工具调用。</summary>
public sealed class ToolCall
{
    /// <summary>工具名（命中本插件 ToolNames 才会分发过来）。</summary>
    public string Name { get; init; } = "";

    /// <summary>模型填写的 reason（这一步的目的，一句话）。</summary>
    public string Reason { get; init; } = "";

    /// <summary>args 对象（原始 JSON；按 ToolDefinition.Parameters 约定解析）。</summary>
    public JsonElement Args { get; init; }
}
