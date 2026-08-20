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
    /// 从 LLM 回复中解析情感标签。返回 (情感, 去掉标签后的正文)。
    /// 支持任意 [xxx] 标签（包括 tts-server 自定义情感），由调用方根据可用列表校验。
    /// 多个标签并列（之间无正文）时只保留最后一个，其余剥离；正文中的所有标签一律去掉、不朗读。
    /// </summary>
    public static (string? Emotion, string Text) Parse(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return (null, reply ?? "");
        var matches = Regex.Matches(reply, @"\[([A-Za-z0-9_\-]+)\]");
        if (matches.Count == 0) return (null, NormalizeSpaces(reply));
        var last = matches[matches.Count - 1].Groups[1].Value;
        var text = Regex.Replace(reply, @"\[[A-Za-z0-9_\-]+\]", "");
        return (last, NormalizeSpaces(text));
    }

    /// <summary>
    /// 解析回复中的多个情感标签，用于说话中途切换情绪。
    /// 约定：标签仅作中途切换用，不代表整段情绪、不做结尾标注；标签一律从正文剥离、不朗读；
    /// 标签并列连写（之间无正文）时只保留最后一个。返回 (情感, 文本) 分段：开头无标签的文本用 null
    /// （默认情绪）；标签间空文本段被跳过；结尾标签后无文字则不起切换作用。相邻同情感段的合并由调用方按解析后的情感处理。
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

/// <summary>流式显示的情绪标签过滤器（变长扣留，替代固定窗口缓存）。
/// 每片增量只释放到「最后一个未验证的 [」之前的文本：这样半截标签（如 "[ha"）绝不会闪现；
/// 遇到完整 [word] 且 word 属于已知情感（内置 + TTS 自定义，与 ToDisplay 口径一致）→ 整段剔除不显示；
/// '[' 后超过 40 字符仍凑不成有效标签 → 判定是普通字面量，把 '[' 释放（防孤立 '[' 卡住后续全部输出）。
/// 延迟代价 ≤ 单个标签长度（最长约 11 字符），比固定 8 字窗口更安全且不漏不闪。
/// 流结束时调用 Flush()：剩余部分整体剥离已知标签后返回。</summary>
public sealed class StreamTagFilter
{
    private const int MaxTagSpan = 40; // '[' 到有效收尾的最大跨度，超过即按字面量处理
    private static readonly Regex TagWordRegex = new("^[A-Za-z0-9_\\-]+$", RegexOptions.Compiled);

    private readonly HashSet<string> _known;
    private string _pending = "";

    public StreamTagFilter(IEnumerable<string>? knownEmotions)
    {
        var set = new HashSet<string>(ChatEmotion.Emotions, StringComparer.OrdinalIgnoreCase);
        if (knownEmotions != null)
            foreach (var e in knownEmotions)
                if (!string.IsNullOrEmpty(e)) set.Add(e!);
        _known = set;
    }

    /// <summary>追加一片增量（delta 的增量部分，非累计值），返回新可释放的显示文本（可能为空）。</summary>
    public string Feed(string piece)
    {
        if (string.IsNullOrEmpty(piece)) return "";
        _pending += piece;
        var released = "";
        while (_pending.Length > 0)
        {
            var lb = _pending.IndexOf('[');
            if (lb < 0) { released += _pending; _pending = ""; break; } // 无未决 '['：全部释放

            var rb = _pending.IndexOf(']', lb + 1);
            if (rb >= 0 && IsKnownTag(_pending, lb, rb))
            {
                released += _pending[..lb];          // 标签前的正文
                _pending = _pending[(rb + 1)..];     // 剔除整个 [word]，继续扫（标签后可能紧跟另一个）
                continue;
            }
            if (rb >= 0)
            {
                // 第一个 ']' 收尾的候选无效（含空格/中文等不可能是情感标签）：'[' 是字面量，释放它继续
                released += _pending[..(lb + 1)];
                _pending = _pending[(lb + 1)..];
                continue;
            }
            if (_pending.Length - lb > MaxTagSpan)
            {
                // ']' 迟迟不来且跨度超限：不是标签，'[' 按字面量释放
                released += _pending[..(lb + 1)];
                _pending = _pending[(lb + 1)..];
                continue;
            }
            break; // 可能是半截标签：扣留到下一个增量
        }
        return released;
    }

    /// <summary>流结束：剩余扣留部分剥离所有完整已知标签后整体返回（未知 '[' 序列原样显示）。</summary>
    public string Flush()
    {
        var s = _pending;
        _pending = "";
        if (s.Length == 0) return "";
        return Regex.Replace(s, @"\[([A-Za-z0-9_\-]+)\]", m => _known.Contains(m.Groups[1].Value) ? "" : m.Value);
    }

    private bool IsKnownTag(string s, int lb, int rb)
    {
        var word = s[(lb + 1)..rb];
        return TagWordRegex.IsMatch(word) && _known.Contains(word);
    }
}