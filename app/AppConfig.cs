using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopPetUi;

public sealed record LlmEndpoint(string Url, string Model, string ApiKey, string ExtraParams);

public sealed class ChatLlamaConfig
{
    public string Url { get; set; } = "http://127.0.0.1:8080";
    public string Model { get; set; } = "local";
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 512;
    public string SystemPrompt { get; set; } =
        "你是一个住在用户桌面上的陪伴型聊天助手，温柔体贴，像朋友一样关心用户，让人感到安心。" +
        "请始终使用简体中文回复，语气自然亲切，可以活泼、调侃或偶尔撒娇，但不要油腻。" +
        "回复要简短（2句以内），像日常聊天一样自然，不要长篇大论，不要重复用户的话。" +
        "回复结尾请附上1个情感标签，具体可选标签见本系统提示末尾的【情感标签】一节。";
}

public sealed class ChatTtsConfig
{
    public string Provider { get; set; } = "gptsovits"; // "gptsovits" | "windows"
    public string Url { get; set; } = "http://127.0.0.1:9880";
    public string? VoiceId { get; set; }
    public string TextLang { get; set; } = "ja";
    public string Emotion { get; set; } = "neutral";
    public double SpeedFactor { get; set; } = 1.0;
    public bool Streaming { get; set; } = false;
}

public sealed class ChatHotkeyConfig
{
    public string Modifiers { get; set; } = "Ctrl|Alt";
    public string Key { get; set; } = "Space";
}

public sealed class ChatUiConfig
{
    public bool PopupFollowsPet { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public double Width { get; set; } = 420;
    public double Height { get; set; } = 340;
    public int MaxBubbleChars { get; set; } = 120;
}

public sealed class ProxyConfig
{
    public string Mode { get; set; } = "system"; // "system" | "none" | "custom"
    public string Address { get; set; } = "";
}

public sealed class ChatConfig
{
    public bool Enabled { get; set; } = true;
    public ChatLlamaConfig Llama { get; set; } = new();
    public ChatTtsConfig Tts { get; set; } = new();
    public ChatHotkeyConfig Hotkey { get; set; } = new();
    public ChatUiConfig Ui { get; set; } = new();
    public int ContextLength { get; set; } = 20;
    public bool Proactive { get; set; } = false;
    public double ProactiveIntervalSec { get; set; } = 30.0;
    public bool ScreenAware { get; set; } = false;
    public double ScreenAwareChance { get; set; } = 0.3;
    public string UserAddress { get; set; } = "";
    public string Provider { get; set; } = "openai"; // 请求格式："openai"（OpenAI 兼容，默认）；未来可加 "anthropic" 等
    public string ApiKey { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "";
    public string ApiModel { get; set; } = "";
    public Dictionary<string, string> ProviderExtraParams { get; set; } = new()
    {
        ["openai"] = "{\"thinking\":{\"type\":\"disabled\"}}",
        ["anthropic"] = "{\"thinking\":{\"type\":\"disabled\"}}",
    };
    public ProxyConfig Proxy { get; set; } = new();
}

public sealed class CharacterConfig
{
    public string Dir { get; set; } = "character";
    public string Current { get; set; } = "鲸鱼娘";
    public double Scale { get; set; } = 1.0;
    public string IdleEmotion { get; set; } = "idle";
    public double IdleIntervalSec { get; set; } = 6.0;
    public double BubbleDurationSec { get; set; } = 6.0;
    public double BubbleReserve { get; set; } = 140.0;
    public bool CrossFade { get; set; } = false;
    public double Width { get; set; } = 512;
    public double Height { get; set; } = 720;
}

public sealed class CharacterLlmConfig
{
    public string SystemPrompt { get; set; } = "";
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
}

public sealed class CharacterTtsConfig
{
    public string? Provider { get; set; }
    public string? Url { get; set; }
    public string? VoiceId { get; set; }
    public string? TextLang { get; set; }
    public string? Emotion { get; set; }
    public double? SpeedFactor { get; set; }
    public bool? Streaming { get; set; }
}

public sealed class CharacterProfile
{
    public string Name { get; set; } = "";
    public CharacterLlmConfig Llm { get; set; } = new();
    public CharacterTtsConfig? Tts { get; set; }
    public double? ProactiveTemperature { get; set; }
    public string? UserAddress { get; set; }
    public double? Scale { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static CharacterProfile Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var p = JsonSerializer.Deserialize<CharacterProfile>(File.ReadAllText(path), Options);
                if (p != null) return p;
            }
        }
        catch
        {
            // fall back to empty profile on corrupt file
        }
        return new CharacterProfile();
    }

    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // persist failures are non-fatal
        }
    }
}

public sealed class AppConfig
{
    public bool Topmost { get; set; } = true;
    public bool ClickThroughAuto { get; set; } = true;
    public double X { get; set; } = double.NaN;
    public double Y { get; set; } = double.NaN;
    public double AlphaThreshold { get; set; } = 20.0;
    public int SampleThrottleMs { get; set; } = 24;

    [JsonIgnore]
    public string ConfigPath { get; set; } = "";

    [JsonIgnore]
    public CharacterProfile? ActiveCharacter { get; set; }

    public string CharacterDir => Path.Combine(AppContext.BaseDirectory, Character.Dir);

    public string EffectiveSystemPrompt =>
        !string.IsNullOrWhiteSpace(ActiveCharacter?.Llm?.SystemPrompt)
            ? ActiveCharacter!.Llm.SystemPrompt
            : Chat.Llama.SystemPrompt;

    public double EffectiveTemperature =>
        ActiveCharacter?.Llm?.Temperature is double t ? t : Chat.Llama.Temperature;

    public int EffectiveMaxTokens =>
        ActiveCharacter?.Llm?.MaxTokens is int m ? m : Chat.Llama.MaxTokens;

    public double EffectiveProactiveTemperature =>
        ActiveCharacter?.ProactiveTemperature is double t ? t : EffectiveTemperature;

    public double EffectiveScale =>
        ActiveCharacter?.Scale is double s && s > 0 ? s : Character.Scale;

    public string EffectiveUserAddress
    {
        get
        {
            var v = ActiveCharacter?.UserAddress;
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            return Chat.UserAddress.Trim();
        }
    }

    public ChatTtsConfig EffectiveTts()
    {
        var tts = ActiveCharacter?.Tts;
        if (tts == null)
        {
            // 角色未配置 TTS 时默认不调用语音
            return new ChatTtsConfig { Provider = "none", TextLang = "auto" };
        }
        return new ChatTtsConfig
        {
            Provider = string.IsNullOrWhiteSpace(tts.Provider) ? "none" : tts.Provider.Trim(),
            Url = tts.Url ?? Chat.Tts.Url,
            VoiceId = tts.VoiceId,
            TextLang = string.IsNullOrWhiteSpace(tts.TextLang) ? "auto" : tts.TextLang.Trim(),
            Emotion = tts.Emotion ?? Chat.Tts.Emotion,
            SpeedFactor = tts.SpeedFactor ?? Chat.Tts.SpeedFactor,
            Streaming = tts.Streaming ?? Chat.Tts.Streaming,
        };
    }

    public string EffectiveTextLang
    {
        get
        {
            var v = EffectiveTts().TextLang;
            if (string.IsNullOrWhiteSpace(v)) return "ja";
            var l = v.Trim().ToLowerInvariant();
            if (l.StartsWith("zh")) return "zh";
            if (l.StartsWith("ja")) return "ja";
            if (l.StartsWith("en")) return "en";
            return l;
        }
    }

    public LlmEndpoint EffectiveLlm()
    {
        var format = string.IsNullOrWhiteSpace(Chat.Provider) ? "openai" : Chat.Provider.Trim();
        var baseUrl = !string.IsNullOrWhiteSpace(Chat.ApiBaseUrl)
            ? Chat.ApiBaseUrl.Trim()
            : Chat.Llama.Url;
        var model = !string.IsNullOrWhiteSpace(Chat.ApiModel)
            ? Chat.ApiModel.Trim()
            : Chat.Llama.Model;
        var extra = Chat.ProviderExtraParams.TryGetValue(format, out var ep) ? ep ?? "" : "";
        return new LlmEndpoint(baseUrl, model, Chat.ApiKey ?? "", extra);
    }

    public void LoadActiveCharacter()
    {
        var charDir = string.IsNullOrWhiteSpace(Character.Current)
            ? CharacterDir
            : Path.Combine(CharacterDir, Character.Current);
        ActiveCharacter = CharacterProfile.Load(Path.Combine(charDir, "character.json"));
    }

    public List<string> ListCharacters()
    {
        if (!Directory.Exists(CharacterDir)) return new List<string>();
        return Directory.GetDirectories(CharacterDir)
            .Select(Path.GetFileName)
            .Where(x => !string.IsNullOrEmpty(x))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    public ChatConfig Chat { get; set; } = new();
    public CharacterConfig Character { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AppConfig Load(string path)
    {
        var cfg = new AppConfig { ConfigPath = path };
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options);
                if (loaded != null)
                {
                    loaded.ConfigPath = path;
                    return loaded;
                }
            }
        }
        catch
        {
            // fall back to defaults on corrupt config
        }
        return cfg;
    }

    public void Save()
    {
        try
        {
            if (string.IsNullOrEmpty(ConfigPath)) return;
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // persist failures are non-fatal
        }
    }
}
