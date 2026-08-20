using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DesktopPetUi.Core.Agent;

/// <summary>一次 agent 工具调用的审计记录（持久化，用于追踪与回放）。</summary>
public sealed class AgentOpRecord
{
    public DateTime Ts { get; set; } = DateTime.Now;
    /// <summary>工具名（delete_file / run_powershell ...）。</summary>
    public string Tool { get; set; } = "";
    /// <summary>动作短标题（如「删除（不可恢复）」）。</summary>
    public string Title { get; set; } = "";
    /// <summary>操作详情全文（完整命令 / 路径，不截断）。</summary>
    public string Detail { get; set; } = "";
    /// <summary>裁定：auto=自动放行 allowed=用户允许 denied=用户拒绝。</summary>
    public string Verdict { get; set; } = "auto";
    /// <summary>补充说明（如「信任目录」「并信任该目录」）。</summary>
    public string Note { get; set; } = "";
}

/// <summary>
/// Agent 操作日志：按角色持久化到 character/&lt;名&gt;/agent_ops.json（追加式审计轨迹，上限 MaxEntries 条）。
/// 与 memory.json 分离——操作记录只供人追踪，永不进入 LLM 上下文。
/// </summary>
public static class AgentOpLog
{
    private const int MaxEntries = 1000;
    private static readonly object _ioLock = new();

    public static string PathFor(AppConfig cfg) =>
        Path.Combine(cfg.CharacterDir,
            string.IsNullOrWhiteSpace(cfg.Character.Current)
                ? "agent_ops.json"
                : Path.Combine(cfg.Character.Current, "agent_ops.json"));

    public static List<AgentOpRecord> Load(AppConfig cfg)
    {
        lock (_ioLock)
        {
            try
            {
                var path = PathFor(cfg);
                if (!File.Exists(path)) return new();
                return JsonSerializer.Deserialize<List<AgentOpRecord>>(File.ReadAllText(path)) ?? new();
            }
            catch (Exception ex)
            {
                Log.Error("AgentOpLog.Load failed", ex);
                return new();
            }
        }
    }

    public static void Append(AppConfig cfg, AgentOpRecord rec)
    {
        lock (_ioLock)
        {
            try
            {
                var path = PathFor(cfg);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var list = File.Exists(path)
                    ? (JsonSerializer.Deserialize<List<AgentOpRecord>>(File.ReadAllText(path)) ?? new())
                    : new List<AgentOpRecord>();
                list.Add(rec);
                if (list.Count > MaxEntries) list.RemoveRange(0, list.Count - MaxEntries);
                File.WriteAllText(path, JsonSerializer.Serialize(list));
            }
            catch (Exception ex)
            {
                Log.Error("AgentOpLog.Append failed", ex);
            }
        }
    }

    public static void Clear(AppConfig cfg)
    {
        lock (_ioLock)
        {
            try
            {
                var path = PathFor(cfg);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Error("AgentOpLog.Clear failed", ex);
            }
        }
    }
}
