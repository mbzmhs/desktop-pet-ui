using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Security;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPetUi.Core.Agent;

public enum ToolTier
{
    Auto,     // 只读 / 新建，直接执行
    Confirm,  // 删除、覆盖已有文件、非只读命令：先弹确认
}

/// <summary>工具执行结果：回填给模型的文本 + 可选截图（随反馈消息发给视觉模型）。</summary>
public sealed record ToolResult(string Text, List<string>? Images = null)
{
    public static implicit operator ToolResult(string text) => new(text);
}

/// <summary>一个工具调用的解析结果。</summary>
public sealed class ParsedToolCall
{
    public string Name = "";
    public JsonObject Args = new();
    /// <summary>确认气泡的动作标题（简短）。</summary>
    public string Title = "";
    /// <summary>确认气泡的操作详情全文（完整命令/路径，不截断关键信息）。</summary>
    public string Detail = "";
    /// <summary>确认气泡里展示给用户的操作描述（Title+Detail 摘要，用于日志）。</summary>
    public string Description = "";
    /// <summary>模型自评危险等级：low/medium/high（空=未提供）。仅作参考，宿主分级为准；high 时会升级为确认。</summary>
    public string Risk = "";
    /// <summary>模型对风险的一句话说明（展示用）。</summary>
    public string RiskNote = "";
}

/// <summary>
/// Agent 工具注册表与执行器。所有路径相对路径都锚定到 workDir；
/// 危险操作（删除 / 覆盖已有文件 / 非只读 PowerShell）由宿主端强制分级，不依赖模型自觉。
/// </summary>
public static class AgentTools
{
    private const int MaxResultChars = 2000;   // 回填给模型的单次结果上限
    private const int MaxListEntries = 200;    // list_dir 最多条目
    private const int MaxSearchVisited = 200_000; // search_files 最多访问文件数
    private const int SearchMaxDepth = 8;      // search_files 递归深度上限
    internal const int JobBufferCap = 16_000;   // 后台任务输出环形缓冲上限
    internal const int JobHistoryCap = 20;      // 保留的已完成任务数

    /// <summary>写操作特征：命中任意一条即视为非只读（宁可多问，不可误放）。</summary>
    private static readonly string[] PsWriteTokens =
    {
        "Set-", "New-", "Add-Content", "Remove-", "Move-", "Copy-", "Rename-", "Clear-",
        "Stop-", "Start-", "Invoke-", "Save-", "Update-", "Install-", "Uninstall-",
        "Enable-", "Disable-", "Register-", "Publish-", "Export-", "Out-File",
        "iex", "Invoke-Expression", "format-volume", "diskpart",
    };

    /// <summary>只读 cmdlet 动词前缀白名单（配合写操作特征双重校验）。</summary>
    private static readonly string[] PsReadOnlyVerbs =
    {
        "Get-", "Test-", "Select-", "Where-", "Sort-", "Measure-", "Group-", "Compare-",
        "Format-", "Resolve-", "Join-Path", "Split-Path", "ConvertTo-", "Write-Host",
        "Write-Output", "Out-String", "Out-Null", "Echo",
    };

    /// <summary>常用位置的真实绝对路径（注入系统提示词用，让模型知道"桌面/文档/下载"在哪）。不存在的位置返回 null。</summary>
    public static (string Home, string? Desktop, string? Documents, string? Downloads) KnownFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? desktop = SafeExisting(Environment.SpecialFolder.DesktopDirectory);
        string? docs = SafeExisting(Environment.SpecialFolder.MyDocuments);
        // Downloads 在中文系统上叫「下载」，没有 SpecialFolder，按候选名探测
        string? downloads = null;
        foreach (var name in new[] { "Downloads", "下载" })
        {
            try
            {
                var p = Path.Combine(home, name);
                if (Directory.Exists(p)) { downloads = p; break; }
            }
            catch { }
        }
        return (home, desktop, docs, downloads);

        static string? SafeExisting(Environment.SpecialFolder f)
        {
            try
            {
                var p = Environment.GetFolderPath(f);
                return !string.IsNullOrWhiteSpace(p) && Directory.Exists(p) ? p : null;
            }
            catch { return null; }
        }
    }

    public static string ResolveWorkDir(AppConfig cfg)
    {
        var w = (cfg.Chat.Agent.WorkDir ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(w))
        {
            try
            {
                var full = Path.GetFullPath(w);
                if (Directory.Exists(full)) return full;
            }
            catch { }
        }
        return AppContext.BaseDirectory; // 默认 = 程序所在目录
    }

    /// <summary>目标（文件或目录）是否位于任一信任目录下（含其本身；大小写不敏感，带路径边界检查）。</summary>
    public static bool InTrustedDir(string full, AppConfig cfg)
    {
        if (string.IsNullOrEmpty(full)) return false;
        var list = cfg.Chat.Agent.TrustedDirs;
        if (list == null || list.Count == 0) return false;
        foreach (var t in list)
        {
            var d = NormDir(t);
            if (d.Length > 0 && PathPrefixMatch(full, d)) return true;
        }
        return false;
    }

    /// <summary>目标是否位于工作目录下（含其本身）。</summary>
    public static bool InWorkDir(string full, AppConfig cfg)
    {
        if (string.IsNullOrEmpty(full)) return false;
        var d = NormDir(ResolveWorkDir(cfg));
        return d.Length > 0 && PathPrefixMatch(full, d);
    }

    private static string NormDir(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        try
        {
            var f = Path.GetFullPath(p);
            return f.EndsWith("\\", StringComparison.Ordinal) ? f : f + "\\";
        }
        catch { return ""; }
    }

    /// <summary>full 是否等于 dir 或位于 dir 内；dir 必须以 '\' 结尾。</summary>
    private static bool PathPrefixMatch(string full, string dirWithSlash)
    {
        var f = full.EndsWith("\\", StringComparison.Ordinal) ? full : full + "\\";
        return f.StartsWith(dirWithSlash, StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolvePath(string path, AppConfig cfg) => ResolvePathDetailed(path, cfg).Full;

    /// <summary>路径解析（带错误原因，用于排查模型输出的异常字符）。</summary>
    public static (string Full, string Error) ResolvePathDetailed(string path, AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(path)) return ("", "path 参数为空");
        try
        {
            var full = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(ResolveWorkDir(cfg), path));
            return (full, "");
        }
        catch (Exception ex)
        {
            return ("", ex.Message);
        }
    }

    public static bool Known(string name) => name switch
    {
        "read_file" or "list_dir" or "search_files" or "write_file" or
        "edit_file" or "delete_file" or "search_content" or "web_fetch" or
        "run_powershell" or "start_powershell" or "check_job" or "ask_user" or
        "observe_screen" => true,
        _ => false,
    };

    public static ParsedToolCall? Build(string name, JsonObject args, AppConfig cfg)
    {
        var call = new ParsedToolCall { Name = name, Args = args };
        switch (name)
        {
            case "read_file":
                call.Title = "读取文件";
                call.Detail = arg(args, "path");
                break;
            case "list_dir":
                call.Title = "列出目录";
                call.Detail = OrDefault(arg(args, "path"), cfg.Chat.Agent.WorkDir);
                break;
            case "search_files":
                call.Title = "按文件名搜索";
                call.Detail = "匹配：" + arg(args, "name_pattern") + (string.IsNullOrWhiteSpace(arg(args, "root_dir")) ? "" : "\n范围：" + arg(args, "root_dir"));
                break;
            case "write_file":
                var target = ResolvePath(arg(args, "path"), cfg);
                call.Title = File.Exists(target) ? "覆盖已有文件" : "创建新文件";
                call.Detail = target;
                break;
            case "edit_file":
                call.Title = "编辑文件（精确替换一处）";
                call.Detail = ResolvePath(arg(args, "path"), cfg);
                break;
            case "delete_file":
                call.Title = "删除（不可恢复）";
                call.Detail = arg(args, "path");
                break;
            case "search_content":
                call.Title = "搜索文件内容";
                call.Detail = "匹配：" + arg(args, "pattern") + (string.IsNullOrWhiteSpace(arg(args, "root_dir")) ? "" : "\n范围：" + arg(args, "root_dir"));
                break;
            case "web_fetch":
                call.Title = "抓取网页";
                call.Detail = arg(args, "url");
                break;
            case "ask_user":
                call.Title = "向用户提问";
                call.Detail = arg(args, "question");
                break;
            case "run_powershell":
                call.Title = "运行 PowerShell 命令";
                call.Detail = arg(args, "command") + PsPathHintLine(args);
                break;
            case "start_powershell":
                call.Title = "后台启动 PowerShell 任务";
                call.Detail = arg(args, "command") + PsPathHintLine(args);
                break;
            case "check_job":
                call.Title = "查询后台任务";
                call.Detail = arg(args, "job_id");
                break;
            case "observe_screen":
                call.Title = "观察屏幕（截图）";
                call.Detail = "";
                break;
        }
        call.Description = string.IsNullOrEmpty(call.Detail) ? call.Title : call.Title + "\n" + Truncate(call.Detail, 300);
        return call;
    }

    /// <summary>权限分级 + 自动放行原因（供审计记录）。危险操作由宿主端强制分级，不依赖模型自觉。</summary>
    public static (ToolTier Tier, string AutoReason) Classify(ParsedToolCall call, AppConfig cfg)
    {
        var name = call.Name;
        var args = call.Args;
        switch (name)
        {
            case "read_file":
            case "list_dir":
            case "search_files":
            case "search_content":
            case "web_fetch":
            case "check_job":
            case "ask_user":
            case "observe_screen":
                return (ToolTier.Auto, "");
            case "write_file":
            case "edit_file":
            case "delete_file":
                return ClassifyFileWrite(name, args, cfg);
            case "run_powershell":
            case "start_powershell":
                // PowerShell 与文件操作同一套规则：低风险路由（按 PsAutoPolicy）→ 路径范围路由；fail-safe：判不准一律确认
            {
                var cmd = arg(args, "command");
                // LLM 侧信号：顶层 risk=low（协议定义 low=只读/无副作用）或工具参数 read_only=true
                var llmSaysLow = string.Equals(call.Risk, "low", StringComparison.OrdinalIgnoreCase) || GetBool(args, "read_only");
                switch ((cfg.Chat.Agent.PsAutoPolicy ?? "").Trim().ToLowerInvariant())
                {
                    case "llm":   // 宽松：信 LLM 自评，宿主不再复核
                        if (llmSaysLow) return (ToolTier.Auto, "LLM自评低风险");
                        break;
                    case "dual":  // 双重审核（默认）：LLM 自评 + 宿主 IsLowRiskCommand 复核都过
                        if (llmSaysLow && IsLowRiskCommand(cmd)) return (ToolTier.Auto, "低风险复核");
                        break;
                    default:      // off：声明路由关闭，只读命令一律确认
                        break;
                }
                if (PsPathsInScope(cmd, GetStringList(args, "paths"), cfg)) return (ToolTier.Auto, "路径在允许范围");
                return (ToolTier.Confirm, "");
            }
            default:
                return (ToolTier.Confirm, ""); // 未知工具一律先问
        }
    }

    /// <summary>文件写/删的权限分级：信任目录直接放行；否则按工作目录 / 其他目录的权限设定。</summary>
    private static (ToolTier Tier, string AutoReason) ClassifyFileWrite(string name, JsonObject args, AppConfig cfg)
    {
        var agent = cfg.Chat.Agent;
        var target = ResolvePath(arg(args, "path"), cfg);
        if (target.Length > 0 && InTrustedDir(target, cfg)) return (ToolTier.Auto, "信任目录");

        var perm = (target.Length > 0 && InWorkDir(target, cfg) ? agent.WorkDirPerm : agent.OtherDirPerm) ?? "";
        switch (perm.Trim().ToLowerInvariant())
        {
            case "write":
                return (ToolTier.Auto, "范围全部可写"); // 该范围写操作全部自动放行
            case "readonly":
                return (ToolTier.Confirm, ""); // 该范围一切写/删都先确认
            default:
                // auto：新建文件自动，覆盖已有文件 / 删除需确认
                if (name == "write_file" && !File.Exists(target)) return (ToolTier.Auto, "新建文件");
                return (ToolTier.Confirm, "");
        }
    }

    /// <summary>本次调用的目标是否全部位于信任目录内（文件工具=path 所在；PowerShell=所有可验证字面路径都在其中）。用于豁免"模型自评 high → 升级确认"：用户已显式授权该目录的文件操作。</summary>
    public static bool TargetInTrustedDir(ParsedToolCall call, AppConfig cfg)
    {
        if (call.Name is ("write_file" or "edit_file" or "delete_file"))
        {
            var full = ResolvePath(call.Args["path"]?.ToString()?.Trim() ?? "", cfg);
            return full.Length > 0 && InTrustedDir(full, cfg);
        }
        if (call.Name is ("run_powershell" or "start_powershell"))
        {
            var paths = ExtractPsPaths(arg(call.Args, "command"));
            if (paths.Count == 0) return false;
            foreach (var raw in paths)
            {
                if (raw.IndexOfAny(PsUnverifiableChars) >= 0) continue; // 含变量无法判定，不参与
                string full;
                try
                {
                    full = Path.IsPathRooted(raw)
                        ? Path.GetFullPath(raw)
                        : Path.GetFullPath(Path.Combine(ResolveWorkDir(cfg), raw));
                }
                catch { return false; }
                if (full.Length == 0) return false;
                var probe = full;
                if (raw.IndexOfAny(new[] { '*', '?' }) >= 0)
                {
                    try { probe = Path.GetDirectoryName(full) ?? ""; } catch { return false; }
                    if (probe.Length <= 3) return false;
                }
                if (!InTrustedDir(probe, cfg)) return false; // 有任一可验证路径在信任目录外 → 不豁免
            }
            return true;
        }
        return false;
    }

    /// <summary>供确认气泡使用：本次操作可"信任该目录"的目录（目标所在目录）；无可信单一目录 / 盘符根返回 null。</summary>
    public static string? TrustableDirFor(ParsedToolCall call, AppConfig cfg)
    {
        if (call.Name is ("write_file" or "edit_file" or "delete_file"))
        {
            var full = ResolvePath(call.Args["path"]?.ToString()?.Trim() ?? "", cfg);
            if (full.Length == 0) return null;
            string? dir;
            try { dir = Path.GetDirectoryName(full); } catch { return null; }
            if (string.IsNullOrEmpty(dir)) return null;
            // 不允许一键信任盘符根（C:\）
            if (dir.TrimEnd('\\', '/').Length <= 2) return null;
            return dir;
        }
        if (call.Name is ("run_powershell" or "start_powershell"))
        {
            // 命令里提取到的字面路径都落在同一个目录时，才提供"信任该目录"
            var paths = ExtractPsPaths(arg(call.Args, "command"));
            if (paths.Count == 0) return null;
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in paths)
            {
                if (raw.IndexOfAny(PsUnverifiableChars) >= 0) continue;
                string full;
                try
                {
                    full = Path.IsPathRooted(raw)
                        ? Path.GetFullPath(raw)
                        : Path.GetFullPath(Path.Combine(ResolveWorkDir(cfg), raw));
                }
                catch { continue; }
                if (full.Length == 0) continue;
                string? d;
                try { d = Path.GetDirectoryName(full); } catch { continue; }
                if (!string.IsNullOrEmpty(d) && d.TrimEnd('\\', '/').Length > 2) dirs.Add(d);
            }
            return dirs.Count == 1 ? dirs.First() : null;
        }
        return null;
    }

    public static bool IsReadOnlyCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        try
        {
            // 去掉行注释
            var lines = new List<string>();
            foreach (var raw in command.Split('\n'))
            {
                var hash = raw.IndexOf('#');
                lines.Add(hash >= 0 ? raw[..hash] : raw);
            }
            var text = string.Join('\n', lines);

            // 输出重定向写文件（> / >>），排除 -gt/-lt 等比较运算符
            if (System.Text.RegularExpressions.Regex.IsMatch(text, "(^|[^\\w-])>{1,2}\\s")) return false;

            foreach (var segment in text.Split(';', '\n'))
            {
                foreach (var part in segment.Split('|'))
                {
                    var p = part.Trim();
                    if (p.Length == 0) continue;
                    // 提取 cmdlet 形态的词（Verb-Noun），逐个校验白名单
                    var tokens = System.Text.RegularExpressions.Regex.Matches(p, "[A-Za-z][A-Za-z]*-[A-Za-z]+");
                    // 没有任何可识别 cmdlet、却含字母的片段 = 外部命令（cmd / python / del 等）→ 按非只读处理
                    if (tokens.Count == 0 && System.Text.RegularExpressions.Regex.IsMatch(p, "[A-Za-z]")) return false;
                    foreach (var token in tokens)
                    {
                        var t = token.ToString() ?? "";
                        if (t.Length == 0) continue;
                        if (PsWriteTokens.Any(w => t.StartsWith(w, StringComparison.OrdinalIgnoreCase))) return false;
                        if (!PsReadOnlyVerbs.Any(v => t.StartsWith(v, StringComparison.OrdinalIgnoreCase))) return false;
                    }
                }
            }
            return true;
        }
        catch
        {
            return false; // 解析失败按非只读处理
        }
    }

    /// <summary>低风险复核的扩展只读动词表：严格白名单 + ForEach-Object（纯迭代）+ ConvertFrom-*（解析数据）。</summary>
    private static readonly string[] LowRiskReadOnlyVerbs = PsReadOnlyVerbs.Concat(new[] { "ForEach-", "ConvertFrom-" }).ToArray();

    /// <summary>
    /// 低风险 PowerShell 复核（PsAutoPolicy=dual 用）：宿主端静态校验无副作用的只读查询命令，不依赖模型自评。
    /// 比 IsReadOnlyCommand 宽松：允许脚本块（Where-Object {$_.CPU -gt 100} 这类属性访问/比较）与 ForEach-Object；
    /// 拒绝输出重定向、写特征词、.NET 静态调用（::）、方法调用（.Xxx()）与外部命令。任何一步判不准 → false（fail-safe → 弹确认）。
    /// </summary>
    public static bool IsLowRiskCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        try
        {
            var text = StripLineComments(command);
            // 输出重定向写文件（> / >>），排除 -gt/-lt 等比较运算符
            if (System.Text.RegularExpressions.Regex.IsMatch(text, "(^|[^\\w-])>{1,2}\\s")) return false;
            // .NET 静态调用（[System.IO.File]::Delete 等）与方法调用（$x.Delete() 等）→ 非低风险
            if (text.Contains("::")) return false;
            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\.\s*[A-Za-z_]\w*\s*\(")) return false;

            // 剔除字面路径（带连字符的文件名会被误认为 cmdlet token）
            var scrubbed = text;
            foreach (var p in ExtractPsPaths(text))
                if (p.Length > 0) scrubbed = scrubbed.Replace(p, " ");

            // 1) 摘除引号字符串（惰性数据）
            var unquoted = new StringBuilder(scrubbed.Length);
            for (var i = 0; i < scrubbed.Length; i++)
            {
                var c = scrubbed[i];
                if (c != '"' && c != '\'') { unquoted.Append(c); continue; }
                var j = i + 1;
                while (j < scrubbed.Length && scrubbed[j] != c)
                {
                    if (c == '"' && scrubbed[j] == '\\' && j + 1 < scrubbed.Length) j++;
                    j++;
                }
                if (j >= scrubbed.Length) return false; // 引号未闭合 → 不可判定
                unquoted.Append(' ');
                i = j;
            }

            // 2) 提取脚本块（平衡花括号）：从主文本剥离，单独校验
            var blocks = new List<string>();
            var main = new StringBuilder(unquoted.Length);
            for (var i = 0; i < unquoted.Length; i++)
            {
                if (unquoted[i] != '{') { main.Append(unquoted[i]); continue; }
                int depth = 0, start = i;
                for (; i < unquoted.Length; i++)
                {
                    if (unquoted[i] == '{') depth++;
                    else if (unquoted[i] == '}') { depth--; if (depth == 0) break; }
                }
                if (depth != 0) return false; // 花括号未闭合 → 不可判定
                blocks.Add(unquoted.ToString(start, i - start + 1));
            }

            // 3) 主文本按管道分段校验（与 IsReadOnlyCommand 同规则，用扩展动词表）
            foreach (var segment in main.ToString().Split(';', '\n'))
            {
                foreach (var partRaw in segment.Split('|'))
                {
                    var p = partRaw.Trim();
                    if (p.Length == 0) continue;
                    var tokens = System.Text.RegularExpressions.Regex.Matches(p, "[A-Za-z][A-Za-z]*-[A-Za-z]+");
                    // 没有任何可识别 cmdlet、却含字母的片段 = 外部命令（cmd / del 等）→ 非低风险
                    if (tokens.Count == 0 && System.Text.RegularExpressions.Regex.IsMatch(p, "[A-Za-z]")) return false;
                    foreach (var token in tokens)
                    {
                        var t = token.ToString() ?? "";
                        if (t.Length == 0) continue;
                        if (PsWriteTokens.Any(w => t.StartsWith(w, StringComparison.OrdinalIgnoreCase))) return false;
                        if (!LowRiskReadOnlyVerbs.Any(v => t.StartsWith(v, StringComparison.OrdinalIgnoreCase))) return false;
                    }
                }
            }

            // 4) 脚本块：允许属性访问/比较，拒绝写特征词（:: / 方法调用已在上面全局拒绝）
            foreach (var b in blocks)
                if (PsWriteTokens.Any(w => b.Contains(w, StringComparison.OrdinalIgnoreCase))) return false;

            return true;
        }
        catch
        {
            return false; // 解析失败按非低风险处理
        }
    }

    /// <summary>路径里出现这些字符即认为范围不可判定（变量 / 环境变量 / 反引号转义）。</summary>
    private static readonly char[] PsUnverifiableChars = { '$', '%', '`' };

    /// <summary>去掉行注释（与 IsReadOnlyCommand 一致：# 之后到行尾）。</summary>
    private static string StripLineComments(string command)
    {
        var lines = new List<string>();
        foreach (var raw in command.Split('\n'))
        {
            var hash = raw.IndexOf('#');
            lines.Add(hash >= 0 ? raw[..hash] : raw);
        }
        return string.Join('\n', lines);
    }

    private static bool LooksLikePath(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (s.Length >= 3 && char.IsLetter(s[0]) && s[1] == ':' && s[2] == '\\') return true; // C:\...
        if (s.StartsWith("\\\\", StringComparison.Ordinal)) return true;                        // \\server\share
        if (s.StartsWith(".\\", StringComparison.Ordinal) || s.StartsWith("..\\", StringComparison.Ordinal)
            || s.StartsWith("~\\", StringComparison.Ordinal)) return true;                       // 相对路径（锚定 workdir）
        return false;
    }

    /// <summary>
    /// 从 PS 命令中提取字面路径（带引号与不带引号的：盘符 / UNC / .\ ..\ ~\ 前缀），原样返回、不做解析。
    /// 只做保守的字面量识别；变量拼接等无法静态判定的情形由 PsPathsInScope 兜底为"不可判定"。
    /// </summary>
    public static List<string> ExtractPsPaths(string command)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(command)) return result;
        var text = StripLineComments(command);

        // 1) 先把引号串摘出来（带空格的路径都在引号里），剩余文本再去匹配不带引号的
        var rest = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '"' && c != '\'') { rest.Append(c); continue; }
            var j = i + 1;
            while (j < text.Length && text[j] != c)
            {
                if (c == '"' && text[j] == '\\' && j + 1 < text.Length) j++; // \" 转义
                j++;
            }
            if (j >= text.Length) break; // 引号未闭合：停止扫描，保守处理
            var inner = text.Substring(i + 1, j - i - 1);
            if (LooksLikePath(inner)) result.Add(inner);
            rest.Append(' ');
            i = j;
        }

        // 2) 不带引号的绝对 / 相对路径前缀
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            rest.ToString(),
            @"(?:[A-Za-z]:\\|\\\\[^\\\s]|(?:\.\.?|~)\\)[^""'|;&<>\s()`]*"))
        {
            var v = m.Value;
            if (LooksLikePath(v)) result.Add(v);
        }
        return result;
    }

    /// <summary>
    /// PS 命令的路径范围校验（仅用于 auto 审批模式下的非只读命令）：
    /// 按 ; / 换行分段，每段要么只读、要么含字面路径；所有字面路径（宿主提取 ∪ 模型声明，相对路径锚定 workdir）
    /// 必须落在信任目录 / 可写权限范围 / auto+目标不存在（新建）之内。任何一步判不准都返回 false（fail-safe → 弹确认）。
    /// </summary>
    public static bool PsPathsInScope(string command, IReadOnlyList<string>? declared, AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var agent = cfg.Chat.Agent;
        var workDir = ResolveWorkDir(cfg);
        string home = "";
        try { home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch { }

        var text = StripLineComments(command);
        // 按 ; 与换行分段（不按管道分：管道里的 token 同样全部进入并集校验）
        var segments = new List<string>();
        foreach (var seg in System.Text.RegularExpressions.Regex.Split(text, @"[;\r\n]+"))
            if (!string.IsNullOrWhiteSpace(seg)) segments.Add(seg);
        if (segments.Count == 0) return false;

        var union = new List<string>();
        foreach (var seg in segments)
        {
            if (IsReadOnlyCommand(seg)) continue;   // 只读段无需路径校验
            var found = ExtractPsPaths(seg);
            if (found.Count == 0) return false;     // 非只读段却提取不到字面路径（变量/管道来源等）→ 不可判定
            union.AddRange(found);
        }
        if (declared != null)
            foreach (var d in declared)
                if (!string.IsNullOrWhiteSpace(d)) union.Add(d.Trim());
        if (union.Count == 0) return false;

        foreach (var raw in union)
        {
            if (raw.IndexOfAny(PsUnverifiableChars) >= 0) return false; // $ % ` → 含变量，不可判定
            string full;
            try
            {
                var p = raw;
                if (p == "~" || p.StartsWith("~\\", StringComparison.Ordinal))
                    p = home.Length > 0 ? Path.Combine(home, p.Substring(p == "~" ? 1 : 2).TrimStart('\\')) : p;
                full = Path.IsPathRooted(p)
                    ? Path.GetFullPath(p)
                    : Path.GetFullPath(Path.Combine(workDir, p));
            }
            catch { return false; }
            if (full.Length == 0) return false;

            var probe = full;
            if (raw.IndexOfAny(new[] { '*', '?' }) >= 0)
            {
                // 通配：按其父目录判定（不能逐个验证）；指向盘符根则范围过宽
                try { probe = Path.GetDirectoryName(full) ?? ""; } catch { return false; }
                if (probe.Length <= 3) return false;
            }

            if (InTrustedDir(probe, cfg)) continue; // 信任目录 → 允许
            var perm = (InWorkDir(probe, cfg) ? agent.WorkDirPerm : agent.OtherDirPerm) ?? "";
            switch (perm.Trim().ToLowerInvariant())
            {
                case "write": break;               // 该范围可写 → 允许
                case "readonly": return false;     // 该范围只读 → 不放行
                default:                           // auto：只放行新建（目标不存在）
                    bool exists;
                    try { exists = File.Exists(probe) || Directory.Exists(probe); } catch { return false; }
                    if (exists) return false;
                    break;
            }
        }
        return true;
    }

    /// <summary>确认气泡里展示命令涉及的路径（宿主提取 ∪ 模型声明，最多 4 个）。</summary>
    private static string PsPathHintLine(JsonObject args)
    {
        var paths = ExtractPsPaths(arg(args, "command"));
        foreach (var d in GetStringList(args, "paths"))
            if (!paths.Contains(d, StringComparer.OrdinalIgnoreCase)) paths.Add(d);
        if (paths.Count == 0) return "";
        return "\n涉及路径：" + string.Join("、", paths.Take(4));
    }

    private static List<string> GetStringList(JsonObject args, string key)
    {
        var list = new List<string>();
        try
        {
            if (args[key] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    var s = item?.GetValue<string>()?.Trim();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
            }
            else if (args[key] is JsonValue v && v.TryGetValue<string>(out var single) && !string.IsNullOrWhiteSpace(single))
            {
                list.Add(single.Trim());
            }
        }
        catch { }
        return list;
    }

    /// <summary>执行工具，返回回填给模型的结果（错误也以文本形式返回，让模型自行调整）。host 供 ask_user 使用。</summary>
    public static async Task<ToolResult> ExecuteAsync(string name, JsonObject args, AppConfig cfg, ISpeakHost? host = null)
    {
        var agent = cfg.Chat.Agent;
        try
        {
            switch (name)
            {
                case "read_file": return ReadFile(arg(args, "path"), cfg);
                case "list_dir": return ListDir(arg(args, "path"), cfg);
                case "search_files":
                    return await Task.Run(() => SearchFiles(
                        arg(args, "name_pattern"), OrDefault(arg(args, "root_dir"), ""), args["max_results"]?.GetValue<int>() ?? 50, cfg));
                case "write_file": return CreateFile(arg(args, "path"), arg(args, "content"), cfg);
                case "edit_file":
                    return EditFile(arg(args, "path"), GetStringRaw(args, "old_string"), GetStringRaw(args, "new_string"), cfg);
                case "delete_file": return DeleteFile(arg(args, "path"), cfg);
                case "search_content":
                    return await Task.Run(() => SearchContent(
                        arg(args, "pattern"), OrDefault(arg(args, "root_dir"), ""), args["max_results"]?.GetValue<int>() ?? 50, cfg));
                case "web_fetch": return await WebFetch(arg(args, "url"));
                case "run_powershell":
                    return await Task.Run(() => RunPowerShellSync(arg(args, "command"), agent.PsTimeoutSec, cfg));
                case "start_powershell": return JobManager.Start(arg(args, "command"), cfg);
                case "check_job": return JobManager.Check(arg(args, "job_id"));
                case "ask_user":
                    if (host == null) return "错误：当前环境不支持向用户提问";
                    var q = arg(args, "question");
                    if (string.IsNullOrWhiteSpace(q)) return "错误：question 不能为空";
                    var r = await host.AskUserAsync(q);
                    return r.Answered
                        ? "用户回答：" + Truncate(r.Text, 500)
                        : "用户没有回答（超时或取消）。不要重复问同样的问题，基于已有信息继续，或直接告诉用户你暂时无法完成。";
                case "observe_screen":
                    var shots = await Task.Run(() => ScreenCapture.CaptureScreens(agent.AgentScreens));
                    var ok = shots.Where(s => !string.IsNullOrEmpty(s)).Cast<string>().ToList();
                    if (ok.Count == 0) return "错误：屏幕截图失败（无可用屏幕或权限不足）。请告诉用户无法查看屏幕。";
                    return new ToolResult(
                        "以下是当前屏幕截图（共 " + ok.Count + " 屏）。请基于图片中实际看到的内容回答，不要编造图中没有的信息。",
                        ok);
                default:
                    return "错误：未知工具「" + name + "」";
            }
        }
        catch (Exception ex)
        {
            Log.Error("AgentTool " + name + " failed", ex);
            return "错误：" + ex.Message;
        }
    }

    /// <summary>取字符串参数（与 arg 不同：不 Trim，保留 old_string/new_string 的前后空白与换行）。</summary>
    private static string GetStringRaw(JsonObject args, string key) => args[key]?.GetValue<string>() ?? "";

    /// <summary>
    /// 目标不存在时的错误提示（不做任何模糊匹配，防止误操作）：
    /// 附上该目录下现有的目录/文件名，让模型用准确名字重试。
    /// </summary>
    private static string MissingFileHint(string full, string original)
    {
        var dir = System.IO.Path.GetDirectoryName(full);
        var hint = "";
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            try
            {
                var parts = new List<string>();
                foreach (var d in Directory.GetDirectories(dir).Take(20))
                    parts.Add(System.IO.Path.GetFileName(d) + "/");
                foreach (var f in Directory.GetFiles(dir).Take(20))
                    parts.Add(System.IO.Path.GetFileName(f));
                if (parts.Count > 0)
                    hint = "。该目录下有：" + string.Join("、", parts);
            }
            catch { }
        }
        return "错误：目标不存在（请精确使用上面的名字重试） " + original + hint;
    }

    private static string ReadFile(string path, AppConfig cfg)
    {
        var (full, perr) = ResolvePathDetailed(path, cfg);
        if (full.Length == 0) return "错误：路径无效 " + path + "（" + perr + "）";
        if (!File.Exists(full)) return MissingFileHint(full, path);
        var maxLines = Math.Max(20, cfg.Chat.Agent.ReadFileMaxLines);
        using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        // 二进制检测：前 8KB 出现 NUL 字节即视为二进制（先于 StreamReader 创建，避免流位置失步）
        var head = new byte[8192];
        var n = fs.Read(head, 0, head.Length);
        if (head.Take(n).Contains((byte)0)) return "错误：这是二进制文件，无法读取 " + path;
        fs.Seek(0, SeekOrigin.Begin);
        using var sr = new StreamReader(fs);

        var sb = new StringBuilder();
        var lineNo = 0;
        string? line;
        while ((line = sr.ReadLine()) != null && lineNo < maxLines)
        {
            sb.AppendLine(line);
            lineNo++;
            if (sb.Length > 20_000) break;
        }
        var more = sr.ReadLine() != null;
        return Truncate(sb.ToString(), MaxResultChars) +
               (more ? "\n（内容过长已截断，仅显示前 " + lineNo + " 行）" : "");
    }

    private static string ListDir(string path, AppConfig cfg)
    {
        var full = ResolvePath(OrDefault(path, "."), cfg);
        if (!Directory.Exists(full)) return "错误：目录不存在 " + path;
        var sb = new StringBuilder();
        try
        {
            foreach (var d in Directory.GetDirectories(full).Take(MaxListEntries))
                sb.AppendLine(Path.GetFileName(d) + "/");
            foreach (var f in Directory.GetFiles(full).Take(MaxListEntries))
                sb.AppendLine(Path.GetFileName(f));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return "错误：无权限访问 " + full;
        }
        if (sb.Length == 0) return "（空目录）" + full;
        return Truncate(full + "\n" + sb.ToString(), MaxResultChars);
    }

    private static string SearchFiles(string pattern, string rootDir, int maxResults, AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "错误：name_pattern 不能为空";
        var root = ResolvePath(OrDefault(rootDir, "."), cfg);
        if (!Directory.Exists(root)) return "错误：目录不存在 " + rootDir;
        if (maxResults <= 0) maxResults = 50;

        var matches = new List<string>();
        var visited = 0;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && matches.Count < maxResults && visited < MaxSearchVisited)
        {
            var (dir, depth) = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(dir); } catch { continue; }
            foreach (var f in files)
            {
                visited++;
                if (MatchName(Path.GetFileName(f), pattern)) matches.Add(f);
            }
            if (depth >= SearchMaxDepth) continue;
            string[] dirs;
            try { dirs = Directory.GetDirectories(dir); } catch { continue; }
            foreach (var d in dirs)
            {
                var n = Path.GetFileName(d);
                if (n.StartsWith("$", StringComparison.Ordinal) || n == "node_modules") continue;
                stack.Push((d, depth + 1));
            }
        }
        if (matches.Count == 0) return "（未找到匹配「" + pattern + "」的文件）";
        return string.Join("\n", matches.Take(maxResults)) +
               (visited >= MaxSearchVisited ? "\n（扫描范围过大已提前停止）" : "");
    }

    private static bool MatchName(string name, string pattern) => WildcardMatch(name, pattern);

    private static bool WildcardMatch(string name, string pattern)
    {
        // 支持 * 与 ? 的简单通配（大小写不敏感）
        var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(name, rx,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string CreateFile(string path, string content, AppConfig cfg)
    {
        var (full, perr) = ResolvePathDetailed(path, cfg);
        if (full.Length == 0) return "错误：路径无效 " + path + "（" + perr + "）";
        try
        {
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(full, content ?? "");
            return "已写入 " + full + "（" + (content?.Length ?? 0) + " 字符）";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            return "错误：写入失败 " + ex.Message;
        }
    }

    private static string DeleteFile(string path, AppConfig cfg)
    {
        var (full, perr) = ResolvePathDetailed(path, cfg);
        if (full.Length == 0) return "错误：路径无效 " + path + "（" + perr + "）";
        if (full.Length < 4 || Path.GetPathRoot(full)?.TrimEnd('\\', '/') == full.TrimEnd('\\', '/'))
            return "错误：拒绝删除盘符根目录";
        // 严格精确匹配，不做模糊纠正（防止误删）
        if (!File.Exists(full) && !Directory.Exists(full))
            return MissingFileHint(full, path);
        try
        {
            if (Directory.Exists(full)) Directory.Delete(full, true);
            else File.Delete(full);
            return "已删除 " + full;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            return "错误：删除失败 " + ex.Message;
        }
    }

    /// <summary>局部编辑：old_string 必须与文件内容逐字符一致且唯一出现，替换为 new_string。不做模糊匹配（防误改）。</summary>
    private static string EditFile(string path, string oldStr, string newStr, AppConfig cfg)
    {
        if (string.IsNullOrEmpty(oldStr)) return "错误：old_string 不能为空";
        var (full, perr) = ResolvePathDetailed(path, cfg);
        if (full.Length == 0) return "错误：路径无效 " + path + "（" + perr + "）";
        if (!File.Exists(full)) return MissingFileHint(full, path);
        string original;
        try { original = File.ReadAllText(full); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            return "错误：读取失败 " + ex.Message;
        }
        if (original.IndexOf('\0') >= 0) return "错误：这是二进制文件，无法编辑 " + path;

        // 先按原样匹配；不中且 old_string 用 \n 而文件是 Windows \r\n 时，再试转换后的形式
        var candidates = new[] { oldStr };
        if (!original.Contains(oldStr, StringComparison.Ordinal) && oldStr.Contains('\n'))
            candidates = new[] { oldStr.Replace("\n", "\r\n") };

        string? hit = null;
        foreach (var c in candidates)
        {
            var count = CountOccurrences(original, c);
            if (count == 1) { hit = c; break; }
            if (count > 1) return "错误：old_string 在文件中出现 " + count + " 次，请提供更多上下文使其唯一";
        }
        if (hit == null)
            return "错误：未找到要替换的内容。old_string 必须与文件内容逐字符一致（包括空格、缩进和换行）；先用 read_file 查看准确内容再试";

        var updated = original.Replace(hit, newStr);
        try { File.WriteAllText(full, updated); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            return "错误：写入失败 " + ex.Message;
        }
        return "已编辑 " + full + "（替换 " + hit.Length + " 字符为 " + newStr.Length + " 字符）";
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    private const int SearchContentMaxFileBytes = 2_000_000; // 超过此大小的文件跳过（防大文件拖慢/撑爆结果）
    private const int SearchContentMaxLinesPerFile = 20_000;  // 单文件最多扫描行数

    /// <summary>按正则搜索文件内容（只读），返回 路径:行号:行内容。大小写不敏感。</summary>
    private static string SearchContent(string pattern, string rootDir, int maxResults, AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "错误：pattern 不能为空";
        System.Text.RegularExpressions.Regex rx;
        try { rx = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant); }
        catch (Exception ex) { return "错误：正则无效 " + ex.Message; }
        var root = ResolvePath(OrDefault(rootDir, "."), cfg);
        if (!Directory.Exists(root)) return "错误：目录不存在 " + rootDir;
        if (maxResults <= 0) maxResults = 50;

        var matches = new List<string>();
        var visited = 0;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0 && matches.Count < maxResults && visited < MaxSearchVisited)
        {
            var (dir, depth) = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(dir); } catch { continue; }
            foreach (var f in files)
            {
                if (matches.Count >= maxResults) break;
                visited++;
                MatchFileContent(f, rx, matches, maxResults);
            }
            if (depth >= SearchMaxDepth) continue;
            string[] dirs;
            try { dirs = Directory.GetDirectories(dir); } catch { continue; }
            foreach (var d in dirs)
            {
                var n = Path.GetFileName(d);
                if (n.StartsWith("$", StringComparison.Ordinal) || n == "node_modules") continue;
                stack.Push((d, depth + 1));
            }
        }
        if (matches.Count == 0) return "（未找到包含「" + pattern + "」的内容）";
        return string.Join("\n", matches.Take(maxResults)) +
               (visited >= MaxSearchVisited ? "\n（扫描范围过大已提前停止）" : "");
    }

    private static void MatchFileContent(string file, System.Text.RegularExpressions.Regex rx, List<string> matches, int maxResults)
    {
        try
        {
            var len = new FileInfo(file).Length;
            if (len == 0 || len > SearchContentMaxFileBytes) return;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var head = new byte[4096];
            var n = fs.Read(head, 0, head.Length);
            if (head.Take(n).Contains((byte)0)) return; // 二进制跳过
            fs.Seek(0, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            string? line;
            var no = 0;
            while ((line = sr.ReadLine()) != null && no < SearchContentMaxLinesPerFile)
            {
                no++;
                if (rx.IsMatch(line))
                {
                    matches.Add(file + ":" + no + ": " + Truncate(line.Trim(), 160));
                    if (matches.Count >= maxResults) return;
                }
            }
        }
        catch { /* 单个文件读不了就跳过 */ }
    }

    // 浏览器样请求头：不少站点反爬见 UA 缺失/异常直接回「版本太低」拦截页；Cookie 容器处理首次响应种 cookie 放行
    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() })
        { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        return client;
    });

    /// <summary>读取响应正文（带上限，防超大页面吃内存；内容最终会被截到 6000 字）。</summary>
    private static async Task<string> ReadCappedAsync(HttpContent content, int maxBytes)
    {
        using var stream = await content.ReadAsStreamAsync();
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        long total = 0;
        int n;
        while ((n = await stream.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            if (total + n > maxBytes) { ms.Write(buf, 0, (int)(maxBytes - total)); break; }
            ms.Write(buf, 0, n);
            total += n;
        }
        System.Text.Encoding enc = System.Text.Encoding.UTF8;
        try
        {
            var cs = content.Headers.ContentType?.CharSet;
            if (!string.IsNullOrWhiteSpace(cs)) enc = System.Text.Encoding.GetEncoding(cs);
        }
        catch { /* 未注册的字符集（如 GBK）回退 UTF-8 */ }
        return enc.GetString(ms.ToArray());
    }

    /// <summary>抓取网页正文转纯文本（仅 http/https，拒绝内网/本地地址防 SSRF）。</summary>
    private static async Task<string> WebFetch(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "错误：仅支持 http/https 的完整 URL";
        if (!IsPublicHost(uri)) return "错误：拒绝访问内网/本地地址 " + uri.Host;
        try
        {
            using var resp = await Http.Value.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return "错误：HTTP " + (int)resp.StatusCode + "（" + uri.ToString() + "）";
            var text = await ReadCappedAsync(resp.Content, 2 * 1024 * 1024); // 最多读 2MB
            var isHtml = (resp.Content.Headers.ContentType?.MediaType ?? "").Contains("html", StringComparison.OrdinalIgnoreCase);
            if (isHtml) text = HtmlToText(text);
            else
            {
                // 非 HTML：压缩连续空白行
                text = System.Text.RegularExpressions.Regex.Replace(text, "[ \t]+", " ");
                text = string.Join("\n", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            }
            var t = (text ?? "").Trim();
            if (t.Length == 0) return "（页面没有正文内容）";
            return Truncate(t, 6000);
        }
        catch (Exception ex)
        {
            return "错误：抓取失败 " + ex.Message;
        }
    }

    /// <summary>目标主机是否公网可达（拒绝 loopback / 私网 / 链路本地 / 无法解析的地址）。</summary>
    private static bool IsPublicHost(Uri uri)
    {
        var host = uri.Host;
        if (host.Length == 0) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)) return false;
        if (IPAddress.TryParse(host, out var literal)) return !IsBlockedIp(literal);
        IPAddress[] addrs;
        try { addrs = Dns.GetHostAddresses(host); } catch { return false; }
        foreach (var a in addrs)
            if (IsBlockedIp(a)) return false;
        return true;
    }

    private static bool IsBlockedIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        var raw = ip.GetAddressBytes();
        byte[]? v4 = null;
        if (raw.Length == 4) v4 = raw;
        else if (raw.Length == 16 && IsV4Mapped(raw)) v4 = new[] { raw[12], raw[13], raw[14], raw[15] };
        if (v4 != null)
        {
            var b = v4;
            if (b[0] == 10 || b[0] == 127) return true;
            if (b[0] == 169 && b[1] == 254) return true;                    // 链路本地
            if (b[0] == 192 && b[1] == 168) return true;                   // 内网
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;      // 内网
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;     // CGNAT
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true; // fe80::/10 链路本地
            if (b[0] == 0xFC || b[0] == 0xFD) return true;          // fc00::/7 唯一本地
        }
        return false;
    }

    /// <summary>是否 IPv4-mapped IPv6（::ffff:a.b.c.d）。</summary>
    private static bool IsV4Mapped(byte[] v6)
    {
        for (var i = 0; i < 10; i++)
            if (v6[i] != 0) return false;
        return v6[10] == 0xFF && v6[11] == 0xFF;
    }

    /// <summary>HTML → 纯文本：去 script/style、换行块级标签、剥标签、解码常见实体。</summary>
    private static string HtmlToText(string html)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(html, "(?is)<(script|style|head)[^>]*>.*?</\\1>", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, "(?s)<br[^>]*>|</p>|</div>|</tr>|</li>|</h[1-6]>", "\n");
        s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", " ");
        s = s.Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<")
             .Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'");
        var lines = s.Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\s{2,}", " ").Trim())
            .Where(l => l.Length > 0);
        return string.Join("\n", lines);
    }

    private static string RunPowerShellSync(string command, double timeoutSec, AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(command)) return "错误：command 不能为空";
        var (code, output, timedOut) = PowerShellRunner.Run(command, Math.Clamp(timeoutSec, 5, 300), ResolveWorkDir(cfg));
        return FormatPsResult(code, output, timedOut);
    }

    private static string FormatPsResult(int? code, string output, bool timedOut)
    {
        var sb = new StringBuilder();
        if (timedOut) sb.AppendLine("（超时被终止）");
        else sb.AppendLine("退出码 " + code);
        var outText = Truncate(output.Trim(), MaxResultChars);
        return sb.Append(outText.Length > 0 ? outText : "（无输出）").ToString();
    }

    private static string arg(JsonObject args, string key) => args[key]?.GetValue<string>()?.Trim() ?? "";
    private static string OrDefault(string v, string d) => string.IsNullOrWhiteSpace(v) ? d : v;

    private static bool GetBool(JsonObject args, string key)
    {
        try
        {
            if (args[key] is JsonValue v)
            {
                if (v.TryGetValue<bool>(out var b)) return b;
                if (v.TryGetValue<string>(out var s)) return bool.TryParse(s.Trim(), out var r) && r;
            }
        }
        catch { }
        return false;
    }

    public static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "\n…(已截断)";
    }
}

/// <summary>PowerShell 进程运行器（同步等待）。</summary>
public static class PowerShellRunner
{
    public static (int? Code, string Output, bool TimedOut) Run(string command, double timeoutSec, string workDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + ToEncodedCommand(command),
            WorkingDirectory = Directory.Exists(workDir) ? workDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var sbOut = new StringBuilder();
        var sbErr = new StringBuilder();
        using var proc = Process.Start(psi);
        if (proc == null) return (-1, "错误：无法启动 powershell.exe", false);
        var doneOut = new TaskCompletionSource();
        var doneErr = new TaskCompletionSource();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sbOut) sbOut.AppendLine(e.Data); else doneOut.TrySetResult(); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sbErr) sbErr.AppendLine(e.Data); else doneErr.TrySetResult(); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var timedOut = !proc.WaitForExit((int)Math.Ceiling(timeoutSec * 1000));
        if (timedOut)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }
        // 等管道读完（最多再等 3s）
        try
        {
            Task.WaitAll(new[] { doneOut.Task, doneErr.Task }, 3000);
        }
        catch { }

        string o, e;
        lock (sbOut) o = sbOut.ToString();
        lock (sbErr) e = sbErr.ToString();
        var output = string.IsNullOrWhiteSpace(e) ? o : (string.IsNullOrWhiteSpace(o) ? e : o + "\n[stderr]\n" + e);
        int? code = null;
        try { if (!timedOut && proc.HasExited) code = proc.ExitCode; } catch { }
        return (code, output, timedOut);
    }

    /// <summary>把命令编码为 -EncodedCommand 参数（Base64 UTF-16LE），彻底绕开命令行引号/反斜杠转义问题。
    /// 头部注入 $ProgressPreference='SilentlyContinue'：PS5.1 在 stdout 被重定向时，会把进度记录
    /// （如"正在准备首次使用模块"）序列化成 CLIXML 块混进 stdout，抑制掉即可根治。</summary>
    public static string ToEncodedCommand(string command) =>
        Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue'; " + command));
}

/// <summary>后台 PowerShell 任务管理：启动即返回 job id，跨对话存活，硬上限到点强杀。</summary>
public sealed class JobManager
{
    private sealed class JobState
    {
        public string Id = "";
        public DateTime StartedAt = DateTime.Now;
        public Process? Proc;
        public readonly object Lock = new();
        public StringBuilder Buf = new();
        public int? ExitCode;
        public bool TimedOut;
        public long ReadPos; // check_job 已读位置（增量返回）
    }

    private static readonly Dictionary<string, JobState> Jobs = new();
    private static readonly object Gate = new();
    private static int _seq;

    public static string Start(string command, AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(command)) return "错误：command 不能为空";
        var agent = cfg.Chat.Agent;
        lock (Gate)
        {
            var running = Jobs.Values.Count(j => j.Proc != null && !j.Proc.HasExited);
            if (running >= Math.Max(1, agent.MaxRunningJobs))
                return "错误：后台任务数已达上限（" + Math.Max(1, agent.MaxRunningJobs) + "），请先用 check_job 查询或等待完成";
        }

        var id = "job_" + Interlocked.Increment(ref _seq);
        var job = new JobState { Id = id };
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + PowerShellRunner.ToEncodedCommand(command),
            WorkingDirectory = Directory.Exists(AgentTools.ResolveWorkDir(cfg)) ? AgentTools.ResolveWorkDir(cfg) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        try
        {
            job.Proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return "错误：启动失败 " + ex.Message;
        }
        job.Proc!.OutputDataReceived += (_, e) => Append(job, e.Data);
        job.Proc.ErrorDataReceived += (_, e) => Append(job, "[stderr] " + e.Data);
        job.Proc.BeginOutputReadLine();
        job.Proc.BeginErrorReadLine();

        lock (Gate) Jobs[id] = job;
        Watchdog(job, Math.Max(1.0, agent.JobMaxMinutes));
        return id + " 已启动（预计超过 1 分钟的任务请用 check_job 查询进度）";
    }

    private static void Append(JobState job, string? line)
    {
        if (line == null) return;
        lock (job.Lock)
        {
            job.Buf.AppendLine(line);
            if (job.Buf.Length > AgentTools.JobBufferCap)
                job.Buf.Remove(0, job.Buf.Length / 2); // 丢最旧的一半，保留近期输出
        }
    }

    private static void Watchdog(JobState job, double maxMinutes)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(maxMinutes));
            var proc = job.Proc;
            if (proc == null) return;
            try
            {
                if (!proc.HasExited)
                {
                    lock (job.Lock) job.TimedOut = true;
                    proc.Kill(entireProcessTree: true);
                    Append(job, "（超过上限被强制终止）");
                }
            }
            catch { }
        });
    }

    public static string Check(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return "错误：job_id 不能为空";
        JobState? job;
        lock (Gate)
        {
            if (!Jobs.TryGetValue(jobId.Trim(), out job))
                return "错误：没有这个任务 " + jobId + ActiveSummaryInternal();
        }

        var proc = job.Proc;
        bool exited = proc == null || proc.HasExited;
        string output;
        lock (job.Lock)
        {
            if (exited && job.ExitCode == null)
                job.ExitCode = SafeExitCode(proc);
            // 增量输出：上次 check 之后新增的部分；首次返回全部（截断）
            var start = (int)Math.Min(job.ReadPos, job.Buf.Length);
            output = job.Buf.ToString(start, job.Buf.Length - start);
            job.ReadPos = job.Buf.Length;
            if (exited) job.ReadPos = 0; // 完成后下次 check 重放完整输出
        }
        if (string.IsNullOrWhiteSpace(output)) output = "（暂无新输出）";

        var elapsed = (int)(DateTime.Now - job.StartedAt).TotalSeconds;
        if (!exited)
            return job.Id + " 运行中(" + FormatElapsed(elapsed) + ")，最新输出：\n" + AgentTools.Truncate(output, 1500);
        var head = job.TimedOut ? "（超时被终止）" : "已完成，退出码 " + job.ExitCode;
        return job.Id + " " + head + "，输出：\n" + AgentTools.Truncate(output, 1500);
    }

    private static int? SafeExitCode(Process? proc)
    {
        try { return proc?.HasExited == true ? proc.ExitCode : null; } catch { return null; }
    }

    /// <summary>活跃任务状态摘要（注入系统提示词用）；无任务返回 null。</summary>
    public static string? ActiveSummary()
    {
        var s = ActiveSummaryInternal();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static string ActiveSummaryInternal()
    {
        var lines = new List<string>();
        lock (Gate)
        {
            foreach (var j in Jobs.Values)
            {
                var proc = j.Proc;
                if (proc == null || proc.HasExited) continue;
                var sec = (int)(DateTime.Now - j.StartedAt).TotalSeconds;
                lines.Add(j.Id + " running (" + FormatElapsed(sec) + ")");
            }
        }
        return lines.Count > 0 ? "[ACTIVE JOBS] " + string.Join("; ", lines) : "";
    }

    /// <summary>清理长时间结束的任务，防止字典无限增长。</summary>
    public static void Prune()
    {
        lock (Gate)
        {
            var done = Jobs.Where(kv => kv.Value.Proc == null || kv.Value.Proc.HasExited)
                           .OrderBy(kv => kv.Value.StartedAt)
                           .Take(Math.Max(0, Jobs.Count - AgentTools.JobHistoryCap))
                           .Select(kv => kv.Key)
                           .ToList();
            foreach (var k in done) Jobs.Remove(k);
        }
    }

    private static string FormatElapsed(int sec)
    {
        if (sec < 60) return sec + "s";
        if (sec < 3600) return (sec / 60) + "m" + (sec % 60) + "s";
        return (sec / 3600) + "h" + (sec % 3600 / 60) + "m";
    }
}
