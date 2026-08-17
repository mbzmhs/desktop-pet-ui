using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPetUi.Core;

public static class TtsClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(300) };
    private static string? _voicesBase;
    private static HashSet<string>? _cachedEmotions;
    private static List<(string Id, string Display)>? _cachedVoices;

    /// <summary>当前激活音色可用的情感集合（首次查询后缓存）。失败时返回仅含 neutral 的集合。</summary>
    public static async Task<HashSet<string>> GetAvailableEmotionsAsync(string baseUrl, CancellationToken ct = default)
    {
        if (_voicesBase == baseUrl && _cachedEmotions != null) return _cachedEmotions;
        try
        {
            var url = baseUrl.TrimEnd('/') + "/voices";
            using var resp = await Http.GetAsync(url, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("active_voice", out var av) && av.ValueKind == JsonValueKind.String &&
                    data.TryGetProperty("voices", out var voices) &&
                    voices.TryGetProperty(av.GetString() ?? "", out var voice) &&
                    voice.TryGetProperty("emotions", out var emo) && emo.ValueKind == JsonValueKind.Object)
                {
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in emo.EnumerateObject()) set.Add(kv.Name);
                    _voicesBase = baseUrl;
                    _cachedEmotions = set;
                    return set;
                }
            }
        }
        catch { }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "neutral" };
    }

    /// <summary>从 tts-server 的 /voices 接口读取所有可选音色（首次查询后缓存）。返回 (原始ID, 显示名)。</summary>
    public static async Task<List<(string Id, string Display)>> GetAvailableVoicesAsync(string baseUrl, CancellationToken ct = default)
    {
        var list = new List<(string Id, string Display)>();
        if (_voicesBase == baseUrl && _cachedVoices != null) return _cachedVoices;
        try
        {
            var url = baseUrl.TrimEnd('/') + "/voices";
            using var resp = await Http.GetAsync(url, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("voices", out var voices) && voices.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kv in voices.EnumerateObject())
                    {
                        var id = kv.Name;
                        var display = id;
                        if (kv.Value.ValueKind == JsonValueKind.Object &&
                            kv.Value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(name.GetString()) &&
                            !string.Equals(name.GetString(), id, StringComparison.Ordinal))
                        {
                            display = id + "（" + name.GetString()!.Trim() + "）";
                        }
                        list.Add((id, display));
                    }
                }
            }
        }
        catch { }
        if (list.Count > 0)
        {
            _voicesBase = baseUrl;
            _cachedVoices = list;
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

        var url = baseUrl.TrimEnd('/') + "/tts";
        var payload = new
        {
            text = text,
            text_lang = cfg.TextLang,
            voice_id = string.IsNullOrEmpty(cfg.VoiceId) ? null : cfg.VoiceId,
            emotion = string.IsNullOrEmpty(emotion) ? cfg.Emotion : emotion,
            speed_factor = cfg.SpeedFactor,
            media_type = "wav",
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
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = baseUrl.TrimEnd('/') + "/tts";
        var payload = new
        {
            text = text,
            text_lang = cfg.TextLang,
            voice_id = string.IsNullOrEmpty(cfg.VoiceId) ? null : cfg.VoiceId,
            emotion = string.IsNullOrEmpty(emotion) ? cfg.Emotion : emotion,
            speed_factor = cfg.SpeedFactor,
            media_type = "wav",
            streaming = true,
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
}