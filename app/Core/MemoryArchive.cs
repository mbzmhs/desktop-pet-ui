using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DesktopPetUi.Core;

/// <summary>
/// 聊天记录归档：每次记忆压缩（自动/一键）时，被摘要替代的原始记录追加到 character/&lt;名&gt;/memory_archive.json。
/// 记忆管理器「归档记录」tab 只读展示；上限取全局设置 ArchiveMaxEntries（0=无上限），超出丢最旧。
/// </summary>
public static class MemoryArchive
{
    private static readonly object _ioLock = new();

    public static string PathFor(AppConfig cfg) =>
        Path.Combine(cfg.CharacterDir,
            string.IsNullOrWhiteSpace(cfg.Character.Current)
                ? "memory_archive.json"
                : Path.Combine(cfg.Character.Current, "memory_archive.json"));

    public static List<ChatMessage> Load(AppConfig cfg)
    {
        lock (_ioLock)
        {
            try
            {
                var path = PathFor(cfg);
                if (!File.Exists(path)) return new();
                return JsonSerializer.Deserialize<List<ChatMessage>>(File.ReadAllText(path)) ?? new();
            }
            catch (Exception ex)
            {
                Log.Error("MemoryArchive.Load failed", ex);
                return new();
            }
        }
    }

    public static void Append(AppConfig cfg, IEnumerable<ChatMessage> chunk)
    {
        if (chunk == null) return;
        lock (_ioLock)
        {
            try
            {
                var nonEmpty = new List<ChatMessage>();
                foreach (var m in chunk)
                    if (m != null && !string.IsNullOrWhiteSpace(m.Content))
                        nonEmpty.Add(new ChatMessage { Role = m.Role, Content = m.Content, Timestamp = m.Timestamp });
                if (nonEmpty.Count == 0) return;

                var path = PathFor(cfg);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var all = File.Exists(path)
                    ? (JsonSerializer.Deserialize<List<ChatMessage>>(File.ReadAllText(path)) ?? new())
                    : new List<ChatMessage>();
                all.AddRange(nonEmpty);
                var max = cfg.Chat.ArchiveMaxEntries; // 0=无上限
                if (max > 0 && all.Count > max) all.RemoveRange(0, all.Count - max);
                File.WriteAllText(path, JsonSerializer.Serialize(all));
            }
            catch (Exception ex)
            {
                Log.Error("MemoryArchive.Append failed", ex);
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
                Log.Error("MemoryArchive.Clear failed", ex);
            }
        }
    }
}
