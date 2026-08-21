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
