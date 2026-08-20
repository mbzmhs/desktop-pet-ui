using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    [System.Text.Json.Serialization.JsonIgnore]
    public bool PopupFollowsPet { get; set; } = true; // 已废弃：聊天窗改为独立窗口+记忆位置，不再跟随宠物
    public bool AlwaysOnTop { get; set; } = true;
    public double Width { get; set; } = 420;
    public double Height { get; set; } = 560;
    public int MaxBubbleChars { get; set; } = 120;
    /// <summary>记忆窗口位置（NaN=未设置，首次打开时放到宠物附近并记住）。</summary>
    public double X { get; set; } = double.NaN;
    public double Y { get; set; } = double.NaN;
}

public sealed class ChatAgentConfig
{
    public bool Enabled { get; set; } = false; // Agent 总开关（false=纯聊天，不注入工具说明）
    public int MaxSteps { get; set; } = 8;     // 单次对话最大工具调用次数；0=不限（循环直到模型不再调工具）
    public double PsTimeoutSec { get; set; } = 60.0;   // 同步 run_powershell 超时（秒）
    public double JobMaxMinutes { get; set; } = 30.0;  // 后台任务硬上限（分钟），到点强杀
    public int MaxRunningJobs { get; set; } = 4;       // 同时运行的后台任务数上限
    public string WorkDir { get; set; } = "";          // PowerShell/相对路径工作目录（空=程序所在目录）
    public int ReadFileMaxLines { get; set; } = 400;   // read_file 最多返回行数

    /// <summary>工作目录的文件写/删权限：auto=智能（新建自动、覆盖删除确认）write=全部自动 readonly=全部确认。默认 readonly。</summary>
    public string WorkDirPerm { get; set; } = "readonly";
    /// <summary>其他目录的文件写/删权限，取值同上。默认 auto。</summary>
    public string OtherDirPerm { get; set; } = "auto";
    /// <summary>信任目录列表（全局共享的活集合，设置页与确认弹窗直接增删同一实例）：其下的文件操作直接放行；字面路径全部位于其中的 PowerShell 命令同样放行。</summary>
    public ObservableCollection<string> TrustedDirs { get; set; } = new();
    /// <summary>observe_screen 工具捕获的屏幕（1-based 编号）；空列表=当前鼠标所在屏幕。</summary>
    public List<int> AgentScreens { get; set; } = new();

    /// <summary>PowerShell 低风险命令自动放行策略：llm=只信 LLM 自评（risk=low 或 read_only=true，宽松但有风险）；dual=LLM 自评+宿主 IsLowRiskCommand 复核（推荐，默认）；off=不自动放行（只读命令一律确认）。路径范围规则不受此开关影响。</summary>
    public string PsAutoPolicy { get; set; } = "dual";
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
    public bool ReadInnerThoughts { get; set; } = false; // 是否朗读 （）() 和 【】 内的内心想法/小动作；false 时发送给 TTS 的文本剔除括号内容
    public ChatHotkeyConfig Hotkey { get; set; } = new();
    public ChatUiConfig Ui { get; set; } = new();
    public int ContextLength { get; set; } = 80;
    /// <summary>上下文预算（token）：总占用（系统提示+历史）达预算时触发摘要压缩、压到 ≤70%（滞回）；聊天窗标题栏实时显示占用比。
    /// 模型接口自报的上下文上限更低时，实际预算取「上限−输出预留」与它的较小者（保证请求不超模型能力而报错）。默认 16000。</summary>
    public int ContextMaxTokens { get; set; } = 16000;
    public int ArchiveMaxEntries { get; set; } = 5000; // 归档记录上限（条），0=无上限
    public bool Proactive { get; set; } = false;
    public double ProactiveIntervalSec { get; set; } = 30.0;
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
    public ChatAgentConfig Agent { get; set; } = new();
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

    [JsonIgnore]
    public string CharacterDir => Path.Combine(AppContext.BaseDirectory, Character.Dir);

    [JsonIgnore]
    public string EffectiveSystemPrompt =>
        !string.IsNullOrWhiteSpace(ActiveCharacter?.Llm?.SystemPrompt)
            ? ActiveCharacter!.Llm.SystemPrompt
            : "";

    [JsonIgnore]
    public double EffectiveTemperature =>
        ActiveCharacter?.Llm?.Temperature is double t ? t : Chat.Llama.Temperature;

    [JsonIgnore]
    public int EffectiveMaxTokens =>
        ActiveCharacter?.Llm?.MaxTokens is int m ? m : Chat.Llama.MaxTokens;

    [JsonIgnore]
    public double EffectiveProactiveTemperature =>
        ActiveCharacter?.ProactiveTemperature is double t ? t : EffectiveTemperature;

    [JsonIgnore]
    public double EffectiveScale =>
        ActiveCharacter?.Scale is double s && s > 0 ? s : Character.Scale;

    [JsonIgnore]
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

    [JsonIgnore]
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

    /// <summary>角色显示名：优先 character.json 里的 name 字段，缺省回退到文件夹名。</summary>
    public string CharacterDisplayName(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName)) return "";
        if (string.Equals(folderName, Character.Current, StringComparison.OrdinalIgnoreCase) &&
            ActiveCharacter != null)
            return string.IsNullOrWhiteSpace(ActiveCharacter.Name) ? folderName : ActiveCharacter.Name.Trim();
        var p = CharacterProfile.Load(Path.Combine(CharacterDir, folderName, "character.json"));
        return string.IsNullOrWhiteSpace(p.Name) ? folderName : p.Name.Trim();
    }

    /// <summary>当前生效角色的显示名（文件夹名为唯一标识，显示名仅用于界面展示）。</summary>
    public string EffectiveCharacterName => CharacterDisplayName(Character.Current ?? "");

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
