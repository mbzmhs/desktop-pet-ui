using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DesktopPetUi.Core.Agent;

/// <summary>一条 todo 事项（按角色持久化，agent 的 todo 工具维护，todo 窗口实时展示）。</summary>
public sealed class TodoItem
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

/// <summary>
/// Todo 列表：按角色持久化到 character/&lt;名&gt;/todo.json。
/// agent 通过 todo 工具增删改；TodoWindow 订阅 Changed 实时刷新（事件在锁外触发，UI 侧自行切线程）。
/// </summary>
public static class TodoStore
{
    public const int MaxItems = 50;
    private static readonly object _lock = new();

    /// <summary>任意变更后触发（可能在后台线程）。</summary>
    public static event Action? Changed;

    public static string PathFor(AppConfig cfg) =>
        Path.Combine(cfg.CharacterDir,
            string.IsNullOrWhiteSpace(cfg.Character.Current)
                ? "todo.json"
                : Path.Combine(cfg.Character.Current, "todo.json"));

    /// <summary>当前列表快照（副本）。</summary>
    public static List<TodoItem> Snapshot(AppConfig cfg)
    {
        lock (_lock)
            return LoadCore(cfg);
    }

    public static List<TodoItem> Add(AppConfig cfg, string text)
    {
        List<TodoItem> items;
        lock (_lock)
        {
            var list = LoadCore(cfg);
            if (list.Count >= MaxItems)
            {
                // 超限：先丢最旧已完成项，没有则丢最旧
                var idx = -1;
                for (int i = 0; i < list.Count; i++) if (list[i].Done) { idx = i; break; }
                if (idx < 0) idx = 0;
                list.RemoveAt(idx);
            }
            var id = 0;
            foreach (var it in list) if (it.Id > id) id = it.Id;
            list.Add(new TodoItem { Id = id + 1, Text = text.Trim() });
            SaveCore(cfg, list);
            items = new List<TodoItem>(list);
        }
        Changed?.Invoke();
        return items;
    }

    public static (bool Ok, string Error, List<TodoItem> Items) SetDone(AppConfig cfg, int id, bool done)
    {
        List<TodoItem> items;
        lock (_lock)
        {
            var list = LoadCore(cfg);
            var it = list.Find(i => i.Id == id);
            if (it == null)
            {
                items = new List<TodoItem>(list);
                return (false, "没有 id=" + id + " 的事项。当前列表：\n" + Render(items), items);
            }
            it.Done = done;
            SaveCore(cfg, list);
            items = new List<TodoItem>(list);
        }
        Changed?.Invoke();
        return (true, "", items);
    }

    public static (bool Ok, string Error, List<TodoItem> Items) Remove(AppConfig cfg, int id)
    {
        List<TodoItem> items;
        lock (_lock)
        {
            var list = LoadCore(cfg);
            var idx = list.FindIndex(i => i.Id == id);
            if (idx < 0)
            {
                items = new List<TodoItem>(list);
                return (false, "没有 id=" + id + " 的事项。当前列表：\n" + Render(items), items);
            }
            list.RemoveAt(idx);
            SaveCore(cfg, list);
            items = new List<TodoItem>(list);
        }
        Changed?.Invoke();
        return (true, "", items);
    }

    public static void Clear(AppConfig cfg)
    {
        lock (_lock)
        {
            try
            {
                var path = PathFor(cfg);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Error("TodoStore.Clear failed", ex);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>渲染给 LLM / 窗口：「1. [x] 文本」逐行。</summary>
    public static string Render(List<TodoItem> items)
    {
        if (items.Count == 0) return "(空)";
        var sb = new StringBuilder();
        foreach (var it in items)
            sb.Append(it.Id).Append(". ").Append(it.Done ? "[x] " : "[ ] ").Append(it.Text).Append('\n');
        return sb.ToString().TrimEnd();
    }

    /// <summary>注入 agent 系统提示词易变尾部的当前状态行；列表为空返回 ""。</summary>
    public static string SummaryLine(AppConfig cfg)
    {
        var items = Snapshot(cfg);
        if (items.Count == 0) return "";
        int done = 0;
        foreach (var it in items) if (it.Done) done++;
        return "[TODO LIST] " + done + "/" + items.Count + " done:\n" + Render(items);
    }

    private static List<TodoItem> LoadCore(AppConfig cfg)
    {
        try
        {
            var path = PathFor(cfg);
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<List<TodoItem>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("TodoStore.Load failed", ex);
            return new();
        }
    }

    private static void SaveCore(AppConfig cfg, List<TodoItem> items)
    {
        try
        {
            var path = PathFor(cfg);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // 原子写：tmp + rename，防异常退出留下半截文件
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("TodoStore.Save failed", ex);
        }
    }
}
