using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DesktopPetUi.Core;

public static class TtsClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(300) };
    private static string? _modelsBase;
    private static List<(string Id, string Display)>? _cachedModels;
    private static string? _emoKey;
    private static HashSet<string>? _cachedEmotions;

    /// <summary>指定角色（model）可用的情感集合，GET /v1/models/{model}/voices。按 (baseUrl,model) 缓存；失败/无 model 时返回仅含 neutral。</summary>
    public static async Task<HashSet<string>> GetAvailableEmotionsAsync(string baseUrl, string? model = null, CancellationToken ct = default)
    {
        var key = baseUrl + "|" + (model ?? "");
        if (_emoKey == key && _cachedEmotions != null) return _cachedEmotions;
        try
        {
            HashSet<string>? set = null;
            if (!string.IsNullOrWhiteSpace(model))
            {
                // 指定角色：/v1/models/{model}/voices -> data[].id
                var url = baseUrl.TrimEnd('/') + "/v1/models/" + Uri.EscapeDataString(model) + "/voices";
                using var resp = await Http.GetAsync(url, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var v in data.EnumerateArray())
                            if (v.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(idEl.GetString()))
                                set.Add(idEl.GetString()!);
                    }
                }
            }
            else
            {
                // 未指定角色（合成时服务端回退 active_voice）：取 /v1/models 全部角色的情感并集，避免误限制
                var url = baseUrl.TrimEnd('/') + "/v1/models";
                using var resp = await Http.GetAsync(url, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var m in data.EnumerateArray())
                            if (m.TryGetProperty("emotions", out var emo) && emo.ValueKind == JsonValueKind.Array)
                                foreach (var e in emo.EnumerateArray())
                                    if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                                        set.Add(e.GetString()!);
                    }
                }
            }
            if (set != null && set.Count > 0) { _emoKey = key; _cachedEmotions = set; return set; }
        }
        catch { }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "neutral" };
    }

    /// <summary>从 OpenAI 兼容 /v1/models 读取所有可选角色（首次查询后缓存）。返回 (原始ID, 显示名)。</summary>
    public static async Task<List<(string Id, string Display)>> GetAvailableVoicesAsync(string baseUrl, CancellationToken ct = default)
    {
        var list = new List<(string Id, string Display)>();
        if (_modelsBase == baseUrl && _cachedModels != null) return _cachedModels;
        try
        {
            var url = baseUrl.TrimEnd('/') + "/v1/models";
            using var resp = await Http.GetAsync(url, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in data.EnumerateArray())
                    {
                        if (!m.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                        var id = idEl.GetString() ?? "";
                        if (string.IsNullOrWhiteSpace(id)) continue;
                        var display = id;
                        var name = FirstString(m, "name", "desc", "description");
                        if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name.Trim(), id, StringComparison.Ordinal))
                            display = id + "（" + name.Trim() + "）";
                        list.Add((id, display));
                    }
                }
            }
        }
        catch { }
        if (list.Count > 0)
        {
            _modelsBase = baseUrl;
            _cachedModels = list;
        }
        return list;
    }

    public static async Task<(byte[] Data, double DurationSec)> SynthesizeAsync(
        string baseUrl,
        string text,
        ChatTtsConfig cfg,
        string? emotion = null,
        CancellationToken ct = default)
    {
        if (string.Equals(cfg.Provider, "windows", StringComparison.OrdinalIgnoreCase))
            return await SynthesizeWindowsAsync(text, cfg, ct);

        var url = baseUrl.TrimEnd('/') + "/v1/audio/speech";
        var payload = new
        {
            model = string.IsNullOrEmpty(cfg.VoiceId) ? null : cfg.VoiceId,
            input = text,
            voice = string.IsNullOrEmpty(emotion) ? cfg.Emotion : emotion,
            response_format = "wav",
            speed = cfg.SpeedFactor,
            language = cfg.TextLang,
        };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(url, content, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new Exception($"TTS 请求失败 ({resp.StatusCode}): {Truncate(body, 300)}");
        }
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0) throw new Exception("TTS 返回空音频");
        return (bytes, EstimateWavDurationSec(bytes));
    }

    /// <summary>
    /// 流式合成（tts-server `streaming: true`）。逐句完成后以 chunked HTTP 下发，
    /// 每段都是带独立 RIFF 头的完整 WAV。返回的异步序列按句依次产出，可边收边播。
    /// </summary>
    public static async IAsyncEnumerable<byte[]> SynthesizeStreamAsync(
        string baseUrl,
        string text,
        ChatTtsConfig cfg,
        string? emotion = null,
        bool stopPrev = false,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = baseUrl.TrimEnd('/') + "/v1/audio/speech?stream=true";
        var payload = new
        {
            model = string.IsNullOrEmpty(cfg.VoiceId) ? null : cfg.VoiceId,
            input = text,
            voice = string.IsNullOrEmpty(emotion) ? cfg.Emotion : emotion,
            response_format = "wav",
            speed = cfg.SpeedFactor,
            language = cfg.TextLang,
            stream = true,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new Exception($"TTS 流式请求失败 ({resp.StatusCode}): {Truncate(body, 300)}");
        }
        // 服务端流式按句下发"带独立 RIFF 头的完整 WAV 段"，逐段取出即可边收边播。（stopPrev 新协议无对应，忽略。）
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var pending = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var n = await stream.ReadAsync(buffer, ct);
            if (n == 0) break;
            pending.Write(buffer, 0, n);
            while (TryTakeWavSegment(pending, out var seg))
            {
                if (seg.Length > 0) yield return seg;
            }
        }
        while (TryTakeWavSegment(pending, out var seg))
        {
            if (seg.Length > 0) yield return seg;
        }
    }

    /// <summary>从累积缓冲中取出最前面一个完整的 WAV 段（每段自带 RIFF 头），未收全则返回 false。</summary>
    private static bool TryTakeWavSegment(MemoryStream src, out byte[] seg)
    {
        seg = Array.Empty<byte>();
        var buf = src.GetBuffer();
        if (src.Length < 12) return false;
        if (buf[0] != 'R' || buf[1] != 'I' || buf[2] != 'F' || buf[3] != 'F')
            throw new Exception("流式 TTS 返回的数据不是 WAV 段（缺少 RIFF 头）");
        var chunkSize = BitConverter.ToInt32(buf, 4);
        if (chunkSize < 4) throw new Exception("流式 TTS 返回非法 WAV 段长度");
        var total = chunkSize + 8;
        if (src.Length < total) return false;
        seg = new byte[total];
        Buffer.BlockCopy(buf, 0, seg, 0, total);
        var rest = (int)(src.Length - total);
        Buffer.BlockCopy(buf, total, buf, 0, rest);
        src.SetLength(rest);
        src.Position = rest;
        return true;
    }

    /// <summary>按顺序取第一个非空字符串属性值，均无则返回 null。</summary>
    private static string? FirstString(JsonElement el, params string[] names)
    {
        foreach (var n in names)
            if (el.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                return v.GetString();
        return null;
    }

    /// <summary>把多个 WAV 段按序拼接成一个合法 WAV（去掉各段 RIFF 头、合并数据区，更新文件大小）。</summary>
    public static byte[] ConcatWavSegments(IEnumerable<byte[]> segments)
    {
        var list = segments
            .Where(s => s != null && s.Length > 44 &&
                        s[0] == 'R' && s[1] == 'I' && s[2] == 'F' && s[3] == 'F')
            .ToList();
        if (list.Count == 0) return Array.Empty<byte>();
        long dataLen = 0;
        var header = list[0];
        foreach (var s in list)
        {
            var chunkSize = BitConverter.ToInt32(s, 4);
            dataLen += Math.Max(0, chunkSize - 36);
        }
        var outBytes = new byte[44 + dataLen];
        Buffer.BlockCopy(header, 0, outBytes, 0, 44);
        var riffSize = (int)(36 + dataLen);
        outBytes[4] = (byte)(riffSize & 0xFF); outBytes[5] = (byte)((riffSize >> 8) & 0xFF);
        outBytes[6] = (byte)((riffSize >> 16) & 0xFF); outBytes[7] = (byte)((riffSize >> 24) & 0xFF);
        var dataSize = (int)dataLen;
        outBytes[40] = (byte)(dataSize & 0xFF); outBytes[41] = (byte)((dataSize >> 8) & 0xFF);
        outBytes[42] = (byte)((dataSize >> 16) & 0xFF); outBytes[43] = (byte)((dataSize >> 24) & 0xFF);
        long off = 44;
        foreach (var s in list)
        {
            Buffer.BlockCopy(s, 44, outBytes, (int)off, s.Length - 44);
            off += s.Length - 44;
        }
        return outBytes;
    }

    public static async Task<(byte[] Data, double DurationSec)> SynthesizeWindowsAsync(
        string text,
        ChatTtsConfig cfg,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
            var selected = false;
            if (!string.IsNullOrWhiteSpace(cfg.VoiceId))
            {
                try { synth.SelectVoice(cfg.VoiceId.Trim()); selected = true; }
                catch { /* 语音名不存在时退回按语言选择 */ }
            }
            if (!selected)
            {
                var langPrefix = (cfg.TextLang ?? "ja").Trim() switch
                {
                    "ja" => "ja",
                    "zh" => "zh",
                    "en" => "en",
                    _ => null,
                };
                if (langPrefix != null)
                {
                    try
                    {
                        var voice = synth.GetInstalledVoices()
                            .FirstOrDefault(v =>
                            {
                                try { return v.Enabled && v.VoiceInfo?.Culture?.Name.StartsWith(langPrefix, StringComparison.OrdinalIgnoreCase) == true; }
                                catch { return false; }
                            });
                        if (voice?.VoiceInfo != null)
                        {
                            synth.SelectVoice(voice.VoiceInfo.Name);
                        }
                    }
                    catch { }
                }
            }
            if (cfg.SpeedFactor > 0)
                synth.Rate = (int)Math.Clamp(Math.Round((cfg.SpeedFactor - 1.0) * 10), -10, 10);
            using var ms = new MemoryStream();
            synth.SetOutputToWaveStream(ms);
            synth.Speak(text);
            synth.SetOutputToNull();
            var bytes = ms.ToArray();
            if (bytes.Length == 0) throw new Exception("Windows 语音合成返回空音频");
            return (bytes, EstimateWavDurationSec(bytes));
        }, ct);
    }

    public static List<string> GetInstalledWindowsVoices()
    {
        var voices = new List<string>();
        try
        {
            using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
            foreach (var v in synth.GetInstalledVoices())
            {
                var info = v.VoiceInfo;
                if (info != null) voices.Add(info.Name);
            }
        }
        catch { }
        return voices;
    }

    public static double EstimateWavDurationSec(byte[] data)
    {
        try
        {
            if (data.Length > 44 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F')
            {
                var byteRate = BitConverter.ToInt32(data, 28);
                if (byteRate > 0) return data.Length / (double)byteRate;
            }
        }
        catch { }
        return 0;
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
        return s[..max] + "…";
    }

    /// <summary>
    /// 并行流式合成：立即发起请求并把收到的分句写入 Channel，由调用方按序消费。
    /// ready 在收到第一段音频（或流结束）时完成，用于确保服务端已处理 stop_prev 后再发起后续并行请求，
    /// 避免后续请求被自己带 stop_prev 的段杀掉。
    /// </summary>
    public static (IAsyncEnumerable<byte[]> Stream, Task Ready) StartStreamingBuffered(
        string baseUrl, string text, ChatTtsConfig cfg, string? emotion, bool stopPrev, CancellationToken ct)
    {
        var ch = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = false;
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var seg in SynthesizeStreamAsync(baseUrl, text, cfg, emotion, stopPrev, ct).WithCancellation(ct))
                {
                    if (!started) { started = true; ready.TrySetResult(); }
                    await ch.Writer.WriteAsync(seg, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error("TTS 流式合成失败: " + (text.Length > 24 ? text[..24] + "…" : text), ex);
            }
            finally
            {
                if (!started) { started = true; ready.TrySetResult(); }
                ch.Writer.TryComplete();
            }
        }, ct);
        return (ch.Reader.ReadAllAsync(ct), ready.Task);
    }
}