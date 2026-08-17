using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPetUi.Core;

public sealed class MemoryFile
{
    public string? Summary { get; set; }
    public List<ChatMessage> History { get; set; } = new();
}

public sealed class ChatPipeline : IDisposable
{
    private readonly AppConfig _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<ChatMessage> _history = new();
    private string? _summary;
    private string? _lastProactive;
    private string? _screenNote;
    private HashSet<string>? _availableEmotions;
    private DateTime _emotionsFetchedAt;

    private static readonly string[] Weekdays = { "日", "一", "二", "三", "四", "五", "六" };
    private static readonly string[] WeekdaysJa = { "日曜日", "月曜日", "火曜日", "水曜日", "木曜日", "金曜日", "土曜日" };
    private static readonly string[] WeekdaysEn = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };

    public Action<string>? Status { get; set; }
    public Action<string>? DebugLog { get; set; }
    public event Action? HistoryChanged;
    public bool IsRunning { get; private set; }

    public IReadOnlyList<ChatMessage> History => _history;
    public string? Summary => _summary;
    public string? ScreenNote => _screenNote;

    public ChatPipeline(AppConfig config) => _config = config;

    public void Restore(string? summary, IEnumerable<ChatMessage> history)
    {
        _history.Clear();
        _history.AddRange(Sanitize(history));
        _summary = string.IsNullOrWhiteSpace(summary) ? null : summary;
        HistoryChanged?.Invoke();
    }

    private void NotifyHistory() => HistoryChanged?.Invoke();

    private static List<ChatMessage> Sanitize(IEnumerable<ChatMessage> history)
    {
        var clean = new List<ChatMessage>();
        string? lastRole = null;
        foreach (var m in history)
        {
            if (m == null || string.IsNullOrWhiteSpace(m.Content)) continue;
            if (lastRole == m.Role && clean.Count > 0)
            {
                clean[clean.Count - 1].Content += "\n" + m.Content;
                continue;
            }
            clean.Add(new ChatMessage { Role = m.Role, Content = m.Content });
            lastRole = m.Role;
        }
        return clean;
    }

    private string BuildSystemContent()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_config.EffectiveSystemPrompt))
            parts.Add(_config.EffectiveSystemPrompt);
        var lang = CharacterLang();
        parts.Add(lang == "ja"
            ? "【言語ルール】ユーザーがどの言語で話しかけても、あなたは必ず日本語で返答してください。絶対にユーザーの言語に合わせてはいけません。"
            : lang == "en"
                ? "【Language rule】No matter what language the user speaks, always reply in English. Never switch to the user's language."
                : "【语言规则】无论用户用什么语言说话，你都必须用中文回复，绝不能跟着用户换语言。");
        var now = DateTime.Now;
        if (lang == "ja")
        {
            parts.Add("【現在時刻】現在は" + now.ToString("yyyy年M月d日") + "（" + WeekdaysJa[(int)now.DayOfWeek] + "）" + now.ToString("HH:mm") + "です。");
        }
        else if (lang == "en")
        {
            parts.Add("【Current time】It is " + now.ToString("yyyy-MM-dd") + " (" + WeekdaysEn[(int)now.DayOfWeek] + ") " + now.ToString("HH:mm") + ".");
        }
        else
        {
            parts.Add("【当前时间】" + now.ToString("yyyy年M月d日") + " 星期" + Weekdays[(int)now.DayOfWeek] + " " + now.ToString("HH:mm"));
        }
        var address = _config.EffectiveUserAddress;
        if (!string.IsNullOrWhiteSpace(address))
        {
            parts.Add(lang == "ja"
                ? "【ユーザーの呼び方】ユーザーのことを「" + address + "」と呼んでください。"
                : lang == "en"
                    ? "【Addressing the user】Address the user as \"" + address + "\"."
                    : "【对用户的称呼】请用「" + address + "」来称呼用户。");
        }
        if (!string.IsNullOrWhiteSpace(_screenNote))
        {
            parts.Add(lang == "ja"
                ? "【今観察しているユーザーのデスクトップ画面】\n" + Truncate(_screenNote, 400)
                : lang == "en"
                    ? "【What the pet currently observes on the user's desktop】\n" + Truncate(_screenNote, 400)
                    : "【宠物此刻观察到的用户桌面画面】\n" + Truncate(_screenNote, 400));
        }
        if (!string.IsNullOrWhiteSpace(_summary))
        {
            parts.Add(lang == "ja"
                ? "【過去の記憶まとめ】以下はもっと前の会話の記憶要約で、既に過ぎ去った過去の出来事です。直前に起きたことではありません。キャラを保つために引用しても構いませんが、現在の会話を優先してください。\n" + _summary
                : lang == "en"
                    ? "【Memory summary from the past】The following is a summary of memories from earlier conversations; these are already in the past, not just now. You may reference it to stay in character, but prioritize the current conversation.\n" + _summary
                    : "【过去的记忆摘要】以下是更早之前对话的记忆摘要，属于已经过去的事，不是刚刚发生的。可以引用它延续人设，但请以当前对话为准。\n" + _summary);
        }
        var emoLine = AvailableEmotionLine(lang);
        if (!string.IsNullOrWhiteSpace(emoLine))
            parts.Add(emoLine);
        return string.Join("\n\n", parts);
    }

    /// <summary>角色语言：优先用 TTS 的 text_lang（zh/ja/en），未明确时按系统提示词内容推断。</summary>
    private string CharacterLang()
    {
        var lang = _config.EffectiveTextLang;
        if (lang == "zh" || lang == "ja" || lang == "en") return lang;
        var sys = _config.EffectiveSystemPrompt;
        if (string.IsNullOrWhiteSpace(sys)) return "zh";
        foreach (var c in sys)
        {
            if (c >= '\u3040' && c <= '\u30ff') return "ja";   // 假名
            if (c >= '\u4e00' && c <= '\u9fff') return "zh";   // 汉字
        }
        return "en";
    }

    /// <summary>当前角色文件夹下可用于展示的情感子文件夹（排除 idle）。</summary>
    private IReadOnlyList<string>? CharacterEmotions()
    {
        try
        {
            var name = _config.Character.Current;
            if (string.IsNullOrWhiteSpace(name)) return null;
            var dir = Path.Combine(_config.CharacterDir, name);
            if (!Directory.Exists(dir)) return null;
            var idle = _config.Character.IdleEmotion;
            var list = Directory.GetDirectories(dir)
                .Select(Path.GetFileName)
                .Where(d => !string.IsNullOrWhiteSpace(d) &&
                            !string.Equals(d, idle, StringComparison.OrdinalIgnoreCase))
                .Select(d => d!)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return list.Count > 0 ? list : null;
        }
        catch
        {
            return null;
        }
    }

    private string? AvailableEmotionLine(string lang)
    {
        var emotions = CharacterEmotions() ?? ChatEmotion.Emotions;
        var list = string.Join(" ", emotions.Select(x => "[" + x + "]"));
        return lang == "ja"
            ? "【感情タグ】返答は必ず感情タグで始めてください（例：「[happy]こんにちは！」）。タグは読み上げられず、立ち絵の感情切り替えにのみ使われます。会話途中で感情を変える場合も、その位置にタグを挿入してください。タグを連続して並べる場合は先頭の1つだけが有効です。文末にはタグを付けないでください（無効）。1回の返答につき1〜3個で十分です。使えるタグ：" + list
            : lang == "en"
                ? "【Emotion tags】Your reply MUST start with an emotion tag (e.g. '[happy]Hello!'). Tags are not read aloud and only switch the character's expression mid-speech. To change emotion mid-speech, insert a tag at that point. If tags are placed adjacent to each other, only the first one is used. Do not append a tag at the end (ignored). 1-3 tags per reply is enough. Available tags: " + list
                : "【情感标签】回复必须以一个情感标签开头（例如「[happy]你好呀！」）。标签不会被朗读，只在说话中途切换立绘情绪。中途要切换情绪时，在切换位置插入标签即可。标签并列连写时只保留第一个。结尾不要加标签（无效），每次回复 1~3 个即可。只能从以下可选标签中选择：" + list;
    }

    private async Task EnsureEmotionsAsync()
    {
        var tts = _config.EffectiveTts();
        if (string.Equals(tts.Provider, "windows", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tts.Provider, "none", StringComparison.OrdinalIgnoreCase))
        {
            _availableEmotions = null;
            return;
        }
        if (_availableEmotions != null && (DateTime.UtcNow - _emotionsFetchedAt).TotalSeconds < 60) return;
        _availableEmotions = await TtsClient.GetAvailableEmotionsAsync(tts.Url);
        _emotionsFetchedAt = DateTime.UtcNow;
        if (_availableEmotions != null && _availableEmotions.Count > 0)
            DebugLog?.Invoke("[" + DateTime.Now.ToString("HH:mm:ss") + "] 可用情感: " + string.Join(", ", _availableEmotions.OrderBy(x => x)));
    }

    public async Task<string?> ObserveScreenAsync()
    {
        try
        {
            var image = await Task.Run(() => ScreenCapture.CaptureCursorScreenAsBase64());
            if (string.IsNullOrEmpty(image)) return null;
            var lang = _config.EffectiveTextLang;
            string sysObs, userObs;
            if (lang == "ja")
            {
                sysObs = "あなたはユーザーのデスクトップの様子を観察するペットです。";
                userObs = "この画面画像に何が表示されているか簡潔に説明してください。開いているアプリ、テキスト、状況など具体的に。日本語で2〜3文。";
            }
            else if (lang == "en")
            {
                sysObs = "You are a pet assistant observing the user's desktop.";
                userObs = "Briefly describe what is shown in this screenshot. Mention open apps, text, and context specifically. 2-3 sentences in English.";
            }
            else
            {
                sysObs = "你是一个观察用户桌面的宠物助手。";
                userObs = "请简要描述这张屏幕截图里的内容。具体说明打开的应用程序、文字和大致情况。用简体中文，2到3句。";
            }
            var messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = sysObs },
                new() { Role = "user", Content = userObs },
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var ep = _config.EffectiveLlm();
            var desc = await LlamaClient.CompleteVisionAsync(
                ep.Url, messages, image,
                ep.Model, 0.2, 300, ep.ApiKey, ep.ExtraParams, cts.Token);
            _screenNote = Truncate(desc, 400);
            DebugLog?.Invoke("[" + DateTime.Now.ToString("HH:mm:ss") + "] 观察桌面: " + _screenNote);
            return _screenNote;
        }
        catch (Exception ex)
        {
            Log.Error("ObserveScreenAsync failed", ex);
            return null;
        }
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        return s[..max] + "…";
    }

    private async Task<List<ChatMessage>> BuildMessagesAsync(string? extraSystem = null)
    {
        await EnsureEmotionsAsync();
        var system = BuildSystemContent();
        if (!string.IsNullOrWhiteSpace(extraSystem))
            system = string.IsNullOrWhiteSpace(system) ? extraSystem : system + "\n\n" + extraSystem;

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(system))
            messages.Add(new ChatMessage { Role = "system", Content = system });
        messages.AddRange(Sanitize(_history));
        return messages;
    }

    public async Task<bool> RunAsync(string userText, ISpeakHost host)
    {
        await _gate.WaitAsync();
        IsRunning = true;
        try
        {
            _history.Add(new ChatMessage { Role = "user", Content = userText });
            NotifyHistory();
            Status?.Invoke("思考中…");

            var rawReply = await CompleteAsync(await BuildMessagesAsync());
            _history.Add(new ChatMessage { Role = "assistant", Content = rawReply });
            NotifyHistory();

            var (specs, fullText) = await PlanSegmentsAsync(rawReply);
            await SpeakPlannedAsync(host, fullText, specs);

            await MaybeCompressAsync();
            NotifyHistory();
            Status?.Invoke("");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("ChatPipeline.RunAsync failed", ex);
            Status?.Invoke("出错：" + ex.Message);
            return false;
        }
        finally
        {
            IsRunning = false;
            _gate.Release();
        }
    }

    public async Task<bool> RunProactiveAsync(ISpeakHost host)
    {
        await _gate.WaitAsync();
        IsRunning = true;
        try
        {
            var silence = RandomSilenceTurn();
            var messages = await BuildMessagesAsync(ProactiveInstruction());
            if (messages.Count == 0 || messages[^1].Role != "user")
                messages.Add(new ChatMessage { Role = "user", Content = silence });

            var rawReply = await CompleteAsync(messages, _config.EffectiveProactiveTemperature);
            if (_lastProactive != null && IsNearRepeat(rawReply, _lastProactive))
            {
                messages[^1] = new ChatMessage { Role = "user", Content = messages[^1].Content + "\n" + ProactiveRepeatInstruction() };
                rawReply = await CompleteAsync(messages, _config.EffectiveProactiveTemperature);
            }
            _lastProactive = rawReply;

            var (specs, fullText) = await PlanSegmentsAsync(rawReply);
            await SpeakPlannedAsync(host, fullText, specs);

            _history.Add(new ChatMessage { Role = "assistant", Content = rawReply });
            await MaybeCompressAsync();
            NotifyHistory();
            Status?.Invoke("");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("ChatPipeline.RunProactiveAsync failed", ex);
            Status?.Invoke("");
            return false;
        }
        finally
        {
            IsRunning = false;
            _gate.Release();
        }
    }

    private string RandomSilenceTurn()
    {
        var lang = _config.EffectiveTextLang;
        string[] arr;
        if (lang == "ja")
            arr = new[]
            {
                "（しばらく沈黙が続いています。あなたから話しかけてください）",
                "（ユーザーはまだ何も言っていません。新しい話題を振ってください）",
                "（静かな時間が続いています。今日の出来事や気になることを話しかけてください）",
            };
        else if (lang == "en")
            arr = new[]
            {
                "(There has been a long silence. Please start a conversation.)",
                "(The user hasn't said anything yet. Bring up a new topic.)",
                "(It's been quiet for a while. Talk about today's events or something on your mind.)",
            };
        else
            arr = new[]
            {
                "（沉默了一会儿，请主动开口说话）",
                "（用户还没有说话，主动挑个新话题吧）",
                "（安静了一会儿，说说今天的事或你想到的事）",
            };
        return arr[Random.Shared.Next(arr.Length)];
    }

    private string ProactiveInstruction()
    {
        var lang = _config.EffectiveTextLang;
        if (lang == "ja")
            return "今はあなたが話しかける番です。ユーザーはしばらく話していません。過去の会話（摘要）の続きではなく、新しい話題（今日の出来事・趣味・相手の近況など）を1つ話しかけてください。1文以内、同じフレーズや同じ話題の繰り返しは避けてください。文の先頭に感情タグを付けてください。";
        if (lang == "en")
            return "It's your turn to start a conversation. The user has been silent for a while. Don't continue the past conversation (summary); instead bring up a new topic (today's events, hobbies, how the user is doing, etc.). Say it in at most one sentence, avoid repeating the same phrases or topics. Start with one emotion tag.";
        return "现在轮到你主动开口了。用户已经沉默了一会儿。不要接着之前的话题（摘要）继续，而是挑一个新话题（今天发生的事、兴趣爱好、对方近况等）主动说一句。不超过一句话，避免重复说过的话或话题。开头带上情感标签。";
    }

    private string ProactiveRepeatInstruction()
    {
        var lang = _config.EffectiveTextLang;
        if (lang == "ja") return "（さっきと同じ内容を言いました。別の新しい話題で話しかけてください）";
        if (lang == "en") return "(You just said the same thing. Please start a conversation with a different new topic.)";
        return "（刚才说了重复的内容，请换个新话题搭话）";
    }

    private async Task<string> CompleteAsync(List<ChatMessage> messages, double? temperatureOverride = null)
    {
        var ep = _config.EffectiveLlm();
        DebugLog?.Invoke(FormatRequest(ep.Url, ep.Model, messages));
        var reply = await LlamaClient.CompleteAsync(
            ep.Url,
            messages,
            ep.Model,
            temperatureOverride ?? _config.EffectiveTemperature,
            _config.EffectiveMaxTokens,
            ep.ApiKey,
            ep.ExtraParams);
        DebugLog?.Invoke(FormatReply(reply));
        return reply;
    }

    private static string FormatRequest(string url, string model, List<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] → llama ")
          .Append(url).Append("  model=").Append(model).AppendLine();
        foreach (var m in messages)
            sb.Append(m.Role).Append(": ").Append(m.Content).AppendLine();
        return sb.ToString().TrimEnd();
    }

    private static string FormatReply(string reply)
    {
        return "[" + DateTime.Now.ToString("HH:mm:ss") + "] ← llama 回复:\n" + reply;
    }

    /// <summary>把回复拆成（可能的）分段说话计划：单段沿用旧的单情感解析；多段逐段解析、校验情绪并合并相邻同情感。</summary>
    private async Task<(List<SpeechSegmentSpec> Specs, string FullText)> PlanSegmentsAsync(string rawReply)
    {
        var tts = _config.EffectiveTts();
        var available = CharacterEmotions() ?? ChatEmotion.Emotions;
        var parts = ChatEmotion.ParseSegments(rawReply);

        if (parts.Count <= 1)
        {
            var (emo, text) = ChatEmotion.Parse(rawReply);
            if (emo != null && !available.Any(x => x.Equals(emo, StringComparison.OrdinalIgnoreCase))) emo = null;
            if (string.IsNullOrWhiteSpace(emo)) emo = tts.Emotion;
            var ttsEmo = (await ResolveTtsEmotionAsync(emo)) ?? "neutral";
            var spec = new SpeechSegmentSpec { Emotion = emo, TtsEmotion = ttsEmo, Text = text };
            return (new List<SpeechSegmentSpec> { spec }, spec.Text);
        }

        var resolved = new List<SpeechSegmentSpec>();
        foreach (var (e, text) in parts)
        {
            var emo = e;
            if (emo != null && !available.Any(x => x.Equals(emo, StringComparison.OrdinalIgnoreCase))) emo = null;
            if (string.IsNullOrWhiteSpace(emo)) emo = tts.Emotion;
            var ttsEmo = (await ResolveTtsEmotionAsync(emo)) ?? "neutral";
            var last = resolved.Count > 0 ? resolved[^1] : null;
            if (last != null &&
                last.Emotion.Equals(emo, StringComparison.OrdinalIgnoreCase) &&
                last.TtsEmotion.Equals(ttsEmo, StringComparison.OrdinalIgnoreCase))
            {
                last.Text += text;
            }
            else
            {
                resolved.Add(new SpeechSegmentSpec { Emotion = emo, TtsEmotion = ttsEmo, Text = text });
            }
        }
        var fullText = string.Concat(parts.Select(p => p.Text));
        return (resolved, fullText);
    }

    /// <summary>按说话计划播放：单段沿用旧的单情感路径；多段走分段并行播放。</summary>
    private async Task SpeakPlannedAsync(ISpeakHost host, string fullText, IReadOnlyList<SpeechSegmentSpec> specs)
    {
        Status?.Invoke("合成语音…");
        var tts = _config.EffectiveTts();
        var useStream = !string.Equals(tts.Provider, "none", StringComparison.OrdinalIgnoreCase) &&
                        tts.Streaming &&
                        string.Equals(tts.Provider, "gptsovits", StringComparison.OrdinalIgnoreCase);
        if (specs.Count == 1)
        {
            var spec = specs[0];
            if (useStream)
            {
                try
                {
                    await host.SpeakStreamAsync(fullText,
                        TtsClient.SynthesizeStreamAsync(tts.Url, spec.Text, tts, spec.TtsEmotion, stopPrev: true),
                        spec.Emotion, expression: null);
                }
                catch (Exception ex)
                {
                    Log.Error("TTS stream failed (bubble still shown)", ex);
                    Status?.Invoke("语音合成失败，仅显示文字…");
                }
                return;
            }

            byte[]? audio = null;
            if (!string.Equals(tts.Provider, "none", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    (audio, _) = await TtsClient.SynthesizeAsync(tts.Url, spec.Text, tts, spec.TtsEmotion);
                }
                catch (Exception ex)
                {
                    Log.Error("TTS failed (bubble still shown)", ex);
                    Status?.Invoke("语音合成失败，仅显示文字…");
                    audio = null;
                }
            }
            Status?.Invoke(audio != null ? "播放中…" : "");
            await host.SpeakAsync(fullText, audio, spec.Emotion, expression: null);
            return;
        }

        await host.SpeakSegmentsAsync(fullText, specs);
    }

    /// <summary>仅当 TTS 服务器不支持所选情感时，回退为 neutral。</summary>
    private async Task<string?> ResolveTtsEmotionAsync(string? emotion)
    {
        var tts = _config.EffectiveTts();
        if (!string.Equals(tts.Provider, "gptsovits", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(emotion)) return emotion;
        await EnsureEmotionsAsync();
        if (_availableEmotions != null &&
            !_availableEmotions.Any(x => x.Equals(emotion, StringComparison.OrdinalIgnoreCase)))
            return "neutral";
        return emotion;
    }

    private static bool IsNearRepeat(string a, string b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (string.IsNullOrEmpty(na) || string.IsNullOrEmpty(nb)) return false;
        if (na == nb) return true;
        return na.Length > 4 && nb.Length > 4 &&
               (na.StartsWith(nb, StringComparison.Ordinal) || nb.StartsWith(na, StringComparison.Ordinal));
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length == 0 || sb[^1] != ' ') sb.Append(' ');
            }
            else
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }
        return sb.ToString().Trim();
    }

    private async Task MaybeCompressAsync()
    {
        var max = _config.Chat.ContextLength;
        var maxChars = _config.Chat.ContextMaxChars;
        if (max <= 0 && maxChars <= 0) return;

        var count = _history.Count;
        var overflowCount = max > 0 ? count - max : 0;
        var overflowChars = maxChars > 0 ? _history.Sum(m => m.Content?.Length ?? 0) - maxChars : 0;
        if (overflowCount < 2 && overflowChars <= 0) return;

        var take = max > 0 ? Math.Max(max / 2, 2) : 2;
        if (take >= count) take = count / 2;
        if (take < 2) return;

        var chunk = _history.GetRange(0, take);
        _summary = await CompressAsync(chunk, _summary);
        _history.RemoveRange(0, take);
        var unit = maxChars > 0 ? "chars" : "n/a";
        Log.Info($"History compressed: {count} msgs / {unit} -> {_history.Count} msgs");
    }

    private async Task<string> CompressAsync(List<ChatMessage> chunk, string? prevSummary)
    {
        try
        {
            var sb = new StringBuilder();
            var lang = _config.EffectiveTextLang;
            List<ChatMessage> messages;
            if (lang == "ja")
            {
                sb.AppendLine("以下の会話（以前の要約も含む）を、会話の言語（日本語）で簡潔な要約にしてください。");
                sb.AppendLine("ユーザーの好み・話題・出来事・約束・気分などの重要な情報を残してください。");
                sb.AppendLine("要約だけを出力してください。");
                if (!string.IsNullOrWhiteSpace(prevSummary)) sb.AppendLine("以前の要約：").AppendLine(prevSummary);
                sb.AppendLine("要約する会話：");
                foreach (var m in chunk) sb.Append(m.Role).Append(": ").Append(m.Content).AppendLine();

                messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = "あなたは会話の記憶をまとめるアシスタントです。" },
                    new() { Role = "user", Content = sb.ToString() },
                };
            }
            else if (lang == "en")
            {
                sb.AppendLine("Please summarize the following conversation (including any previous summary) concisely in English.");
                sb.AppendLine("Keep important information: the user's preferences, topics, events, promises, and mood.");
                sb.AppendLine("Output only the summary itself.");
                if (!string.IsNullOrWhiteSpace(prevSummary)) sb.AppendLine("Previous summary:").AppendLine(prevSummary);
                sb.AppendLine("Conversation to summarize:");
                foreach (var m in chunk) sb.Append(m.Role).Append(": ").Append(m.Content).AppendLine();

                messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = "You are an assistant that summarizes conversation memories." },
                    new() { Role = "user", Content = sb.ToString() },
                };
            }
            else
            {
                sb.AppendLine("请将下面的对话（包含之前的摘要）用简体中文整理成一份简洁的摘要。");
                sb.AppendLine("请保留用户的重要信息：喜好、话题、发生过的事、约定、情绪等。");
                sb.AppendLine("只输出摘要本身。");
                if (!string.IsNullOrWhiteSpace(prevSummary)) sb.AppendLine("之前的摘要：").AppendLine(prevSummary);
                sb.AppendLine("需要摘要的对话：");
                foreach (var m in chunk) sb.Append(m.Role).Append(": ").Append(m.Content).AppendLine();

                messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = "你是负责整理对话记忆的助手。" },
                    new() { Role = "user", Content = sb.ToString() },
                };
            }
            var ep = _config.EffectiveLlm();
            var summary = await LlamaClient.CompleteAsync(
                ep.Url, messages, ep.Model, 0.3, 256, ep.ApiKey, ep.ExtraParams);
            return string.IsNullOrWhiteSpace(summary) ? (prevSummary ?? "") : summary.Trim();
        }
        catch (Exception ex)
        {
            Log.Error("CompressAsync failed", ex);
            return prevSummary ?? "";
        }
    }

    public void ClearHistory()
    {
        _history.Clear();
        _summary = null;
        _lastProactive = null;
        NotifyHistory();
    }

    public void SetSummary(string? summary) => _summary = string.IsNullOrWhiteSpace(summary) ? null : summary;

    public void SetHistory(IEnumerable<ChatMessage> history)
    {
        _history.Clear();
        _history.AddRange(Sanitize(history));
        NotifyHistory();
    }

    public async Task<bool> CompressNowAsync()
    {
        if (_history.Count == 0) return false;
        await _gate.WaitAsync();
        try
        {
            var result = await CompressAsync(_history.ToList(), _summary);
            _history.Clear();
            _lastProactive = null;
            _summary = string.IsNullOrWhiteSpace(result) ? null : result.Trim();
            NotifyHistory();
            return !string.IsNullOrWhiteSpace(_summary);
        }
        catch (Exception ex)
        {
            Log.Error("CompressNowAsync failed", ex);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        try { _gate.Dispose(); } catch { }
    }
}