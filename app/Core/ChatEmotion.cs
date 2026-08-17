using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace DesktopPetUi.Core;

public static class ChatEmotion
{
    public static readonly string[] Emotions =
    {
        "neutral", "happy", "sad", "angry", "surprised", "afraid", "shy", "confused",
    };

    public static bool IsKnown(string? e)
        => !string.IsNullOrEmpty(e) && Emotions.Any(x => x.Equals(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 从 LLM 回复中解析末尾的 [emotion] 标签。返回 (情感, 去掉标签后的正文)。
    /// 支持任意 [xxx] 标签（包括 tts-server 自定义情感），由调用方根据可用列表校验。
    /// </summary>
    public static (string? Emotion, string Text) Parse(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return (null, reply ?? "");
        var matches = Regex.Matches(reply, @"\[([A-Za-z0-9_\-]+)\]");
        if (matches.Count == 0) return (null, reply.Trim());
        var last = matches[matches.Count - 1];
        var token = last.Groups[1].Value;
        var text = (reply[..last.Index] + reply[(last.Index + last.Length)..]).Trim();
        return (token, NormalizeSpaces(text));
    }

    /// <summary>
    /// 解析回复中的多个情感标签，用于说话中途切换情绪。
    /// 约定：标签仅作中途切换用，不代表整段情绪、不做结尾标注；标签一律从正文剥离、不朗读。
    /// 返回 (情感, 文本) 分段：开头无标签的文本用 null（默认情绪）；标签间空文本段被跳过；
    /// 结尾标签后无文字则不起切换作用。相邻同情感段的合并由调用方按解析后的情感处理。
    /// </summary>
    public static List<(string? Emotion, string Text)> ParseSegments(string reply)
    {
        var segments = new List<(string? Emotion, string Text)>();
        if (string.IsNullOrWhiteSpace(reply)) return segments;
        var matches = Regex.Matches(reply, @"\[([A-Za-z0-9_\-]+)\]");
        if (matches.Count == 0)
        {
            segments.Add((null, NormalizeSpaces(reply)));
            return segments;
        }
        string? current = null;
        var pos = 0;
        foreach (Match m in matches)
        {
            var text = reply[pos..m.Index].Trim();
            if (text.Length > 0) segments.Add((current, NormalizeSpaces(text)));
            current = m.Groups[1].Value;
            pos = m.Index + m.Length;
        }
        var tail = reply[pos..].Trim();
        if (tail.Length > 0) segments.Add((current, NormalizeSpaces(tail)));
        return segments;
    }

    private static string NormalizeSpaces(string s)
        => Regex.Replace(s.Trim(), "[ \t]{2,}", " ");
}