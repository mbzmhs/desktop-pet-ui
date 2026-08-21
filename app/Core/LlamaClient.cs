using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPetUi.Core;

public sealed class ChatMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;
    /// <summary>可选：仅 event 消息携带的"每事件指令"（插件 BuildEventInstruction 给出，如礼物必谢点名）。
    /// ToPayload 时拼进发给模型的 user 消息尾部；不进 UI、不写 memory.json（临时决策辅助，非持久对话）。</summary>
    [JsonIgnore]
    public string? Instruction { get; set; }
    /// <summary>可选：随本条消息发送的 PNG base64 图片（视觉模型）。</summary>
    [JsonIgnore]
    public List<string>? ImageBase64s { get; set; }
}

/// <summary>API 响应中的 token 用量（非流式响应的 usage 字段，最准确的上下文占用来源）。</summary>
public sealed record ChatUsage(int PromptTokens, int CompletionTokens, int TotalTokens)
{
    public static readonly ChatUsage Empty = new(0, 0, 0);
}

/// <summary>补全结果：回复文本 + token 用量。</summary>
public sealed record ChatResult(string Text, ChatUsage Usage);

/// <summary>/v1/models 返回的模型信息；MaxContextTokens=null 表示该 API 未提供上下文上限。</summary>
public sealed record ModelInfo(string Id, int? MaxContextTokens)
{
    public override string ToString() => Id;
}

public static class LlamaClient
{
    private static HttpClient Http = CreateClient();
    // 流式专用：整体超时设为无限（生成可能远超 180s），生命周期由调用方 CancellationToken 控制
    private static HttpClient StreamHttp = new(BuildProxyHandler(null)) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private static string? _discoveredModel;

    // —— 模型上下文上限（由 /v1/models 自行提供）：保证请求不超过它而产生 API 报错 ——
    private static readonly object _modelCtxLock = new();
    private static string _modelCtxKey = "";
    private static int? _modelMaxContext;
    private static DateTime _modelCtxAt;
    private static bool _modelCtxRefreshing;

    /// <summary>读取缓存的模型最大上下文 token（null=尚未查到或该 API 不提供）。仅当 url|model 与缓存匹配时返回。</summary>
    public static int? ModelMaxContext(string baseUrl, string model)
    {
        lock (_modelCtxLock)
            return (baseUrl ?? "").TrimEnd('/') + "|" + (model ?? "") == _modelCtxKey ? _modelMaxContext : null;
    }

    /// <summary>后台刷新指定端点的模型上下文上限（/v1/models，30 分钟缓存；查询失败保持旧值，不阻塞主流程）。</summary>
    public static void RefreshModelContextAsync(string baseUrl, string model, string? apiKey = null)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return;
        var key = baseUrl.TrimEnd('/') + "|" + (model ?? "");
        lock (_modelCtxLock)
        {
            if (_modelCtxRefreshing) return;
            if (key == _modelCtxKey && (DateTime.Now - _modelCtxAt).TotalMinutes < 30) return;
            _modelCtxRefreshing = true;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var infos = await FetchModelsAsync(baseUrl, apiKey);
                StoreModelContext(baseUrl, model ?? "", infos);
            }
            catch
            {
                // 查询失败不影响主流程：继续按用户设置的预算运行
            }
            finally
            {
                lock (_modelCtxLock) _modelCtxRefreshing = false;
            }
        });
    }

    /// <summary>把一次 /v1/models 的解析结果记入缓存（设置页"获取模型"与后台刷新共用）。</summary>
    public static void StoreModelContext(string baseUrl, string model, List<ModelInfo> infos)
    {
        int? mc = null;
        if (!string.IsNullOrWhiteSpace(model))
            mc = infos.FirstOrDefault(i => string.Equals(i.Id, model, StringComparison.OrdinalIgnoreCase))?.MaxContextTokens;
        if (mc == null && infos.Count == 1) mc = infos[0].MaxContextTokens; // 未指定模型名/未匹配：本地服务器通常只有一个模型
        lock (_modelCtxLock)
        {
            _modelCtxKey = baseUrl.TrimEnd('/') + "|" + (model ?? "");
            _modelMaxContext = mc;
            _modelCtxAt = DateTime.Now;
        }
        Log.Info("Model max context: " + model + " = " + (mc?.ToString() ?? "(API 未提供，按设置预算运行)"));
    }

    /// <summary>硬护栏：估算请求 token（总字数×比率）超过模型实际上下文上限时，丢弃最旧的历史条目。
    /// messages[0]（system）不动、至少保留最后一条；只改传入的发送列表，不碰长期历史。返回丢弃条数。</summary>
    public static int TrimToContextCap(List<ChatMessage> messages, int maxContextTokens, double tokPerChar, int outputReserve)
    {
        if (maxContextTokens <= 0 || tokPerChar <= 0 || messages.Count < 3) return 0;
        var capChars = Math.Max(1000, (int)((maxContextTokens - outputReserve) / tokPerChar));
        var keepFrom = 1; // 第一条保留位置（0=system）
        while (keepFrom < messages.Count - 1)
        {
            int total = 0;
            for (var i = keepFrom; i < messages.Count; i++) total += messages[i].Content?.Length ?? 0;
            if (total <= capChars) break;
            keepFrom++;
        }
        if (keepFrom > 1) { messages.RemoveRange(1, keepFrom - 1); return keepFrom - 1; }
        return 0;
    }

    /// <summary>调试钩子：每次发送请求前回调 (url, 脱敏后的完整请求 JSON)。base64 图片会被替换为长度占位。</summary>
    public static Action<string, string>? OnRequest;

    private static readonly Regex Base64ImageRegex = new(
        "data:image/[a-z]+;base64,[A-Za-z0-9+/=]{64,}", RegexOptions.Compiled);

    private static string RedactImages(string json)
        => Base64ImageRegex.Replace(json, m => "data:<image base64, " + m.Value.Length + " chars>");

    /// <summary>按代理设置构建 handler（普通/流式两个 client 各建一个实例，handler 不能跨 client 共享否则 Dispose 会连坐）。</summary>
    private static HttpClientHandler BuildProxyHandler(ProxyConfig? cfg)
    {
        var mode = cfg?.Mode ?? "system";
        var handler = new HttpClientHandler();
        if (string.Equals(mode, "none", StringComparison.OrdinalIgnoreCase))
        {
            handler.UseProxy = false;
        }
        else if (string.Equals(mode, "custom", StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(cfg?.Address))
        {
            handler.UseProxy = true;
            handler.Proxy = new System.Net.WebProxy(cfg.Address.Trim());
        }
        else
        {
            handler.UseProxy = true; // 系统代理（默认）
        }
        return handler;
    }

    private static HttpClient CreateClient() => new(BuildProxyHandler(null)) { Timeout = TimeSpan.FromSeconds(180) };

    public static void ConfigureProxy(ProxyConfig? cfg)
    {
        var old = Http;
        var oldStream = StreamHttp;
        Http = new HttpClient(BuildProxyHandler(cfg)) { Timeout = TimeSpan.FromSeconds(180) };
        StreamHttp = new HttpClient(BuildProxyHandler(cfg)) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _discoveredModel = null;
        try { old.Dispose(); } catch { }
        try { oldStream.Dispose(); } catch { }
    }

    public static async Task<ChatResult> CompleteAsync(
        string baseUrl,
        IReadOnlyList<ChatMessage> messages,
        string model,
        double temperature,
        int maxTokens,
        string? apiKey = null,
        string? extraParams = null,
        CancellationToken ct = default)
    => await CompleteInternalAsync(baseUrl, messages, model, temperature, maxTokens, apiKey, extraParams, ct, retried: false);

    /// <summary>流式补全（SSE）：每收到一片增量就回调 onDelta(到目前为止的累计全文)，结束时返回完整 ChatResult。
    /// usage 由 stream_options.include_usage 让服务端在尾部块携带；拿不到时 Usage 为 Empty（调用方跳过采样即可）。
    /// 用独立的不限超时 client（StreamHttp），生命周期由 ct 控制。</summary>
    public static async Task<ChatResult> CompleteStreamAsync(
        string baseUrl,
        IReadOnlyList<ChatMessage> messages,
        string model,
        double temperature,
        int maxTokens,
        Action<string> onDelta,
        string? apiKey = null,
        string? extraParams = null,
        CancellationToken ct = default)
    => await CompleteStreamInternalAsync(baseUrl, messages, model, temperature, maxTokens, apiKey, extraParams, onDelta, ct, retried: false);

    /// <summary>组装 /v1/chat/completions 请求体（非流式/流式共用；stream=true 时附 stream_options.include_usage）。</summary>
    private static string BuildChatPayload(IReadOnlyList<ChatMessage> messages, string useModel, double temperature, int maxTokens, string? extraParams, bool stream)
    {
        var payload = new JsonObject
        {
            ["model"] = useModel,
            ["messages"] = JsonSerializer.SerializeToNode(messages.Select(ToPayload).ToArray()),
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
            ["stream"] = stream,
        };
        // 注：事件的 user 触发已由 ChatPipeline 持久化进 history（event→user 交替），这里不再临时补；
        // 正常路径下请求恒以 user 收尾，DeepSeek 不会丢弃中间 system / 合并重复会话。
        if (stream) payload["stream_options"] = new JsonObject { ["include_usage"] = true };
        if (!string.IsNullOrWhiteSpace(extraParams))
        {
            try
            {
                if (JsonNode.Parse(extraParams) is JsonObject extra)
                {
                    foreach (var kv in extra)
                    {
                        if (kv.Value != null) payload[kv.Key] = kv.Value.DeepClone();
                    }
                }
            }
            catch
            {
                // 非法 JSON 的高级参数直接忽略
            }
        }
        return payload.ToJsonString();
    }

    /// <summary>诊断日志：请求负载指纹（SHA256 前 12 位）+ 末尾消息的角色/长度/内容片段。
    /// 用途：若连续两轮回复逐字节相同，对比这两行的 payload——相同=请求被冻结（上游 bug），不同=服务端对异质请求返回了相同补全。</summary>
    private static void LogPayloadFingerprint(IReadOnlyList<ChatMessage> messages, string json)
    {
        try
        {
            var h = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(json));
            var parts = new List<string>();
            foreach (var m in messages.Skip(Math.Max(0, messages.Count - 3)))
            {
                var c = (m.Content ?? "").Trim().Replace("\n", " ");
                parts.Add($"{m.Role}:{c.Length}字「{(c.Length > 24 ? c[..24] : c)}」");
            }
            var endRole = messages.Count > 0 ? messages[^1].Role : "-";
            Log.Info($"[llm] payload=0x{Convert.ToHexString(h)[..12]} msgs={messages.Count} end={endRole} tail=[{string.Join(" | ", parts)}]");
        }
        catch { /* 诊断日志失败不影响主流程 */ }
    }

    private static object ToPayload(ChatMessage m)
    {
        // event（第三方事件，如直播间弹幕）：观众内容 + 插件每事件指令合并成**一条 user 消息**——
        // 不用 system（llama.cpp/Qwen jinja 模板不允许中途/多条 system，会报错）；整体带 [SYSTEM] 标记，
        // 依 LIVE ROOM MODE"只有无标记 user 才是用户本人"规则排除出"用户"，防模型把观众当主人。指令在尾部=决策点。
        if (m.Role == "event")
        {
            var c = "[SYSTEM] " + m.Content
                  + (string.IsNullOrWhiteSpace(m.Instruction) ? "" : "\n" + m.Instruction);
            return new { role = "user", content = c };
        }
        var role = m.Role;
        if (m.ImageBase64s == null || m.ImageBase64s.Count == 0)
            return new { role, content = m.Content };
        var parts = new List<object> { new { type = "text", text = m.Content } };
        foreach (var b in m.ImageBase64s)
            parts.Add(new { type = "image_url", image_url = new { url = "data:image/png;base64," + b } });
        return new { role, content = parts.ToArray() };
    }

    public static async Task<List<ModelInfo>> FetchModelsAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        var url = NormalizeBaseUrl(baseUrl) + "/v1/models";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"获取模型失败 ({resp.StatusCode}): {Truncate(body, 200)}");
        var list = new List<ModelInfo>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in data.EnumerateArray())
            {
                if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(id.GetString()))
                    list.Add(new ModelInfo(id.GetString()!, ParseMaxContext(m)));
            }
        }
        return list;
    }

    /// <summary>从 /v1/models 条目解析最大上下文 token，兼容各家字段约定；都没有则返回 null（调用方回退用户预算）。</summary>
    private static int? ParseMaxContext(JsonElement m)
    {
        foreach (var name in new[] { "max_context_tokens", "max_model_len", "context_length", "max_input_tokens" })
            if (m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() > 0) return v.GetInt32();
        // llama.cpp：meta.n_ctx（运行时上下文窗口，部分版本在 model_info 下），回退 n_ctx_train（训练上限）
        foreach (var section in new[] { "meta", "model_info" })
            if (m.TryGetProperty(section, out var si) && si.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "n_ctx", "n_ctx_train" })
                    if (si.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() > 0) return v.GetInt32();
            }
        return null;
    }

    private static string NormalizeBaseUrl(string url)
    {
        var u = (url ?? "").Trim();
        if (string.IsNullOrWhiteSpace(u)) return u;
        u = u.TrimEnd('/');
        if (u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) u = u[..^3];
        return u;
    }

    private static async Task<ChatResult> CompleteInternalAsync(
        string baseUrl,
        IReadOnlyList<ChatMessage> messages,
        string model,
        double temperature,
        int maxTokens,
        string? apiKey,
        string? extraParams,
        CancellationToken ct,
        bool retried)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        var useModel = string.IsNullOrWhiteSpace(model) ? "local" : model;
        var url = baseUrl + "/v1/chat/completions";

        var json = BuildChatPayload(messages, useModel, temperature, maxTokens, extraParams, stream: false);
        LogPayloadFingerprint(messages, json); // 诊断：连续轮次回复逐字节相同时，对比请求是否真的不同
        if (OnRequest != null) OnRequest(url, RedactImages(json));
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            if (!retried && IsModelError(body))
            {
                var discovered = await DiscoverModelAsync(baseUrl, apiKey, ct);
                if (!string.IsNullOrEmpty(discovered))
                {
                    return await CompleteInternalAsync(baseUrl, messages, discovered, temperature, maxTokens, apiKey, extraParams, ct, retried: true);
                }
            }
            throw new Exception($"llama.cpp 请求失败 ({resp.StatusCode}): {Truncate(body, 300)}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err))
        {
            var msg = err.ValueKind == JsonValueKind.String ? (err.GetString() ?? err.ToString()) : err.ToString();
            throw new Exception("llama.cpp 返回错误: " + Truncate(msg, 300));
        }

        // usage：非流式响应直接携带（llama.cpp / OpenAI 兼容 API 均如此），prompt_tokens=本次请求实际占用的上下文 token
        int promptTokens = 0, completionTokens = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number) promptTokens = pt.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var cpt) && cpt.ValueKind == JsonValueKind.Number) completionTokens = cpt.GetInt32();
        }
        int totalTokens = promptTokens + completionTokens;
        if (root.TryGetProperty("usage", out var usage2) && usage2.ValueKind == JsonValueKind.Object &&
            usage2.TryGetProperty("total_tokens", out var tt) && tt.ValueKind == JsonValueKind.Number)
            totalTokens = tt.GetInt32();

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    return new ChatResult(c.GetString() ?? "", new ChatUsage(promptTokens, completionTokens, totalTokens));
                if (choice.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    return new ChatResult(t.GetString() ?? "", new ChatUsage(promptTokens, completionTokens, totalTokens));
            }
        }
        throw new Exception("llama.cpp 响应缺少 choices[0].message.content");
    }

    /// <summary>流式请求内部实现：SSE 逐行读 "data: {...}"，delta.content 累积后逐片回调 onDelta(累计全文)。
    /// 尾部块携带 usage（stream_options.include_usage）；服务端在流内报 error 也按异常抛出。</summary>
    private static async Task<ChatResult> CompleteStreamInternalAsync(
        string baseUrl,
        IReadOnlyList<ChatMessage> messages,
        string model,
        double temperature,
        int maxTokens,
        string? apiKey,
        string? extraParams,
        Action<string> onDelta,
        CancellationToken ct,
        bool retried)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        var useModel = string.IsNullOrWhiteSpace(model) ? "local" : model;
        var url = baseUrl + "/v1/chat/completions";
        var json = BuildChatPayload(messages, useModel, temperature, maxTokens, extraParams, stream: true);
        LogPayloadFingerprint(messages, json); // 诊断：连续轮次回复逐字节相同时，对比请求是否真的不同
        if (OnRequest != null) OnRequest(url, RedactImages(json));
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        using var resp = await StreamHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!retried && IsModelError(body))
            {
                var discovered = await DiscoverModelAsync(baseUrl, apiKey, ct);
                if (!string.IsNullOrEmpty(discovered))
                    return await CompleteStreamInternalAsync(baseUrl, messages, discovered, temperature, maxTokens, apiKey, extraParams, onDelta, ct, retried: true);
            }
            throw new Exception($"llama.cpp 流式请求失败 ({resp.StatusCode}): {Truncate(body, 300)}");
        }

        var sb = new StringBuilder();
        int promptTokens = 0, completionTokens = 0, totalTokens = 0;
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue; // 跳过 event:/注释/空行
            var data = line.Substring(5).Trim();
            if (data == "[DONE]") break;

            JsonElement root;
            using (var doc = JsonDocument.Parse(data))
                root = doc.RootElement.Clone();

            if (root.TryGetProperty("error", out var err))
            {
                var msg = err.ValueKind == JsonValueKind.String ? (err.GetString() ?? err.ToString()) : err.ToString();
                throw new Exception("llama.cpp 流式返回错误: " + Truncate(msg, 300));
            }

            // usage 可能只在尾部块出现（include_usage）
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number) promptTokens = pt.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var cpt) && cpt.ValueKind == JsonValueKind.Number) completionTokens = cpt.GetInt32();
            }

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (!choice.TryGetProperty("delta", out var delta)) continue;
                    if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        var piece = c.GetString() ?? "";
                        if (piece.Length > 0)
                        {
                            sb.Append(piece);
                            onDelta(sb.ToString()); // 交付累计全文：消费侧无需自己拼增量
                        }
                    }
                }
            }
        }

        totalTokens = promptTokens + completionTokens;
        return new ChatResult(sb.ToString(), new ChatUsage(promptTokens, completionTokens, totalTokens));
    }

    private static bool IsModelError(string body)
        => body.IndexOf("model", StringComparison.OrdinalIgnoreCase) >= 0 &&
           (body.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
            body.IndexOf("missing", StringComparison.OrdinalIgnoreCase) >= 0);

    private static async Task<string?> DiscoverModelAsync(string baseUrl, string? apiKey, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_discoveredModel)) return _discoveredModel;
        try
        {
            var url = NormalizeBaseUrl(baseUrl) + "/v1/models";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in data.EnumerateArray())
                {
                    if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(id.GetString()))
                    {
                        _discoveredModel = id.GetString();
                        return _discoveredModel;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        return s[..max] + "…";
    }
}