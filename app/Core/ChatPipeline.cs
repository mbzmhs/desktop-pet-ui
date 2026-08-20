using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DesktopPetUi.Core.Agent;

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
    private readonly object _histLock = new(); // 历史会在 agent 线程池线程上修改，UI 线程并发读取（聊天窗重建消息），必须加锁
    private readonly AgentRunner _agent;
    private Action<string>? _debugLog;
    private string? _summary;
    private string? _lastProactive;
    private HashSet<string>? _availableEmotions;
    private DateTime _emotionsFetchedAt;

    private static readonly string[] WeekdaysEn = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };

    public Action<string>? Status { get; set; }
    /// <summary>每次组装出最终系统提示词时回调（调试窗口展示用）。</summary>
    public Action<string>? SystemPromptDebug { get; set; }
    public Action<string>? DebugLog
    {
        get => _debugLog;
        set
        {
            _debugLog = value;
            _agent.DebugLog = value;
        }
    }
    public event Action? HistoryChanged;
    /// <summary>agent 工具调用裁定事件（auto/allowed/denied），已持久化到 agent_ops.json 后在后台线程触发。</summary>
    public event Action<AgentOpRecord>? OpAdded;
    public bool IsRunning { get; private set; }

    /// <summary>线程安全的历史快照（加锁拷贝），UI 线程可放心遍历。</summary>
    public IReadOnlyList<ChatMessage> History { get { lock (_histLock) return _history.ToList(); } }
    public string? Summary => _summary;
    /// <summary>TTS 端可用自定义情感（显示层剥离情绪标签用）；null=尚未获取。</summary>
    public HashSet<string>? AvailableEmotions => _availableEmotions;

    public ChatPipeline(AppConfig config)
    {
        _config = config;
        _agent = new AgentRunner(config);
        _agent.OnOp = rec =>
        {
            AgentOpLog.Append(_config, rec); // 持久化审计轨迹（内部加锁，后台线程安全）
            OpAdded?.Invoke(rec);
        };
        _agent.OnMessage = m =>
        {
            // 工具往返写入长期历史（角色严格交替 user/assistant，Sanitize 不会误合并）；
            // 后续对话的上下文里就有完整操作轨迹，模型不再"只说不做"。膨胀由记忆压缩兜底。
            lock (_histLock) _history.Add(new ChatMessage { Role = m.Role, Content = m.Content });
            NotifyHistory();
        };
    }

    public void Restore(string? summary, IEnumerable<ChatMessage> history)
    {
        lock (_histLock)
        {
            _history.Clear();
            _history.AddRange(Sanitize(history));
        }
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
            clean.Add(new ChatMessage { Role = m.Role, Content = m.Content, Timestamp = m.Timestamp }); // 保留时间戳：UI 按它与操作记录合并排序
            lastRole = m.Role;
        }
        return clean;
    }

    /// <summary>
    /// 系统提示词组装。顺序 = 缓存友好度：稳定段在前（最大化 LLM 前缀缓存命中），
    /// 每轮都会变化的易变段（后台任务、当前时间）放最后。
    /// </summary>
    private string BuildSystemContent()
    {
        var parts = new List<string>();
        // —— 稳定前缀 ——
        if (!string.IsNullOrWhiteSpace(_config.EffectiveSystemPrompt))
            parts.Add(_config.EffectiveSystemPrompt);
        var lang = CharacterLang();
        parts.Add(lang == "ja"
            ? "LANGUAGE: Always reply in Japanese, no matter what language the user speaks. NEVER switch to the user's language."
            : lang == "en"
                ? "LANGUAGE: Always reply in English, no matter what language the user speaks. NEVER switch to the user's language."
                : "LANGUAGE: Always reply in Chinese (Simplified), no matter what language the user speaks. NEVER switch to the user's language.");
        var address = _config.EffectiveUserAddress;
        if (!string.IsNullOrWhiteSpace(address))
            parts.Add("ADDRESSING: Address the user as \"" + address + "\".");
        if (!string.IsNullOrWhiteSpace(_summary))
            parts.Add("MEMORY (summary of earlier conversations — already in the past, not just now): You may reference it to stay in character, but ALWAYS prioritize the current conversation.\n" + _summary);
        if (_config.Chat.Agent.Enabled)
        {
            var pathLine = AgentPathsLine(_config);
            if (!string.IsNullOrWhiteSpace(pathLine)) parts.Add(pathLine);
            parts.Add(AgentToolLine());
        }
        var emoLine = AvailableEmotionLine();
        if (!string.IsNullOrWhiteSpace(emoLine))
            parts.Add(emoLine);
        // —— 易变尾部（每轮变化，放最后以最小化缓存失效范围）——
        if (_config.Chat.Agent.Enabled)
        {
            JobManager.Prune();
            var jobLine = JobManager.ActiveSummary();
            if (!string.IsNullOrWhiteSpace(jobLine)) parts.Add(jobLine);
        }
        // 只到日期（不带时分）：时分每轮都变，会击穿前缀缓存；模型需要精确时间时会自己问/用工具查
        var now = DateTime.Now;
        parts.Add("CURRENT TIME: " + now.ToString("yyyy-MM-dd") + " (" + WeekdaysEn[(int)now.DayOfWeek] + ")");
        return string.Join("\n\n", parts);
    }

    /// <summary>Agent 工具协议（仅 agent 开启时注入）。英文书写以最大化指令遵循；回复语言由 LANGUAGE 段控制。</summary>
    private static string AgentToolLine()
    {
        return "[AGENT MODE] You can operate this computer on the user's behalf via tools.\n" +
               "PROTOCOL (follow EXACTLY):\n" +
                "- To call a tool, put ONE line in your reply: [tool]{\"name\":\"tool_name\",\"risk\":\"low|medium|high\",\"args\":{...}}[/tool] — at most ONE [tool] line per reply. You may add one short in-character sentence about what you are doing, then WAIT for the [result] message before continuing.\n" +
                "- A reply WITHOUT a [tool] line ENDS the task. Therefore: if you still need to do anything (search, read, run, create...), this reply MUST contain the [tool] line — NEVER just say \"let me look/try/check\" and stop without calling the tool. Reserve tool-free replies for final answers or when no action is needed.\n" +
                "- Example: [tool]{\"name\":\"list_dir\",\"risk\":\"low\",\"args\":{\"path\":\"C:\\\\Users\\\\me\\\\Desktop\"}}[/tool]\n" +
                "- NEVER put tool parameters (command, path, url, ...) at the top level. ALL parameters MUST go inside the \"args\" object. WRONG: {\"name\":\"run_powershell\",\"command\":\"dir\"}. RIGHT: {\"name\":\"run_powershell\",\"risk\":\"low\",\"args\":{\"command\":\"dir\",\"read_only\":true}}\n" +
                "- risk = your self-assessed danger level: low=read-only/no side effect, medium=creating new content, high=delete/overwrite/irreversible. When in doubt, rate HIGHER. The system grades independently and may ask the user to confirm; if the user declines, do NOT retry.\n" +
               "- Do not use tools for plain conversation.\n" +
               "AVAILABLE TOOLS:\n" +
               "- read_file(path): read a file (line limit applies)\n" +
               "- list_dir(path?): list directory contents\n" +
               "- search_files(name_pattern, root_dir?, max_results?): wildcard(* ?) filename search, e.g. *.mp3\n" +
               "- write_file(path, content): write a FILE's full content — creates it if missing, overwrites if existing (overwriting asks the user first). Files only, NEVER for directories.\n" +
               "- edit_file(path, old_string, new_string): replace one exact snippet in an EXISTING file (old_string must match the file exactly and be unique; ALWAYS prefer this over rewriting whole files with write_file)\n" +
               "- delete_file(path): delete a file/folder (always requires user consent)\n" +
               "- search_content(pattern, root_dir?, max_results?): regex content search across files (case-insensitive), returns path:lineNo:line\n" +
               "- web_fetch(url): fetch a web page's main text (http/https only, intranet addresses blocked; plain text, length-capped)\n" +
               "- ask_user(question): ask the user a question and wait for their typed answer (only when you genuinely need info or a choice; never ask what you can find out yourself)\n" +
               "- run_powershell(command, read_only, paths?): run PowerShell synchronously (tasks finishing within ~60s). read_only=true means read-only query. paths = array of ABSOLUTE file/dir paths the command reads/writes — list ALL of them; omitting some is safe (worst case: one extra confirm dialog)\n" +
               "- start_powershell(command, read_only, paths?): start a long task in background, returns a job id (use for tasks likely over 1 minute; paths as above)\n" +
               "- check_job(job_id): check a background job's progress and output\n" +
               "- observe_screen(): capture screenshots of the user's screens and view them. When the user asks what is on their screen / what they are looking at, ALWAYS call this FIRST and answer based only on what you actually see in the images.\n" +
               "HARD RULES:\n" +
               "- To create an empty DIRECTORY, use run_powershell with `New-Item -ItemType Directory` (or mkdir); NEVER use write_file for directories.\n" +
               "- NEVER invent file paths. Before write_file/edit_file/delete_file/search_content, verify the path exists with list_dir or search_files.\n" +
               "- If a tool returns an error, do NOT retry the identical call; fix the problem (e.g. list the directory to find the exact name) or tell the user what failed.";
    }

    /// <summary>工作目录 + 少量已知位置（只给桌面和用户目录，其余位置让模型自己查，不给它猜的素材）。</summary>
    private static string AgentPathsLine(AppConfig cfg)
    {
        var workDir = AgentTools.ResolveWorkDir(cfg);
        var (home, desktop, _, _) = AgentTools.KnownFolders();
        if (string.IsNullOrWhiteSpace(home)) return "";
        var parts = new List<string> { "WORKING DIRECTORY: " + workDir + " — base for relative paths and PowerShell; file operations there follow the working-dir permission setting. If the user does not specify a path, ALWAYS use this working directory as the root (create/read/list files under it), never elsewhere." };
        var entries = new List<string>();
        void Add(string label, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) entries.Add(label + ": " + path);
        }
        Add("User profile", home);
        Add("Desktop", desktop);
        if (entries.Count > 0) parts.Add("COMMON FOLDERS — " + string.Join(". ", entries) + ".");
        parts.Add("When the user refers to \"desktop\" or their user folder, use the paths above directly. For ANY other location (documents, downloads, etc.), do NOT guess paths — determine them yourself first (e.g. run_powershell `[Environment]::GetFolderPath('MyDocuments')`, or search_files/list_dir).");
        return string.Join("\n", parts);
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

    private string? AvailableEmotionLine()
    {
        var emotions = CharacterEmotions() ?? ChatEmotion.Emotions;
        var list = string.Join(" ", emotions.Select(x => "[" + x + "]"));
        return "EMOTION TAGS: Your reply MUST start with an emotion tag (e.g. '[happy]Hello!'). Tags are not read aloud and only switch the character's expression mid-speech; to change emotion mid-speech, insert a tag at that point. Do NOT append a tag at the end (ignored). 1-3 tags per reply is enough. Available tags: " + list;
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
        SystemPromptDebug?.Invoke(system);

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(system))
            messages.Add(new ChatMessage { Role = "system", Content = system });
        messages.AddRange(Sanitize(History)); // 加锁快照，避免与 UI 线程遍历竞争
        return messages;
    }

    public async Task<bool> RunAsync(string userText, ISpeakHost host)
    {
        await _gate.WaitAsync();
        IsRunning = true;
        try
        {
            lock (_histLock) _history.Add(new ChatMessage { Role = "user", Content = userText });
            NotifyHistory();
            Status?.Invoke("思考中…");

            var messages = await BuildMessagesAsync();
            string rawReply;
            if (_config.Chat.Agent.Enabled)
                rawReply = await _agent.RunAsync(messages, host); // 工具循环，中间往返不进历史
            else
                rawReply = await CompleteAsync(messages);
            lock (_histLock) _history.Add(new ChatMessage { Role = "assistant", Content = rawReply });
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

            // 把系统生成的沉默回合作为 user 消息一并记入历史，保证历史中 user/assistant 交替，
            // 避免连续堆积 assistant 发言导致模型下次误以为要说很长一段。
            lock (_histLock)
            {
                _history.Add(new ChatMessage { Role = "user", Content = silence });
                _history.Add(new ChatMessage { Role = "assistant", Content = rawReply });
            }
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
            var spec = new SpeechSegmentSpec { Emotion = emo, TtsEmotion = ttsEmo, Text = TtsText(text) };
            return (new List<SpeechSegmentSpec> { spec }, text);
        }

        var resolved = new List<SpeechSegmentSpec>();
        foreach (var (e, text) in parts)
        {
            var emo = e;
            if (emo != null && !available.Any(x => x.Equals(emo, StringComparison.OrdinalIgnoreCase))) emo = null;
            if (string.IsNullOrWhiteSpace(emo)) emo = tts.Emotion;
            var ttsEmo = (await ResolveTtsEmotionAsync(emo)) ?? "neutral";
            var ttsText = TtsText(text);
            if (string.IsNullOrWhiteSpace(ttsText)) continue;
            var last = resolved.Count > 0 ? resolved[^1] : null;
            if (last != null &&
                last.Emotion.Equals(emo, StringComparison.OrdinalIgnoreCase) &&
                last.TtsEmotion.Equals(ttsEmo, StringComparison.OrdinalIgnoreCase))
            {
                last.Text += ttsText;
            }
            else
            {
                resolved.Add(new SpeechSegmentSpec { Emotion = emo, TtsEmotion = ttsEmo, Text = ttsText });
            }
        }
        var fullText = string.Concat(parts.Select(p => p.Text));
        return (resolved, fullText);
    }

    /// <summary>发送给 TTS 的文本：按全局「朗读内心想法」开关决定是否剔除 （）() 括号内的内心想法。</summary>
    private string TtsText(string text)
        => _config.Chat.ReadInnerThoughts ? text : StripInnerThoughts(text);

    /// <summary>剔除 （）() 和 【】 内的内心想法 / 小动作内容。</summary>
    private static string StripInnerThoughts(string text)
        => Regex.Replace(text, "[（(【][^（）()【】]*[）)】]", "").Trim();

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

        int count;
        lock (_histLock) count = _history.Count;
        var overflowCount = max > 0 ? count - max : 0;
        var overflowChars = maxChars > 0 ? History.Sum(m => m.Content?.Length ?? 0) - maxChars : 0;
        if (overflowCount < 2 && overflowChars <= 0) return;

        var take = max > 0 ? Math.Max(max / 2, 2) : 2;
        if (take >= count) take = count / 2;
        if (take < 2) return;

        List<ChatMessage> chunk;
        lock (_histLock) chunk = _history.GetRange(0, take);
        MemoryArchive.Append(_config, chunk); // 先归档再压缩：即使摘要失败，原始记录也在 memory_archive.json 里
        _summary = await CompressAsync(chunk, _summary);
        lock (_histLock) _history.RemoveRange(0, Math.Min(take, _history.Count));
        int after;
        lock (_histLock) after = _history.Count;
        var unit = maxChars > 0 ? "chars" : "n/a";
        Log.Info($"History compressed: {count} msgs / {unit} -> {after} msgs");
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
                ep.Url, messages, ep.Model, 0.3, SummaryMaxTokens(), ep.ApiKey, ep.ExtraParams);
            return string.IsNullOrWhiteSpace(summary) ? (prevSummary ?? "") : summary.Trim();
        }
        catch (Exception ex)
        {
            Log.Error("CompressAsync failed", ex);
            return prevSummary ?? "";
        }
    }

    /// <summary>摘要输出的 token 上限：按「对话历史总字数上限」的 1/8 计算（约 4000 字 → 500 token），带上下限兜底。</summary>
    private int SummaryMaxTokens()
    {
        var budget = _config.Chat.ContextMaxChars;
        if (budget <= 0) budget = 4000;
        return Math.Clamp(budget / 8, 256, 2048);
    }

    public void ClearHistory()
    {
        lock (_histLock) _history.Clear();
        _summary = null;
        _lastProactive = null;
        NotifyHistory();
    }

    public void SetSummary(string? summary) => _summary = string.IsNullOrWhiteSpace(summary) ? null : summary;

    public void SetHistory(IEnumerable<ChatMessage> history)
    {
        lock (_histLock)
        {
            _history.Clear();
            _history.AddRange(Sanitize(history));
        }
        NotifyHistory();
    }

    public async Task<bool> CompressNowAsync()
    {
        lock (_histLock) { if (_history.Count == 0) return false; }
        await _gate.WaitAsync();
        try
        {
            List<ChatMessage> chunk;
            lock (_histLock) chunk = History.ToList();
            MemoryArchive.Append(_config, chunk); // 归档后再清空
            var result = await CompressAsync(chunk, _summary);
            lock (_histLock) _history.Clear();
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