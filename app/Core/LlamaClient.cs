using System;
using System.Collections.Generic;
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
    /// <summary>可选：随本条消息发送的 PNG base64 图片（视觉模型）。</summary>
    [JsonIgnore]
    public List<string>? ImageBase64s { get; set; }
}

public static class LlamaClient
{
    private static HttpClient Http = CreateClient();
    private static string? _discoveredModel;

    /// <summary>调试钩子：每次发送请求前回调 (url, 脱敏后的完整请求 JSON)。base64 图片会被替换为长度占位。</summary>
    public static Action<string, string>? OnRequest;

    private static readonly Regex Base64ImageRegex = new(
        "data:image/[a-z]+;base64,[A-Za-z0-9+/=]{64,}", RegexOptions.Compiled);

    private static string RedactImages(string json)
        => Base64ImageRegex.Replace(json, m => "data:<image base64, " + m.Value.Length + " chars>");

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { UseProxy = true };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(180) };
    }

    public static void ConfigureProxy(ProxyConfig? cfg)
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
        var old = Http;
        Http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(180) };
        _discoveredModel = null;
        try { old.Dispose(); } catch { }
    }

    public static async Task<string> CompleteAsync(
        string baseUrl,
        IReadOnlyList<ChatMessage> messages,
        string model,
        double temperature,
        int maxTokens,
        string? apiKey = null,
        string? extraParams = null,
        CancellationToken ct = default)
        => await CompleteInternalAsync(baseUrl, messages, model, temperature, maxTokens, apiKey, extraParams, ct, retried: false);

    private static object ToPayload(ChatMessage m)
    {
        if (m.ImageBase64s == null || m.ImageBase64s.Count == 0)
            return new { role = m.Role, content = m.Content };
        var parts = new List<object> { new { type = "text", text = m.Content } };
        foreach (var b in m.ImageBase64s)
            parts.Add(new { type = "image_url", image_url = new { url = "data:image/png;base64," + b } });
        return new { role = m.Role, content = parts.ToArray() };
    }

    public static async Task<List<string>> FetchModelsAsync(string baseUrl, string? apiKey = null, CancellationToken ct = default)
    {
        var url = NormalizeBaseUrl(baseUrl) + "/v1/models";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"获取模型失败 ({resp.StatusCode}): {Truncate(body, 200)}");
        var list = new List<string>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in data.EnumerateArray())
            {
                if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(id.GetString()))
                    list.Add(id.GetString()!);
            }
        }
        return list;
    }

    private static string NormalizeBaseUrl(string url)
    {
        var u = (url ?? "").Trim();
        if (string.IsNullOrWhiteSpace(u)) return u;
        u = u.TrimEnd('/');
        if (u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) u = u[..^3];
        return u;
    }

    private static async Task<string> CompleteInternalAsync(
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

        object[] payloadMessages = messages.Select(ToPayload).ToArray();

        var payload = new JsonObject
        {
            ["model"] = useModel,
            ["messages"] = JsonSerializer.SerializeToNode(payloadMessages),
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens,
            ["stream"] = false,
        };
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
        var json = payload.ToJsonString();
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
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    return c.GetString() ?? "";
                if (choice.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    return t.GetString() ?? "";
            }
        }
        throw new Exception("llama.cpp 响应缺少 choices[0].message.content");
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