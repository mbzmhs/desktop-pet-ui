using System.Text.Json;
using DesktopPetUi.Plugins;

namespace HelloPlugin;

/// <summary>
/// 示例插件：演示全部能力（注册/消息链/工具路由/设定持久化）。
/// 构建后把 bin\Release\net8.0\HelloPlugin.dll 复制到程序目录 plugins\ 下，重启即生效。
/// </summary>
public sealed class HelloPlugin : IPlugin
{
    private IPluginContext? _ctx;

    // 插件自身状态：Register 时从持久化设定初始化，UpdateSetting 时更新
    private string _greeting = "你好";
    private bool _stamp;

    public PluginInfo? Register(IPluginContext ctx, IReadOnlyDictionary<string, JsonElement> settings)
    {
        _ctx = ctx;
        ApplySettings(settings); // 首次为空字典 → 用默认值
        ctx.Log("注册成功（greeting=" + _greeting + ", stamp=" + _stamp + "）");

        return new PluginInfo
        {
            Name = "hello", // 建议与 dll 文件名一致
            Version = "1.0.0",
            Author = "sample",
            Description = "示例插件：演示消息链后缀、pet_greet 工具与两项设定。",
            Tools = new[]
            {
                new ToolDefinition
                {
                    Name = "pet_greet",
                    Parameters = "text?",
                    Description = "让桌宠用当前角色口吻问候用户（text=附加内容，可空）",
                },
            },
            ToolNames = new[] { "pet_greet" }, // 与 Tools 的 Name 对应；[tool] 命中即路由到 ExecuteToolAsync
        };
    }

    private const string Stamp = "—— hello plugin";

    /// <summary>消息链：LLM 回复流式结束以后、工具解析之前。直接原样返回=不修改。</summary>
    public string PreprocessReply(string reply, ReplyContext ctx)
    {
        if (!_stamp || ctx.Source == "agent-step") return reply; // 中间工具步不动（可能含 [tool] 协议）
        // 改过的文本会持久化进历史，模型下一轮可能把签名复读进回复——已含签名则不再追加
        return reply.Contains(Stamp, StringComparison.Ordinal) ? reply : reply + "\n" + Stamp;
    }

    /// <summary>工具执行：name 命中 ToolNames 才分发到这里；返回文本作为 [result] 回喂模型。</summary>
    public Task<string> ExecuteToolAsync(ToolCall call, CancellationToken ct)
    {
        var pet = _ctx?.GetPetInfo();
        var text = "";
        if (call.Args.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            text = "：" + t.GetString();

        var who = pet != null && !string.IsNullOrWhiteSpace(pet.Character) ? pet.Character : "小桌宠";
        var emotion = pet != null && !string.IsNullOrWhiteSpace(pet.Emotion) ? "（当前情绪：" + pet.Emotion + "）" : "";
        return Task.FromResult(_greeting + "！我是" + who + emotion + text);
    }

    public IReadOnlyList<SettingDef> GetSettings() => new[]
    {
        new SettingDef { Name = "greeting", Description = "问候语（pet_greet 工具使用）", Type = SettingType.String, Value = JsonSerializer.SerializeToElement(_greeting) },
        new SettingDef { Name = "stamp", Description = "在最终回复末尾追加「—— hello plugin」签名", Type = SettingType.Bool, Value = JsonDocument.Parse(_stamp ? "true" : "false").RootElement.Clone() },
    };

    public SettingResult UpdateSetting(string name, JsonElement value)
    {
        switch (name)
        {
            case "greeting":
                if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    return new SettingResult(false, "greeting 必须是非空字符串");
                _greeting = value.GetString()!.Trim();
                return new SettingResult(true);

            case "stamp":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    return new SettingResult(false, "stamp 必须是 true/false");
                _stamp = value.GetBoolean();
                return new SettingResult(true);

            default:
                return new SettingResult(false, "未知设定：" + name);
        }
    }

    public void Shutdown() => _ctx?.Log("已卸载");

    private void ApplySettings(IReadOnlyDictionary<string, JsonElement> settings)
    {
        if (settings.TryGetValue("greeting", out var g) && g.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(g.GetString()))
            _greeting = g.GetString()!.Trim();
        if (settings.TryGetValue("stamp", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False)
            _stamp = s.GetBoolean();
    }
}
