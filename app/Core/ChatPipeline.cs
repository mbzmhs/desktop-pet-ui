using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DesktopPetUi.Core.Agent;
using DesktopPetUi.Core.Plugin;
using DesktopPetUi.Plugins;

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
    private int _lastPromptTokens; // 最近一次真实上下文 token（API usage.prompt_tokens）
    private double _tokPerChar;    // 校准的 token/字 比率（0=尚未校准，按默认 ~1.5 字/token 折算）
    private int _lastSystemChars;  // 上一轮实际发送的系统提示词字数（压缩估算用：预算覆盖整次请求=系统+历史）
    private string? _lastProactive;
    private HashSet<string>? _availableEmotions;
    private DateTime _emotionsFetchedAt;

    private static readonly string[] WeekdaysEn = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };

    public Action<string>? Status { get; set; }
    /// <summary>IsRunning 变化时触发（finally 置 false 也触发）：UI 据此刷新停止按钮——
    /// 插件触发的轮次没有调用方兜底刷新，末次 Status("") 发出时 IsRunning 尚为 true，只有这个事件能通知"真正结束"。</summary>
    public Action<bool>? RunningChanged { get; set; }
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
    /// <summary>上下文用量更新（API usage.prompt_tokens；后台线程触发，UI 侧自行切线程）。</summary>
    public event Action? UsageChanged;
    /// <summary>历史压缩进行中变化（true=开始压缩，false=结束；后台线程触发）。压缩期间聊天窗应提示并锁定输入。</summary>
    public event Action<bool>? CompressingChanged;
    /// <summary>流式回复增量（参数=到目前为止的累计原始全文，含未剥离的情绪标签；后台线程触发，仅展示层用）。
    /// 仅在 StreamEnabled 且走流式路径时触发；TTS/历史仍按整条 rawReply 处理，与此无关。</summary>
    public event Action<string>? ReplyDelta;
    /// <summary>本次流式传输结束（均会触发一次；后台线程触发）。参数 completed：
    /// true=正常生成完毕（展示层移除临时气泡，正式版由历史重建）；
    /// false=被 [tool] 门控抑制/出错/停止（展示层冻结已显示文本去光标，保留到历史重建替换，避免"打到最后突然清空"）。</summary>
    public event Action<bool>? ReplyStreamEnd;
    public bool IsRunning { get; private set; }
    private CancellationTokenSource? _runCts;
    private bool _agentStreamSuppressed; // agent 当前流已出现 [tool]（中间工具步）：打字气泡已收尾，后续片不再转发

    /// <summary>手动停止当前运行：立即中止进行中的 LLM 请求/工具；用户发起的轮次会在历史留中断标记。不视为错误。</summary>
    public void Stop() => _runCts?.Cancel();

    /// <summary>停止并等待本轮完全收尾（含历史写入与中断标记，gate 释放为界）。切换角色前调用：
    /// 流式/非流式在途请求都会被 ct 中止，避免旧角色的回复/标记落到新角色的历史上。
    /// 超时不阻塞（正常只在长 TTS 播放中会触顶，而那时历史已写完，无竞态）。</summary>
    public async Task StopAndWaitAsync(int timeoutMs = 5000)
    {
        _runCts?.Cancel();
        if (!IsRunning) return;
        var got = false;
        try
        {
            got = await _gate.WaitAsync(timeoutMs);
        }
        catch (TimeoutException) { /* 超时继续：见上说明，历史写入已完成在 gate 释放之前 */ }
        if (got) _gate.Release(); // 只用作屏障，用完归还
    }

    /// <summary>是否正在压缩历史（摘要 LLM 调用中）。</summary>
    public bool IsCompressing { get; private set; }

    private void SetCompressing(bool v)
    {
        if (IsCompressing == v) return;
        IsCompressing = v;
        CompressingChanged?.Invoke(v);
    }

    /// <summary>最近一次真实上下文 token 数（API usage.prompt_tokens）；0=尚无完成请求。</summary>
    public int LastPromptTokens => _lastPromptTokens;

    /// <summary>校准的 token/字 比率（来自实际 usage÷发送字数）；未校准时按 ~1.5 字/token 折算。</summary>
    public double TokPerChar => _tokPerChar > 0 ? _tokPerChar : 1.0 / 1.5;

    /// <summary>有效上下文预算（token）：用户设置与「模型实际最大上下文−输出预留」取较小者。
    /// 模型上限由 /v1/models 提供；查不到时只按用户设置。0=不限。</summary>
    public int EffectiveContextBudget()
    {
        var user = _config.Chat.ContextMaxTokens;
        if (user <= 0) return 0;
        var ep = _config.EffectiveLlm();
        var modelMax = LlamaClient.ModelMaxContext(ep.Url, ep.Model);
        if (modelMax == null || modelMax <= 0) return user;
        var allow = modelMax.Value - _config.EffectiveMaxTokens - 512; // prompt+输出 max_tokens 都要装进模型上下文
        return Math.Max(1024, Math.Min(user, allow));
    }

    /// <summary>采样一次真实 token 用量：刷新显示值并校准折算比率。
    /// 只用于携带完整历史的请求（主聊天、agent 每步）；摘要压缩请求载荷不同，不在此采样。</summary>
    public void OnUsageSample(int promptTokens, int sentChars)
    {
        if (promptTokens <= 0) return;
        _lastPromptTokens = promptTokens;
        if (sentChars > 200)
        {
            var r = promptTokens / (double)sentChars;
            if (r > 0.1 && r < 2.0) _tokPerChar = Math.Clamp(r, 0.2, 1.5);
        }
        UsageChanged?.Invoke();
    }

    /// <summary>线程安全的历史快照（加锁拷贝），UI 线程可放心遍历。</summary>
    public IReadOnlyList<ChatMessage> History { get { lock (_histLock) return _history.ToList(); } }
    public string? Summary => _summary;
    /// <summary>TTS 端可用自定义情感（显示层剥离情绪标签用）；null=尚未获取。</summary>
    public HashSet<string>? AvailableEmotions => _availableEmotions;

    // ---------------- 情绪标签词表（显示剥离口径） ----------------
    private string _knownEmoChar = "";
    private HashSet<string> _allKnownEmotions = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _knownEmoAt;

    /// <summary>全部已知情绪标签 = 内置 ∪ 当前角色文件夹情感 ∪ TTS 端列表。
    /// 显示层（流式过滤/气泡剥离）一律用此口径：模型被提示词引导使用角色文件夹名，若只认 TTS 端列表，
    /// TTS 不可用或未注册该情感时标签会漏进显示文本（"表情标签不解析"）。30s 缓存兜住文件夹增删。</summary>
    public HashSet<string> AllKnownEmotions()
    {
        var cur = _config.Character.Current ?? "";
        if (cur != _knownEmoChar || (DateTime.UtcNow - _knownEmoAt).TotalSeconds > 30)
        {
            _knownEmoChar = cur;
            _knownEmoAt = DateTime.UtcNow;
            var set = new HashSet<string>(ChatEmotion.Emotions, StringComparer.OrdinalIgnoreCase);
            foreach (var e in CharacterEmotions() ?? Array.Empty<string>()) set.Add(e);
            if (_availableEmotions != null) foreach (var e in _availableEmotions) set.Add(e);
            _allKnownEmotions = set;
        }
        return _allKnownEmotions;
    }

    // ---------------- 工具步流结束时刻（后台时钟） ----------------
    private DateTime? _lastToolStepEndTs;
    private bool _agentStreamFirstDelta = true;

    /// <summary>最近一次 agent 工具步流式结束的后台线程时刻（与历史消息 Timestamp 同时钟域）。
    /// ChatWindow 冻结打字气泡时用它做"正式版已落历史"判定——UI 时钟与后台时钟跨域比较会因 UI 卡顿误判，
    /// 导致冻结纯文本气泡与正式版 Markdown 气泡同屏重叠闪烁。null=本流尚未结束过工具步。</summary>
    public DateTime? LastToolStepEndTs => _lastToolStepEndTs;

    // ---------------- 当前流起点（后台时钟） ----------------
    private DateTime? _streamStepStartTs;

    /// <summary>当前这条流（agent 步 / 普通回复 / 主动搭话）首个增量的后台线程时刻。
    /// ChatWindow 用它判定"临时打字气泡是否已被正式版取代"：历史里出现 ≥ 该时刻的 assistant 消息
    /// （工具步原文 aMsg / 最终正式版）即说明内容已有正式载体，不再重挂临时气泡，避免同内容双显。</summary>
    public DateTime? StreamStepStartTs => _streamStepStartTs;

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
            // Timestamp 原样携带：assistant 步=文本生成完毕时刻（工具裁定前），保证正文气泡排在自动放行记录之前
            lock (_histLock) _history.Add(new ChatMessage { Role = m.Role, Content = m.Content, Timestamp = m.Timestamp });
            NotifyHistory();
        };
        // agent 循环每步也带 system+完整历史，其 usage 同样代表上下文占用（显示与比率校准）
        _agent.OnUsage = (pt, sc) => OnUsageSample(pt, sc);
        // agent 各步流式增量 → 聊天窗打字气泡。[tool] 块由 StreamTagFilter 按区间吞掉（工具可在头部/中部/尾部，
        // 块两侧的正文照常放行显示）；这里只记录"本步含工具"，供流结束时决定 End(false) 还是 End(true)
        // 插件消息链：agent 各步与最终回复在流式结束以后、[tool] 解析之前逐插件传递（异常隔离）
        _agent.PreprocessReply = (reply, isAgentStep) =>
            PluginManager.RunReplyChain(reply, new ReplyContext { Source = isAgentStep ? "agent-step" : "final", IsAgentStep = isAgentStep });
        _agent.OnStreamDelta = soFar =>
        {
            if (_agentStreamFirstDelta)
            {
                _agentStreamFirstDelta = false;
                _lastToolStepEndTs = null; // 新流开始：上一工具步的结束时刻作废（防出错停止时误读旧值）
                _streamStepStartTs = DateTime.Now; // 本步流起点（后台时钟）：打字气泡"已被取代"判定用
            }
            if (!_agentStreamSuppressed && soFar.IndexOf("[tool]", StringComparison.OrdinalIgnoreCase) >= 0)
                _agentStreamSuppressed = true; // 本步是中间工具步（最终纯文字回答不含 [tool]，情绪标签不受影响）
            ReplyDelta?.Invoke(soFar);
        };
        _agent.OnStreamEnd = completed =>
        {
            var toolStep = _agentStreamSuppressed;
            _agentStreamSuppressed = false; // 本步流结束：复位给下一步的新流
            if (toolStep) _lastToolStepEndTs = DateTime.Now; // 后台时钟：与 aMsg.Timestamp 同时钟域，冻结气泡归位判定用
            _agentStreamFirstDelta = true;
            ReplyStreamEnd?.Invoke(completed && !toolStep); // 正常完成且非工具步=true（移除气泡）；工具步/出错=false（冻结收尾）
        };
        _agent.TokPerCharProvider = () => TokPerChar; // 硬护栏裁剪用同一校准比率
    }

    public void Restore(string? summary, IEnumerable<ChatMessage> history)
    {
        lock (_histLock)
        {
            _history.Clear();
            _history.AddRange(Sanitize(history));
        }
        _summary = string.IsNullOrWhiteSpace(summary) ? null : summary;
        _lastPromptTokens = 0; // 换角色/重置后上下文占用归零：标题栏隐藏 Context 显示，直到下一次请求拿到真实 usage
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
            parts.Add(AgentToolLine(PluginManager.PromptToolLines())); // 插件工具随活动列表现取（禁用即不再注入）
        }
        else
        {
            // 历史里可能有 agent 开启时的 [tool]/[result] 示例，模型会模仿——明确声明当前无工具可用
            parts.Add("AGENT MODE IS DISABLED: you have NO tools in this session, even if earlier messages show [tool] examples. If the user asks for computer operations (run commands, create files, browse...), reply in plain text explaining that the Agent feature must be enabled in settings first — NEVER output any [tool] block.");
        }
        var emoLine = AvailableEmotionLine();
        if (!string.IsNullOrWhiteSpace(emoLine))
            parts.Add(emoLine);
        // —— 易变尾部（每轮变化，放最后以最小化缓存失效范围）——
        if (_config.Chat.Agent.Enabled)
        {
            JobManager.Prune(); // 仅清理
            // [ACTIVE JOBS]/[TODO] 摘要都不再注入：状态每轮都可能变，会击穿前缀缓存；模型需要时自己用工具查（check_job / todo 工具）
        }
        // 只到日期（不带时分）：时分每轮都变，会击穿前缀缓存；模型需要精确时间时会自己问/用工具查
        var now = DateTime.Now;
        parts.Add("CURRENT TIME: " + now.ToString("yyyy-MM-dd") + " (" + WeekdaysEn[(int)now.DayOfWeek] + ")");
        // 活动插件的自定义提示片段（如直播插件的互动说明）：追加在 systemPrompt 最尾部，每次请求现取，禁用即不再注入
        var pluginPrompt = PluginManager.SystemPromptSuffix();
        if (!string.IsNullOrEmpty(pluginPrompt)) parts.Add(pluginPrompt);
        return string.Join("\n\n", parts);
    }

    /// <summary>Agent 工具协议（仅 agent 开启时注入）。英文书写以最大化指令遵循；回复语言由 LANGUAGE 段控制。
    /// pluginTools=活动插件的工具描述行（"" = 无插件工具）。</summary>
    private static string AgentToolLine(string pluginTools)
    {
        return "[AGENT MODE] You can operate this computer on the user's behalf via tools. " +
               "CURRENT STATE: agent mode is ENABLED right now — if any earlier message says tools/the Agent are disabled or unavailable, that is stale (the setting was off at the time); ignore it and call tools normally.\n" +
               "PROTOCOL (follow EXACTLY):\n" +
                 "- To call a tool, put ONE line in your reply: [tool]{\"name\":\"tool_name\",\"reason\":\"...\",\"risk\":\"low|medium|high\",\"args\":{...}}[/tool] — at most ONE [tool] line per reply. You may add one short in-character sentence about what you are doing, then WAIT for the [result] message before continuing.\n" +
                 "- EVERY call MUST include \"reason\": ONE short sentence explaining WHY this step is needed (the user sees it verbatim as the action's title). Write it in the same language you use for replies (see the LANGUAGE section above); keep it under 30 words; never repeat the command/path itself — the system already shows that.\n" +
                 "- A reply WITHOUT a [tool] line ENDS the task. Therefore: if you still need to do anything (search, read, run, create...), this reply MUST contain the [tool] line — NEVER just say \"let me look/try/check\" and stop without calling the tool. Reserve tool-free replies for final answers or when no action is needed.\n" +
                 "- Example: [tool]{\"name\":\"list_dir\",\"reason\":\"see what files are on the desktop\",\"risk\":\"low\",\"args\":{\"path\":\"C:\\\\Users\\\\me\\\\Desktop\"}}[/tool]\n" +
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
                "- ask_user(questions): ask the user one or SEVERAL questions in a SINGLE call. questions = array of {question: text, options?: [label, ...], multiple?: bool}. Options render as clickable buttons and the user can also type free text per question. ALWAYS provide options when the answer is a pick from a small set (≤6 labels); omit options only for open-ended questions; set multiple=true only if several picks are valid. Use it only when you genuinely need info or a decision — never ask what you can find out yourself\n" +
               "- run_powershell(command, read_only, paths?): run PowerShell synchronously (tasks finishing within ~60s). read_only=true means read-only query. paths = array of ABSOLUTE file/dir paths the command reads/writes — list ALL of them; omitting some is safe (worst case: one extra confirm dialog)\n" +
               "- start_powershell(command, read_only, paths?): start a long task in background, returns a job id (use for tasks likely over 1 minute; paths as above)\n" +
                "- check_job(job_id): check a background job's progress and output\n" +
                "- todo(action, text?, id?): manage the user-visible task list (shown in a dedicated window). action=add(text=...) to create items, done/undone(id=...), remove(id=...), clear, list. For any non-trivial multi-step task: FIRST add the planned steps, then mark each done IMMEDIATELY as you finish it — the user watches progress live. Items must be concrete, independently completable steps (e.g. \"find file X\", \"write a.txt\") — NEVER create an umbrella/summary item like \"finish the whole task\"; never leave an item unmarked after its step is actually done.\n" +
               "- observe_screen(): capture screenshots of the user's screens and view them. When the user asks what is on their screen / what they are looking at, ALWAYS call this FIRST and answer based only on what you actually see in the images.\n" +
               (pluginTools.Length > 0 ? pluginTools + "\n" : "") + // 插件工具（活动插件，禁用即不再注入）
               "HARD RULES:\n" +
               "- To create an empty DIRECTORY, use run_powershell with `New-Item -ItemType Directory` (or mkdir); NEVER use write_file for directories.\n" +
               "- NEVER invent file paths. Before write_file/edit_file/delete_file/search_content, verify the path exists with list_dir or search_files.\n" +
                "- If a tool returns an error, do NOT retry the identical call; fix the problem (e.g. list the directory to find the exact name) or tell the user what failed.\n" +
                "- NEVER ask the user \"should I continue?\" mid-task. Keep working step by step until every todo item is done or you hit a genuine blocker (unrecoverable error, missing permission/file). Only then stop and report exactly what failed and why.";
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
        _lastSystemChars = system.Length; // 供下一轮压缩估算（预算=系统+历史，与 usage.prompt_tokens 口径一致）

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(system))
            messages.Add(new ChatMessage { Role = "system", Content = system });
        var history = Sanitize(History); // 加锁快照，避免与 UI 线程遍历竞争
        ShrinkOldProtocol(history);     // agent 工具往返老化收缩：只影响发给模型的上下文
        messages.AddRange(history);
        EnforceModelCap(messages);      // 硬护栏：估算超模型实际上限时丢最旧历史，保证不触发 API 报错
        return messages;
    }

    /// <summary>硬护栏（最后防线）：模型接口声明了上下文上限且估算总占用要超出时，从发送列表丢弃最旧历史。
    /// 正常情况由压缩闸门提前处理，这里兜住估算偏差/突发超长消息；被丢的条目仍在 _history 与归档里。</summary>
    private void EnforceModelCap(List<ChatMessage> messages)
    {
        var ep = _config.EffectiveLlm();
        var modelMax = LlamaClient.ModelMaxContext(ep.Url, ep.Model);
        if (modelMax == null || modelMax <= 0) return; // API 未提供上限：不干预
        var dropped = LlamaClient.TrimToContextCap(messages, modelMax.Value, TokPerChar, _config.EffectiveMaxTokens + 512);
        if (dropped > 0) Log.Info($"Context hard-cap: dropped {dropped} oldest history entries to fit model context ({modelMax} tok)");
    }

    /// <summary>agent 工具往返老化收缩：距末尾超过最近 10 条的协议消息截掉超长载荷，把上下文预算让给情感对话。
    /// 只修改发给模型的内存列表；_history、memory.json 与归档均不变（UI 仍显示全文）。</summary>
    private static void ShrinkOldProtocol(List<ChatMessage> msgs)
    {
        const int keepRecent = 10; // 最近 10 条保持完整：模型的工具用法示例
        var start = Math.Max(0, msgs.Count - keepRecent);
        for (var i = 0; i < start; i++)
        {
            var c = msgs[i].Content ?? "";
            if (c.Length <= 400) continue;
            if (c.StartsWith("[tool]"))
                msgs[i].Content = c[..300] + "…";
            else if (c.StartsWith("[result]") || c.StartsWith("[error]") || c.StartsWith("[note]"))
                msgs[i].Content = Truncate(c, 240);
        }
    }

    public Task<bool> RunAsync(string userText, ISpeakHost host) => RunAsync(userText, host, asEvent: false);

    /// <param name="asEvent">true=第三方事件（插件 SendEventAsync，如直播间弹幕）：历史记 Role="event"，
    /// 对模型呈现为 system（叙述者）而非 user——模型不会把观众发言当成用户本人说的话；聊天窗用独立紧凑样式。</param>
    /// <param name="allowAgent">false=本轮不启用 agent 工具链（即使全局开启）：第三方事件默认 false，防不可信内容注入电脑操作指令。</param>
    public async Task<bool> RunAsync(string userText, ISpeakHost host, bool asEvent, bool allowAgent = true, string? eventInstruction = null)
    {
        await _gate.WaitAsync();
        IsRunning = true;
        RunningChanged?.Invoke(true);
        var cts = new CancellationTokenSource();
        _runCts = cts;
        try
        {
            lock (_histLock)
            {
                _history.Add(new ChatMessage { Role = asEvent ? "event" : "user", Content = userText });
                if (asEvent)
                    // 事件后紧跟一条持久化 user 触发（同主动搭话的沉默回合同构）：wire 呈 event(system)→user→assistant 正常交替，
                    // DeepSeek 不再丢弃中间 system / 合并重复会话；这条也是留给模型的持久痕迹，且只此一次 LLM 调用
                    _history.Add(new ChatMessage { Role = "user", Content = EventReplyTrigger(eventInstruction) });
            }
            NotifyHistory();
            Status?.Invoke("思考中…");

            await MaybeCompressAsync(cts.Token); // 请求前压缩（对齐 opencode），不占 TTS 时间；滞回保证不会每轮都压
            NotifyHistory();
            var messages = await BuildMessagesAsync();
            string rawReply;
            if (_config.Chat.Agent.Enabled && allowAgent)
                rawReply = await _agent.RunAsync(messages, host, cts.Token); // 工具循环（中间往返经 OnMessage 进长期历史）
            else
            {
                rawReply = await CompleteAsync(messages, null, cts.Token, onDelta: _ => { }, streamToUi: !asEvent); // 事件回复不进 UI 流式（防 [SKIP] 闪烁）；正常聊天实时打字
                // agent 关闭时模型若模仿历史输出 [tool]：回灌"工具不可用"纠正并让它改用文字回答（最多 2 次），往返持久化防止复发
                const string noToolFeedback = "[error] Agent 功能未开启，无法调用工具。请直接用文字回答：可以说明需要做什么、或提醒用户在设置里开启 Agent 后再试。不要再输出 [tool] 块。";
                for (var guard = 0; guard < 2 && rawReply.IndexOf("[tool]", StringComparison.OrdinalIgnoreCase) >= 0; guard++)
                {
                    lock (_histLock)
                    {
                        _history.Add(new ChatMessage { Role = "assistant", Content = rawReply });
                        _history.Add(new ChatMessage { Role = "user", Content = noToolFeedback });
                    }
                    messages.Add(new ChatMessage { Role = "assistant", Content = rawReply });
                    messages.Add(new ChatMessage { Role = "user", Content = noToolFeedback });
                    rawReply = await CompleteAsync(messages, null, cts.Token, onDelta: _ => { }, streamToUi: !asEvent);
                }
                if (rawReply.IndexOf("[tool]", StringComparison.OrdinalIgnoreCase) >= 0)
                    rawReply = AgentRunner.StripToolBlocks(rawReply); // 仍不合规：剥离工具块只留文字部分
            }
            rawReply = PluginManager.RunReplyChain(rawReply, new ReplyContext { Source = "final", IsAgentStep = false }); // 插件消息链（最终回答）
            // 空回复 = 本轮不产生可见输出（插件可在消息链把"跳过"翻译成空，如直播间跳过不想回的弹幕）——
            // 不朗读不出气泡；历史写一条紧凑标记保持 user/assistant 交替，后续上下文知道"宠物选择了沉默"。宿主不认识具体协议词，只认空。
            // 另：模型可能把下面这条跳过标记当样例复读回来（而非用插件的跳过词）——识别并吞掉，同样不朗读不出气泡。
            // 标记用非对话协议标签 [no-reply]（明显不是台词）：防止被当聊天内容混入、或被模型复读成语音。
            const string skipMarker = "[no-reply]";
            if (string.IsNullOrWhiteSpace(rawReply) || rawReply.Contains("no-reply"))
            {
                lock (_histLock) _history.Add(new ChatMessage { Role = "assistant", Content = skipMarker });
                NotifyHistory();
                Status?.Invoke("");
                return true;
            }
            lock (_histLock) _history.Add(new ChatMessage { Role = "assistant", Content = rawReply });
            NotifyHistory();

            var (specs, fullText) = await PlanSegmentsAsync(rawReply);
            await SpeakPlannedAsync(host, fullText, specs);

            Status?.Invoke("");
            return true;
        }
        catch (OperationCanceledException) when (_runCts?.IsCancellationRequested == true)
        {
            // 手动停止：历史留中断标记（assistant 角色保持 user/assistant 交替），模型下次知道任务被终止、做到哪了
            lock (_histLock) _history.Add(new ChatMessage { Role = "assistant", Content = "[system] 用户手动停止了本次任务（未完成）。" });
            NotifyHistory();
            Status?.Invoke("已停止");
            return false;
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
            _runCts = null;
            RunningChanged?.Invoke(false);
            cts.Dispose();
            _gate.Release();
        }
    }

    public async Task<bool> RunProactiveAsync(ISpeakHost host)
    {
        await _gate.WaitAsync();
        IsRunning = true;
        RunningChanged?.Invoke(true);
        var cts = new CancellationTokenSource();
        _runCts = cts;
        try
        {
            var silence = RandomSilenceTurn();
            await MaybeCompressAsync(cts.Token); // 请求前压缩，不占 TTS 时间
            NotifyHistory();
            var messages = await BuildMessagesAsync(ProactiveInstruction());
            if (messages.Count == 0 || messages[^1].Role != "user")
                messages.Add(new ChatMessage { Role = "user", Content = silence });

            var rawReply = await CompleteAsync(messages, _config.EffectiveProactiveTemperature, cts.Token, onDelta: _ => { }); // 流式：聊天窗实时打字
            if (_lastProactive != null && IsNearRepeat(rawReply, _lastProactive))
            {
                messages[^1] = new ChatMessage { Role = "user", Content = messages[^1].Content + "\n" + ProactiveRepeatInstruction() };
                rawReply = await CompleteAsync(messages, _config.EffectiveProactiveTemperature, cts.Token, onDelta: _ => { });
            }
            rawReply = PluginManager.RunReplyChain(rawReply, new ReplyContext { Source = "proactive", IsAgentStep = false }); // 插件消息链（主动搭话）
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
            NotifyHistory();
            Status?.Invoke("");
            return true;
        }
        catch (OperationCanceledException) when (_runCts?.IsCancellationRequested == true)
        {
            // 手动停止主动搭话：无任务状态，不留标记
            Status?.Invoke("");
            return false;
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
            _runCts = null;
            RunningChanged?.Invoke(false);
            cts.Dispose();
            _gate.Release();
        }
    }

    /// <summary>系统生成消息的统一前缀标记：这类 user 消息并非用户本人所说（主动搭话沉默指令、事件触发），聊天窗据此隐藏其蓝色气泡。
    /// 固定用英文 [SYSTEM]——这些消息只进模型上下文、不在 UI 显示，英文对指令遵循最稳；检测端按 [SYSTEM] 前缀匹配。</summary>
    private static string SysPrefix() => "[SYSTEM] ";

    private string RandomSilenceTurn()
    {
        var arr = new[]
        {
            "(There has been a long silence. Please start a conversation.)",
            "(The user hasn't said anything yet. Bring up a new topic.)",
            "(It's been quiet for a while. Talk about today's events or something on your mind.)",
        };
        return SysPrefix() + arr[Random.Shared.Next(arr.Length)];
    }

    /// <summary>事件（弹幕/礼物等）入库后紧跟的 user 触发：让 wire 以 user 收尾、正常交替，模型据此决定是否回应。
    /// 必须带 [SYSTEM] 标记——LIVE ROOM MODE 规则是"Only UNMARKED messages come from your user"，无标记的 user 消息会被当成用户本人，
    /// 导致角色把观众弹幕误当用户的话来回应；标成系统转发后即被排除出"用户"，并明确指示回应的是那位观众而非用户。
    /// 跳过机制（输出什么、如何表示不回应）归插件的 system prompt 定义，这里只要求"回应这位观众或保持沉默"。固定英文（不在 UI 显示）。</summary>
    // 宿主只是 [SYSTEM] 结构包装：把插件给出的每事件指令原样放进 user 触发词（尾部，贴近决策点、比 system 头部遵循度更高）。
    // 不含任何处理策略；插件未给指令时给一条最中性兜底。彻底解耦——"怎么回/跳过/格式/别续旧话题"全由插件在指令里决定。
    private string EventReplyTrigger(string? pluginInstruction = null)
        => SysPrefix() + (string.IsNullOrWhiteSpace(pluginInstruction) ? "(Handle the event above per your rules.)" : pluginInstruction);

    // 固定英文 + [SYSTEM] 前缀（与事件触发/沉默指令同约定）：只进模型上下文、不在 UI 显示，英文对指令遵循最稳。
    // 回复语言由主 system prompt 的 LANGUAGE 段决定，这里用英文不影响角色用配置语言搭话。
    private string ProactiveInstruction()
        => SysPrefix() + "PROACTIVE TURN: It is your turn to start a conversation; the user has been silent for a while. Do NOT continue the past conversation (summary) — bring up ONE new topic (today's events, hobbies, how the user is doing, etc.). Keep it to at most one sentence and avoid repeating phrases or topics you have used before. Start your reply with one emotion tag.";

    private string ProactiveRepeatInstruction()
        => SysPrefix() + "You just said essentially the same thing again. Start a conversation with a DIFFERENT new topic instead.";

    /// <param name="onDelta">非 null 且 StreamEnabled 时走 SSE 流式（每片回调累计全文）；否则原非流式整包路径。</param>
    /// <param name="streamToUi">是否把流式增量转发到聊天窗打字气泡（ReplyDelta/ReplyStreamEnd）。事件回复传 false：弹幕短、常跳过且为第三方触发，实时打字会把 [SKIP] 之类协议词闪出来；正常聊天/主动搭话保持 true。</param>
    private async Task<string> CompleteAsync(List<ChatMessage> messages, double? temperatureOverride = null, CancellationToken ct = default, Action<string>? onDelta = null, bool streamToUi = true)
    {
        var ep = _config.EffectiveLlm();
        DebugLog?.Invoke(FormatRequest(ep.Url, ep.Model, messages));
        ChatResult result;
        if (onDelta != null && _config.Chat.StreamEnabled)
        {
            var streamOk = false;
            var firstDelta = true;
            try
            {
                result = await LlamaClient.CompleteStreamAsync(
                    ep.Url,
                    messages,
                    ep.Model,
                    temperatureOverride ?? _config.EffectiveTemperature,
                    _config.EffectiveMaxTokens,
                    soFar => { if (firstDelta) { firstDelta = false; _streamStepStartTs = DateTime.Now; } onDelta(soFar); if (streamToUi) ReplyDelta?.Invoke(soFar); },
                    ep.ApiKey,
                    ep.ExtraParams,
                    ct);
                streamOk = true;
            }
            finally
            {
                if (streamToUi) ReplyStreamEnd?.Invoke(streamOk); // 完成=移除气泡；出错/停止=冻结已显示文本
            }
        }
        else
        {
            result = await LlamaClient.CompleteAsync(
                ep.Url,
                messages,
                ep.Model,
                temperatureOverride ?? _config.EffectiveTemperature,
                _config.EffectiveMaxTokens,
                ep.ApiKey,
                ep.ExtraParams,
                ct);
        }
        if (result.Usage.PromptTokens > 0)
            OnUsageSample(result.Usage.PromptTokens, messages.Sum(m => m.Content?.Length ?? 0)); // 真实上下文 token：显示+比率校准（流式拿不到 usage 时跳过，沿用上次值）
        DebugLog?.Invoke(FormatReply(result.Text));
        return result.Text;
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

    private async Task MaybeCompressAsync(CancellationToken ct = default)
    {
        // 有效预算（用户设置 ∩ 模型实际上限）→ 字数预算（校准比率折算，未校准时默认 ~1.5 字/token）：压缩发生在请求前，只能用本地估算。
        // 只按 token 预算触发，不按消息条数——agent 一个任务就产生大量往返消息，条数阈值会频繁误触发
        var maxChars = EffectiveContextBudget() > 0 ? (int)(EffectiveContextBudget() / TokPerChar) : 0;
        if (maxChars <= 0) return;

        List<ChatMessage> snap;
        lock (_histLock) snap = _history.ToList();
        var count = snap.Count;
        var totalChars = snap.Sum(m => m.Content?.Length ?? 0);
        // 预算覆盖整次请求（系统提示词+历史），与聊天窗标题栏显示一致——usage.prompt_tokens 是整请求量，
        // 若只按历史比较，系统提示词（几千 token）会造成"显示已超预算却不触发压缩"的偏移
        var sysChars = _lastSystemChars;
        if (totalChars + sysChars - maxChars <= 0) return;

        // 最小必要压缩：只压到剩余（系统+历史）≤ 预算的 70%（滞回，避免每轮反复压），绝不碰最近几轮
        var targetChars = Math.Max(0, (long)(maxChars * 0.7) - sysChars);
        var prefix = new long[count + 1];
        for (var i = 0; i < count; i++) prefix[i + 1] = prefix[i] + (snap[i].Content?.Length ?? 0);

        int take = -1;
        for (var t = Math.Min(4, count - 2); t <= count - 2; t++)
        {
            if (totalChars - prefix[t] <= targetChars) { take = t; break; }
        }
        if (take < 0) take = count - 2; // 仍超预算：压到只剩最后两条
        if (take < 2 || count - take < 2) return;

        List<ChatMessage> chunk;
        lock (_histLock) chunk = _history.GetRange(0, Math.Min(take, Math.Max(0, _history.Count - 2)));
        if (chunk.Count < 2) return;
        SetCompressing(true); // 聊天窗提示"整理记忆中"+锁定输入
        try
        {
            MemoryArchive.Append(_config, chunk); // 先归档再压缩：即使摘要失败，原始记录也在 memory_archive.json 里
            _summary = await CompressAsync(chunk, _summary, ct);
            if (ct.IsCancellationRequested) return; // 停止发生在摘要期间：不删历史（避免无摘要丢原文）
            lock (_histLock) _history.RemoveRange(0, Math.Min(chunk.Count, Math.Max(0, _history.Count - 2)));
        }
        finally { SetCompressing(false); }
        int after;
        lock (_histLock) after = _history.Count;
        var unit = EffectiveContextBudget() > 0 ? EffectiveContextBudget() + " tok" : "n/a";
        Log.Info($"History compressed: {count} msgs / budget {unit} -> {after} msgs");
    }

    private async Task<string> CompressAsync(List<ChatMessage> chunk, string? prevSummary, CancellationToken ct = default)
    {
        try
        {
            var sb = new StringBuilder();
            var lang = _config.EffectiveTextLang;
            List<ChatMessage> messages;
            if (lang == "ja")
            {
                sb.AppendLine("以下の会話（以前の要約を含む）を日本語で構造化された記憶の要約に整理してください。必ず以下のセクションすべてを残して出力し、内容がなければ「（なし）」と書いてください：");
                sb.AppendLine();
                sb.AppendLine("## ユーザーとの関係");
                sb.AppendLine("- 呼び方・好み・嫌いなもの・関係のトーン（具体的な名前はそのまま保持）");
                sb.AppendLine("## 気分と約束");
                sb.AppendLine("- 最近の気分；約束・取り決め（果たしていないものは必ず保持）；触れにくい話題");
                sb.AppendLine("## 出来事");
                sb.AppendLine("- 感情的に意味のある重要な出来事（日時+内容）、重複は統合可だが事実は落とさない");
                sb.AppendLine("## agent操作");
                sb.AppendLine("- 実行した主な操作（コマンド/パスはそのまま保持）、ユーザーが拒否した操作、信頼ディレクトリの変更");
                sb.AppendLine("## 次の一手");
                sb.AppendLine("- 未完の会話の糸口や未処理事項");
                sb.AppendLine();
                sb.AppendLine("ルール：短い箇条書きにすること（段落にしない）；具体的なパス・コマンド・ファイル名・人名をそのまま保持；要約本体のみ出力し、「要約」ということ自体には触れないこと。");
                if (!string.IsNullOrWhiteSpace(prevSummary))
                {
                    sb.AppendLine("統合ルール：旧要約で果たしていない約束と長期的な好みは必ず引き継ぐこと；新旧が矛盾すれば新しい方を正とする。");
                    sb.AppendLine("以前の要約：").AppendLine(prevSummary);
                }
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
                sb.AppendLine("Summarize the conversation below (including any previous summary) into a structured memory summary in English. Output exactly these sections, keeping every one (write \"(none)\" when empty):");
                sb.AppendLine();
                sb.AppendLine("## User & Relationship");
                sb.AppendLine("- address, preferences, pet peeves, tone of the relationship (keep exact names verbatim)");
                sb.AppendLine("## Mood & Commitments");
                sb.AppendLine("- recent mood; promises and commitments (unfulfilled ones MUST be kept); sensitive topics");
                sb.AppendLine("## What Happened");
                sb.AppendLine("- important events with emotional significance (time + content); merge duplicates but never drop facts");
                sb.AppendLine("## Agent Actions");
                sb.AppendLine("- key operations performed (keep commands/paths verbatim), operations the user declined, trusted-dir changes");
                sb.AppendLine("## Next Steps");
                sb.AppendLine("- open threads or pending items");
                sb.AppendLine();
                sb.AppendLine("Rules: terse bullets, not prose; preserve exact paths, commands, file names and personal names; output only the summary itself, do not mention that a summary was made.");
                if (!string.IsNullOrWhiteSpace(prevSummary))
                {
                    sb.AppendLine("Merge rules: carry forward unfulfilled commitments and long-term preferences from the prior summary; where old and new conflict, the newer wins.");
                    sb.AppendLine("Previous summary:").AppendLine(prevSummary);
                }
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
                sb.AppendLine("请将下面的对话（包含之前的摘要）用简体中文整理成一份结构化的记忆摘要。严格按以下模板输出，保留所有小节（无内容写\"（无）\"）：");
                sb.AppendLine();
                sb.AppendLine("## 用户与关系");
                sb.AppendLine("- 称呼、喜好、雷点、关系基调（具体名称原样保留）");
                sb.AppendLine("## 情绪与约定");
                sb.AppendLine("- 近期情绪基调；承诺与约定（未兑现的必须保留）；敏感话题");
                sb.AppendLine("## 发生过的事");
                sb.AppendLine("- 有情感意义的重要事件（时间+内容），可合并去重，但不要丢失事实");
                sb.AppendLine("## agent操作");
                sb.AppendLine("- 执行过的关键操作（命令/路径原样保留）、被用户拒绝的操作、信任目录变更");
                sb.AppendLine("## 下一步");
                sb.AppendLine("- 未完成的对话线索或待办");
                sb.AppendLine();
                sb.AppendLine("规则：使用简短要点，不要段落；原样保留具体路径、命令、文件名与人名；只输出摘要本身，不要提及\"摘要\"这件事。");
                if (!string.IsNullOrWhiteSpace(prevSummary))
                {
                    sb.AppendLine("合并规则：旧摘要中未兑现的承诺与长期偏好必须带过；新旧冲突以新为准。");
                    sb.AppendLine("之前的摘要：").AppendLine(prevSummary);
                }
                sb.AppendLine("需要摘要的对话：");
                foreach (var m in chunk) sb.Append(m.Role).Append(": ").Append(m.Content).AppendLine();

                messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = "你是负责整理对话记忆的助手。" },
                    new() { Role = "user", Content = sb.ToString() },
                };
            }
            var ep = _config.EffectiveLlm();
            // 摘要请求不携带完整历史，其 usage 不代表上下文占用，故不采样
            var result = await LlamaClient.CompleteAsync(
                ep.Url, messages, ep.Model, 0.3, SummaryMaxTokens(), ep.ApiKey, ep.ExtraParams, ct);
            var summary = result.Text;
            return string.IsNullOrWhiteSpace(summary) ? (prevSummary ?? "") : summary.Trim();
        }
        catch (OperationCanceledException)
        {
            throw; // 手动停止：让上层感知（不删历史、不留"压缩失败"日志）
        }
        catch (Exception ex)
        {
            Log.Error("CompressAsync failed", ex);
            return prevSummary ?? "";
        }
    }

    /// <summary>摘要输出的 token 上限：按「上下文预算」的 1/8 计算，下限 512（结构化模板需要空间）、上限 2048。</summary>
    private int SummaryMaxTokens()
    {
        var budget = _config.Chat.ContextMaxTokens;
        if (budget <= 0) budget = 16000;
        return Math.Clamp(budget / 8, 512, 2048);
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
            SetCompressing(true); // 手动压缩同样提示+锁定输入
            try
            {
                MemoryArchive.Append(_config, chunk); // 归档后再清空
                var result = await CompressAsync(chunk, _summary);
                lock (_histLock) _history.Clear();
                _lastProactive = null;
                _summary = string.IsNullOrWhiteSpace(result) ? null : result.Trim();
                NotifyHistory();
            }
            finally { SetCompressing(false); }
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