using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json.Nodes;

namespace DesktopPetUi.Core.Agent;

/// <summary>
/// Agent 循环驱动器：LLM 回复中解析 [tool]{...}[/tool] 调用 → 分级（自动/确认）→ 执行 →
/// 结果回填，直到模型给出最终回答或达到最大步数。中间的工具往返只存在于本次运行的
/// 工作消息列表，不进入长期对话历史。
/// </summary>
public sealed class AgentRunner
{
    private readonly AppConfig _config;

    public Action<string>? DebugLog { get; set; }

    /// <summary>每次工具调用决策后触发（自动放行/用户允许/用户拒绝），由管线负责持久化与广播。</summary>
    public Action<AgentOpRecord>? OnOp { get; set; }

    /// <summary>循环中每追加一条工作消息（[tool] 调用 / [result] 反馈）触发，由管线写入长期历史。</summary>
    public Action<ChatMessage>? OnMessage { get; set; }

    /// <summary>每次 LLM 补全后触发 (prompt_tokens, 发送总字数)；agent 每步都带完整历史，由管线校准 token/字比率并刷新显示。</summary>
    public Action<int, int>? OnUsage { get; set; }

    public AgentRunner(AppConfig config) => _config = config;

    /// <summary>
    /// 找第一个 [tool] 块：标记后做大括号配平扫描（识别 JSON 字符串与转义）取出完整 JSON。
    /// 比正则健壮：容忍缺少 [/tool]、JSON 内换行、代码围栏等格式差异。
    /// 返回 (jsonStart, jsonEndExclusive)；找不到或未闭合返回 null。
    /// </summary>
    private static (int Start, int End)? FindToolBlock(string s)
    {
        var marker = s.IndexOf("[tool]", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;
        var i = s.IndexOf('{', marker);
        if (i < 0 || i - marker > 64) return null;
        var depth = 0;
        var inStr = false;
        var esc = false;
        for (var j = i; j < s.Length; j++)
        {
            var c = s[j];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
            }
            else if (c == '"') inStr = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return (i, j + 1);
            }
        }
        return null;
    }

    /// <summary>执行 agent 循环，返回最终回答文本（不含工具块）。</summary>
    public async Task<string> RunAsync(IReadOnlyList<ChatMessage> seedMessages, ISpeakHost host)
    {
        var maxSteps = Math.Clamp(_config.Chat.Agent.MaxSteps, 1, 32);
        var messages = new List<ChatMessage>(seedMessages);

        for (var step = 1; step <= maxSteps; step++)
        {
            var reply = await CompleteAsync(messages);
            if (string.IsNullOrWhiteSpace(reply)) return "";

            string? feedback = null; // 非 null 时回填给模型继续循环
            List<string>? feedbackImages = null; // 工具结果携带的截图（随反馈消息发给视觉模型）
            var block = FindToolBlock(reply);
            if (block is { } b)
            {
                var jsonText = reply[b.Start..b.End];
                var call = ParseCall(jsonText, out var parseError);
                if (call == null)
                {
                    Log.Info("Agent 工具 JSON 解析失败: " + EscapeForLog(Truncate(jsonText, 800)));
                    feedback = "[error] " + parseError;
                }
                else
                {
                    Log.Info($"Agent step {step}/{maxSteps}: {call.Name} raw={EscapeForLog(jsonText)}");
                    DebugLog?.Invoke("[" + DateTime.Now.ToString("HH:mm:ss") + "] agent 工具调用: " + call.Name + "  " + call.Description.Replace("\n", " ⏎ "));

                    var (tier, autoReason) = AgentTools.Classify(call, _config);
                    if (tier == ToolTier.Auto && call.Risk == "high" && !AgentTools.TargetInTrustedDir(call, _config))
                        tier = ToolTier.Confirm; // 模型自评高风险 → 升级为确认（宿主分级仍是底线；信任目录豁免——用户已显式授权其下文件操作）
                    string? trustNote = null;
                    if (tier == ToolTier.Confirm)
                    {
                        var question = call.Description
                            + (call.Risk.Length > 0 ? "\n（模型自评：" + call.Risk
                                + (call.RiskNote.Length > 0 ? "，" + Truncate(call.RiskNote, 60) : "") + "）" : "");
                        DebugLog?.Invoke("[" + DateTime.Now.ToString("HH:mm:ss") + "] agent 需要用户确认: " + question.Replace("\n", " ⏎ "));
                        var trustDir = AgentTools.TrustableDirFor(call, _config);
                        var res = await host.ConfirmAsync(new ConfirmRequest
                        {
                            Title = call.Title,
                            Detail = call.Detail,
                            Risk = call.Risk,
                            RiskNote = call.RiskNote,
                            Question = question,
                            TrustableDir = trustDir,
                        });
                        if (!res.Allowed)
                            feedback = DeclineResult(call.Name);
                        else if (res.TrustFolder && trustDir != null)
                            trustNote = "用户已授权目录「" + trustDir + "」：其下文件操作直接放行，字面路径全部位于该目录的 PowerShell 命令也无需再问（无路径/含变量的命令仍会确认）。";
                    }

                    // 审计记录：每次工具调用的裁定都持久化（自动放行 / 用户允许 / 用户拒绝）；auto 备注=真实放行原因
                    var verdict = feedback != null ? "denied" : (tier == ToolTier.Auto ? "auto" : "allowed");
                    var opNote = verdict switch
                    {
                        "auto" => autoReason,
                        "allowed" => trustNote != null ? "并信任该目录" : "",
                        _ => "",
                    };
                    OnOp?.Invoke(new AgentOpRecord
                    {
                        Tool = call.Name,
                        Title = call.Title,
                        Detail = call.Detail,
                        Verdict = verdict,
                        Note = opNote,
                    });

                    if (feedback == null)
                    {
                        var result = await AgentTools.ExecuteAsync(call.Name, call.Args, _config, host);
                        Log.Info("Agent result [" + call.Name + "]: " + EscapeForLog(Truncate(result.Text, 500)));
                        DebugLog?.Invoke("[" + DateTime.Now.ToString("HH:mm:ss") + "] agent 工具结果: " + Truncate(result.Text, 500).Replace("\n", " ⏎ "));
                        feedback = (trustNote != null ? "[note] " + trustNote + "\n" : "") + "[result] " + Truncate(result.Text, 2000);
                        feedbackImages = result.Images;
                    }
                }
            }
            else if (reply.IndexOf("[tool]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Log.Info("Agent 工具块不完整: " + EscapeForLog(Truncate(reply, 800)));
                feedback = "[error] 工具调用块不完整（缺少 [tool]{...} 或大括号未闭合）。请重新完整输出：" + SingleLineFormat;
            }
            else
            {
                return reply; // 最终回答（情感标签由管线后续解析）
            }

            var aMsg = new ChatMessage { Role = "assistant", Content = reply };
            messages.Add(aMsg);
            OnMessage?.Invoke(aMsg); // 中间往返进长期历史：模型跨对话保留工具用法与操作记忆（opencode 式）
            var fbMsg = new ChatMessage { Role = "user", Content = feedback };
            if (feedbackImages != null) fbMsg.ImageBase64s = feedbackImages;
            messages.Add(fbMsg);
            OnMessage?.Invoke(new ChatMessage { Role = "user", Content = feedback }); // 只持久化文本，截图 base64 不进 memory.json（防膨胀）
        }

        // 步数用尽：强制收束，不再允许工具调用
        DebugLog?.Invoke("[" + DateTime.Now.ToString("HH:mm:ss") + "] agent 达到最大步数，强制收束");
        messages.Add(new ChatMessage { Role = "user", Content = "[system] 已达到最大工具调用次数。不要再输出 [tool] 块，直接基于已有结果给用户最终回答。" });
        var final = await CompleteAsync(messages);
        return StripToolBlocks(final ?? "");
    }

    private string DeclineResult(string name) =>
        "[result] 用户拒绝了「" + name + "」操作，没有执行。不要重试同一操作，直接告诉用户你取消了；如需要可建议替代方案。";

    private ParsedToolCall? ParseCall(string jsonText, out string error)
    {
        error = "";
        try
        {
            if (JsonNode.Parse(jsonText)?.AsObject() is not { } obj)
            {
                error = "工具调用 JSON 解析失败。请严格输出单行格式：[tool]{\"name\":\"工具名\",\"args\":{...}}[/tool]";
                return null;
            }
            var name = obj["name"]?.GetValue<string>()?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "工具调用缺少 name 字段。请严格输出单行格式：[tool]{\"name\":\"工具名\",\"args\":{...}}[/tool]";
                return null;
            }
            if (!AgentTools.Known(name))
            {
                error = "未知工具「" + name + "」。只能用系统提示【工具】一节列出的工具。";
                return null;
            }
            var args = obj["args"]?.AsObject();
            if (args == null)
            {
                // 容错：模型有时把参数平铺在顶层而非 "args" 对象里，此时收集除 name/risk/risk_note 外的字段
                args = new JsonObject();
                foreach (var kv in obj)
                {
                    if (kv.Key is "name" or "risk" or "risk_note") continue;
                    var node = kv.Value;
                    if (node == null) continue;
                    args[kv.Key] = JsonNode.Parse(node.ToJsonString());
                }
            }
            var call = AgentTools.Build(name, args, _config);
            if (call != null)
            {
                // 模型自评风险（可选字段，缺失/非法则忽略）
                call.Risk = (obj["risk"]?.ToString() ?? "").Trim().ToLowerInvariant();
                if (call.Risk is not ("low" or "medium" or "high")) call.Risk = "";
                call.RiskNote = (obj["risk_note"]?.ToString() ?? "").Trim();
            }
            return call;
        }
        catch
        {
            error = "工具调用 JSON 解析失败。请严格输出单行格式：[tool]{\"name\":\"工具名\",\"args\":{...}}[/tool]";
            return null;
        }
    }

    private async Task<string> CompleteAsync(List<ChatMessage> messages)
    {
        var ep = _config.EffectiveLlm();
        DebugLog?.Invoke("[" + DateTime.Now.ToString("HH:mm:ss") + "] → agent llama " + ep.Url + " model=" + ep.Model);
        var result = await LlamaClient.CompleteAsync(
            ep.Url, messages, ep.Model,
            _config.EffectiveTemperature, _config.EffectiveMaxTokens,
            ep.ApiKey, ep.ExtraParams);
        // 每步都带 system+完整历史：上报真实 usage（上下文占用显示与 token/字比率校准）
        OnUsage?.Invoke(result.Usage.PromptTokens, messages.Sum(m => m.Content?.Length ?? 0));
        DebugLog?.Invoke("[" + DateTime.Now.ToString("HH:mm:ss") + "] ← agent llama 回复:\n" + result.Text);
        return result.Text;
    }

    private const string SingleLineFormat = "[tool]{\"name\":\"工具名\",\"args\":{...}}[/tool]";

    /// <summary>兜底：若强制收束后模型仍输出工具块，剥掉再返回。</summary>
    private static string StripToolBlocks(string s)
    {
        var result = s;
        while (true)
        {
            var m = result.IndexOf("[tool]", StringComparison.OrdinalIgnoreCase);
            if (m < 0) break;
            var block = FindToolBlock(result.Substring(m));
            if (block is not { } b)
            {
                result = result[..m].TrimEnd(); // 不完整块：从 [tool] 起截断
                break;
            }
            var end = m + b.End;
            var tail = result[end..];
            var t = tail.IndexOf("[/tool]", StringComparison.OrdinalIgnoreCase);
            if (t >= 0 && t < 32) end += t + "[/tool]".Length;
            result = result[..m] + result[end..];
        }
        return result.Trim();
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "…";
    }

    /// <summary>把不可见控制字符转成 \uXXXX，写日志时可见（排查模型输出异常用）。</summary>
    private static string EscapeForLog(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c < 0x20 || c == 0x7F ? "\\u" + ((int)c).ToString("x4") : c);
        return sb.ToString();
    }
}
