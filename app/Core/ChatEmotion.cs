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
        text = Regex.Replace(text, "[ \t]{2,}", " ");
        return (token, text);
    }
}