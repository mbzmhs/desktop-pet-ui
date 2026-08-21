using System.Text.Json;

namespace DesktopPetUi.Plugins;

/// <summary>设定数值类型（宿主设置页据此渲染输入控件）。</summary>
public enum SettingType
{
    String, // 文本
    Int,    // 整数
    Double, // 小数
    Bool,   // 开关
    Json,   // 任意 JSON（多行文本编辑）
}

/// <summary>设定定义：名称 + 说明 + 数值类型。</summary>
public sealed class SettingDef
{
    /// <summary>设定名（插件内唯一；更新时只传它+值）。</summary>
    public required string Name { get; init; }

    /// <summary>说明（设置页展示给用户）。</summary>
    public required string Description { get; init; }

    /// <summary>数值类型。UpdateSetting 收到的 JsonElement 应符合此类型，否则插件应拒绝并给出明确错误。</summary>
    public required SettingType Type { get; init; }

    /// <summary>当前值（设置页初始回显用；插件在 GetSettings 时填入自身状态，可空=未设置）。</summary>
    public JsonElement? Value { get; init; }
}

/// <summary>设定更新结果：失败时 Error 必须是给用户看的明确错误说明（如"volume 必须是 0-100 的整数"）。</summary>
public sealed record SettingResult(bool Ok, string Error = "");
