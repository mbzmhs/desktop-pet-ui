namespace DesktopPetUi.Plugins;

/// <summary>
/// 可选扩展接口：插件在实现 <see cref="IPlugin"/> 之外再实现本接口，其返回的文本会追加到 system prompt 尾部
/// （活动插件按文件名顺序排列，多个片段以空行分隔）。典型用途：依赖插件运行时的上下文说明——
/// 例如直播插件告知宠物"你正在直播间里，要和观众互动、感谢礼物"。
/// </summary>
public interface ISystemPromptContributor
{
    /// <summary>
    /// 追加到 system prompt 尾部的自定义提示片段。每次构建 system prompt（即每次 LLM 请求）都会调用，
    /// 必须轻量：只读自身状态拼字符串返回，不做 IO、不长持锁。返回 null/空 = 本次不注入（如功能未激活）。
    /// </summary>
    string? GetSystemPromptPart();
}
