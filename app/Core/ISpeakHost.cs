using System.Collections.Generic;
using System.Threading.Tasks;

namespace DesktopPetUi.Core;

/// <summary>一段带情绪的说话计划（一条回复可能拆成多段，说话中途切换情绪）。</summary>
public sealed class SpeechSegmentSpec
{
    /// <summary>展示用情感（已含默认回退，非空）。</summary>
    public string Emotion { get; set; } = "";

    /// <summary>TTS 合成用情感（已含 neutral 回退，非空）。</summary>
    public string TtsEmotion { get; set; } = "";

    /// <summary>该段的正文（不含标签）。</summary>
    public string Text { get; set; } = "";
}

/// <summary>Agent 操作确认请求。</summary>
public sealed class ConfirmRequest
{
    /// <summary>简短动作标题（如"运行 PowerShell 命令"）；为空时回退到 Question 纯文本渲染。</summary>
    public string Title { get; set; } = "";
    /// <summary>操作详情全文（完整命令/路径，等宽字体展示，不省略关键信息）。</summary>
    public string Detail { get; set; } = "";
    /// <summary>模型自评风险：low/medium/high（空=未提供），渲染为彩色徽标。</summary>
    public string Risk { get; set; } = "";
    /// <summary>模型对风险的一句话说明。</summary>
    public string RiskNote { get; set; } = "";
    /// <summary>纯文本问题（Title 为空时的回退内容，也用于日志）。</summary>
    public string Question { get; set; } = "";
    /// <summary>可一键信任的目录（目标所在目录）；null 时隐藏"信任该目录"按钮。PowerShell 永远为 null。</summary>
    public string? TrustableDir { get; set; }
}

/// <summary>用户对确认气泡的选择。</summary>
public sealed class ConfirmResult
{
    /// <summary>是否放行本次操作（超时/关闭=false）。</summary>
    public bool Allowed { get; set; }
    /// <summary>用户是否同时选择了"信任该目录"（仅文件操作有效；PowerShell 不受信任目录影响）。</summary>
    public bool TrustFolder { get; set; }
}

/// <summary>用户对提问的回答（Answered=false 表示超时/取消，Text 为空）。</summary>
public sealed class AskResult
{
    public bool Answered { get; set; }
    public string Text { get; set; } = "";
}

public interface ISpeakHost
{
    Task SpeakAsync(string? text, byte[]? audio, string? emotion, string? expression);

    Task SpeakStreamAsync(string? text, IAsyncEnumerable<byte[]> audioSegments, string? emotion, string? expression);

    /// <summary>分段说话：整条回复显示一个气泡，播放时按段边界切换情绪。</summary>
    Task SpeakSegmentsAsync(string? fullText, IReadOnlyList<SpeechSegmentSpec> segments);

    /// <summary>弹出带 [确认][取消]（必要时另有[信任该目录]）按钮的确认气泡；超时/关闭按取消处理。</summary>
    Task<ConfirmResult> ConfirmAsync(ConfirmRequest request);

    /// <summary>弹出带输入框 + [发送][取消] 的气泡向用户提问，等待用户键入回答；超时/关闭按未回答处理。</summary>
    Task<AskResult> AskUserAsync(string question);
}