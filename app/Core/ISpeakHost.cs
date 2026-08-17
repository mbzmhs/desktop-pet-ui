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

public interface ISpeakHost
{
    Task SpeakAsync(string? text, byte[]? audio, string? emotion, string? expression);

    Task SpeakStreamAsync(string? text, IAsyncEnumerable<byte[]> audioSegments, string? emotion, string? expression);

    /// <summary>分段说话：整条回复显示一个气泡，播放时按段边界切换情绪。</summary>
    Task SpeakSegmentsAsync(string? fullText, IReadOnlyList<SpeechSegmentSpec> segments);
}