namespace BiliLivePlugin;

/// <summary>直播间事件类型。</summary>
internal enum LiveKind
{
    Danmaku,  // 聊天弹幕（DANMU_MSG）
    Gift,     // 礼物（SEND_GIFT）
    Sc,       // 醒目留言 / 超级弹弹（SUPER_CHAT_MESSAGE）
    Interact, // 互动（INTERACT_WORD_V2：关注/特别关注/互粉/分享；进场 Entry 太频繁不响应）
}

/// <summary>一条已解析的直播间事件（值语义，跨线程安全）。</summary>
internal sealed record LiveEvent(LiveKind Kind, string User, long Mid, string Text, double PriceYuan)
{
    /// <summary>Danmaku=弹幕内容；Gift=「礼物名 xN」；Sc=留言内容。</summary>
}

/// <summary>过滤规则（UpdateSetting 时整体重建，读侧无锁）。</summary>
internal sealed class LiveFilter
{
    public bool RespondDanmaku { get; init; } = true;
    public bool RespondGift { get; init; } = true;
    public bool RespondSc { get; init; } = true;
    public bool RespondInteract { get; init; } = true;
    public double MinGiftPrice { get; init; }
    public double MinScPrice { get; init; }
    public HashSet<string> BlockKeywords { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<long> BlockUsers { get; init; } = new();

    /// <summary>true=放行。顺序：类型开关 → 屏蔽用户 → 价格阈值 → 关键词。</summary>
    public bool Pass(LiveEvent e)
    {
        switch (e.Kind)
        {
            case LiveKind.Danmaku: if (!RespondDanmaku) return false; break;
            case LiveKind.Gift:
                if (!RespondGift) return false;
                if (e.PriceYuan < MinGiftPrice) return false;
                break;
            case LiveKind.Sc:
                if (!RespondSc) return false;
                if (e.PriceYuan < MinScPrice) return false;
                break;
            case LiveKind.Interact: if (!RespondInteract) return false; break;
        }
        if (e.Mid > 0 && BlockUsers.Contains(e.Mid)) return false;
        foreach (var kw in BlockKeywords)
            if ((e.User.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                 e.Text.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                return false;
        return true;
    }
}

/// <summary>发给 Pet 的消息格式化（明确标注直播间来源，让模型知道这不是用户本人输入）。</summary>
internal static class LiveFormat
{
    public static string Format(LiveEvent e) => e.Kind switch
    {
        LiveKind.Gift => $"[直播间] 礼物：{e.User}送出 {e.Text}（价值{Money(e.PriceYuan)}）",
        LiveKind.Sc => $"[直播间] 醒目留言：{e.User}留言「{e.Text}」（{Money(e.PriceYuan)}）",
        LiveKind.Interact => $"[直播间] 互动：{e.User}{e.Text}",
        _ => string.IsNullOrEmpty(e.User) ? $"[直播间] 弹幕：「{e.Text}」" : $"[直播间] 弹幕：{e.User}说「{e.Text}」",
    };

    /// <summary>窗口合并后的多条弹幕（保持 FIFO 顺序编号）。</summary>
    public static string FormatBatch(IReadOnlyList<LiveEvent> batch)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("[直播间] 弹幕（").Append(batch.Count).Append("条）：\n");
        for (var i = 0; i < batch.Count; i++)
        {
            sb.Append(i + 1).Append(". ");
            if (!string.IsNullOrEmpty(batch[i].User)) sb.Append(batch[i].User).Append("说");
            sb.Append("「").Append(batch[i].Text).Append("」\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>价格显示：整数不带小数（¥5），非整数一位小数（¥0.5）。</summary>
    public static string Money(double yuan) =>
        Math.Abs(yuan - Math.Round(yuan)) < 0.01 ? "¥" + ((long)Math.Round(yuan)).ToString("N0") : "¥" + yuan.ToString("0.0");
}
