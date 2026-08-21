# 桌宠插件开发手册

插件是放在程序目录 `plugins\` 下的 .NET 8 DLL。宿主启动时按**文件名顺序**扫描加载，每个插件可获得四类能力：

| 能力 | 说明 |
|---|---|
| 消息链 | LLM 回复流式结束以后、工具解析之前，逐插件传递文本（可改写/过滤/追加） |
| 工具路由 | 声明工具名后，模型 `[tool]` 调用命中即分发给你执行（直接执行，不弹权限确认；每次调用记入 `agent_ops.json`，verdict=plugin） |
| 代替用户发消息 | `ctx.SendChatAsync(text)` 走完整聊天管线（与用户打字同路径，消息以 user 身份进入上下文） |
| 注入第三方事件 | `ctx.SendEventAsync(text)` 同样走完整管线，但消息以"叙述者"身份进入上下文（对模型是 system 而非 user，历史 Role="event"，聊天窗独立紧凑行）——适合直播间弹幕等**不是用户本人说的话** |
| 设定持久化 | 声明设定项，宿主设置页「插件设置」Tab 渲染控件并写入 `plugin.json`；支持热启用/禁用 |

## 1. 工程搭建

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>MyPlugin</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <!-- 第三方开发：引用程序目录下的 API 副本（部署时自动更新） -->
    <Reference Include="C:\Users\admin\Desktop\desktop-pet-ui\plugins\api\DesktopPetUi.PluginApi.dll" />
    <!-- 仓库内开发：可改为 <ProjectReference Include="../../plugin-api/DesktopPetUi.PluginApi.csproj" /> -->
  </ItemGroup>
</Project>
```

- **不要引用 WPF/WinForms**。API 是纯 net8.0 classlib，插件跑在宿主进程里，触碰 UI 线程会崩溃。
- 类必须 `public`、有**无参公共构造函数**、实现 `IPlugin`。一个 dll 只认第一个命中的实现类型。

## 2. API 一览（DesktopPetUi.Plugins 命名空间）

```csharp
public interface IPlugin
{
    // 注册（宿主启动时调用，UI 线程）。settings=plugin.json 中本插件已持久化的设定（首次为空字典）。
    // 返回 null = 注册失败（宿主跳过并记日志）。
    PluginInfo? Register(IPluginContext ctx, IReadOnlyDictionary<string, JsonElement> settings);

    // 消息链：按 plugins 目录文件名顺序逐插件传递；直接原样返回=不修改。
    // 抛异常时宿主保留上一段文本继续传，不会中断链。
    string PreprocessReply(string reply, ReplyContext ctx);

    // 工具执行：[tool] 的 name 命中 PluginInfo.ToolNames 才分发到这里。
    // 返回文本作为 [result] 回喂模型（建议 ≤2000 字）。
    Task<string> ExecuteToolAsync(ToolCall call, CancellationToken ct);

    IReadOnlyList<SettingDef> GetSettings();           // 设定列表（宿主据此渲染控件）
    SettingResult UpdateSetting(string name, JsonElement value); // 校验+应用；不合规返回 Ok=false 并附用户可读错误
    void Shutdown();                                    // 禁用/退出时调用，做资源清理
}

public interface IPluginContext
{
    Task<bool> SendChatAsync(string text, CancellationToken ct);                    // 代替用户发消息（完整管线，消息以 user 身份进入上下文，agent 按全局开关）
    Task<bool> SendEventAsync(string text, bool allowAgent = false, CancellationToken ct = default); // 注入第三方事件（如直播间弹幕）：历史记 Role="event"，对模型呈现为 system 叙述者而非 user；聊天窗用独立紧凑行。allowAgent=false（默认）=本轮不启用 agent 工具链——第三方内容不可信，防注入电脑操作指令。文本建议自带醒目标记前缀并在 GetSystemPromptPart 里解释
    PetSnapshot GetPetInfo();                                    // 当前角色/情绪/缩放/窗口位置/功能开关
    void Log(string message);                                    // 写入宿主日志（带 [plugin:名] 前缀）
}

// 可选扩展：再实现这个接口，返回的文本会追加到 system prompt 尾部（活动插件按文件名顺序，空行分隔）。
// 每次 LLM 请求都会调用 → 必须轻量（只读状态拼字符串，不做 IO）；返回 null/空 = 本次不注入。
public interface ISystemPromptContributor
{
    string? GetSystemPromptPart();
}
```

- **自定义 system prompt 片段**（`ISystemPromptContributor`，可选）：用于告知宠物插件引入的新上下文——例如直播插件注入"你正在直播间，要回应弹幕、感谢礼物"。只在功能激活时返回非空（如连接中），停用即自动消失；抛异常只跳过该插件片段，不影响其他插件与对话。

- `PluginInfo`：Name（唯一标识，建议=dll 文件名）、Version、Author、Description、`Tools`（注入 systemPrompt 的工具定义）、`ToolNames`（路由清单）。
- `ReplyContext.Source`：`"agent-step"`（中间工具步，文本可能含 `[tool]...[/tool]`，**不要破坏协议格式**）/ `"final"` / `"proactive"`。
- `ToolCall.Args` 是 `JsonElement`（JSON object），用 `TryGetProperty` 取参。
- `SettingDef.Value`：当前值（设置页回显用），在 `GetSettings()` 时填入自身状态。

## 3. 线程模型与约定

- `Register` / `Shutdown` / 设置页操作：**UI 线程**，不要阻塞（别在里面做网络/IO）。
- `PreprocessReply` / `ExecuteToolAsync` / `SendChatAsync` / `SendEventAsync` 的回调：后台线程。
- 任何方法抛异常都会被宿主隔离并记日志，但请自己保证状态一致。
- `SendChatAsync` / `SendEventAsync` 都走完整聊天管线（串行门），**没有频率限制护栏**——插件自负行为，避免高频调用刷屏/烧 token。
- 工具直接执行、无权限确认：请保持只读或低风险；危险操作请自行向用户说明（可通过 `SendChatAsync` 告知）。
- **工具只在 agent 开启的轮次可用**：agent 全局关闭、或该轮 `allowAgent=false`（如观众事件）时，工具定义不会注入 systemPrompt——依赖工具的插件功能在这些轮次不生效。对不可信内容触发的轮次，行为约定应走"prompt 说明 + 消息链解析协议文本（如 `[SKIP]`）"，而不是工具。
- **消息链改过的文本会原样持久化进对话历史**，模型下一轮可能模仿/复读你追加的内容（如签名）。追加类改动务必做幂等检查：已含标记就不再追加。
- **`[SKIP]` 静默协议**：最终回答（消息链处理后的结果）恰为 `[SKIP]`（忽略大小写与首尾空白）时，宿主视为"本轮不回应"——不朗读、不出气泡，历史里只留一条紧凑的 `[system] 本轮未回应。` 标记（保持 user/assistant 交替）。典型用法：system prompt 片段告知模型何时该跳过（最终回答只输出 `[SKIP]`），消息链 `Source="final"` 处可顺带统计。**与 Agent 开关无关**——不需要工具即可生效；对 `allowAgent=false` 的事件轮次这是唯一的跳过手段。

## 4. 部署与生效时机

1. `dotnet build -c Release`，把 `bin\Release\net8.0\MyPlugin.dll` 复制到程序目录 `plugins\`。
2. **新 dll / 代码改动需要重启宿主**；启用/禁用、设定修改是热的（立即生效）。
3. 持久化：`plugin.json`（exe 目录）存 `{ "plugins": { "<dll名>": { "enabled": bool, "settings": {...} } } }`。
4. 消息链顺序 = `plugins\` 文件名排序；想调整顺序就改文件名前缀（如 `01_foo.dll`）。

## 5. 示例插件

仓库内 `plugin-samples/HelloPlugin/` 演示了全部能力：

```bash
cd plugin-samples/HelloPlugin
dotnet build -c Release
cp bin/Release/net8.0/HelloPlugin.dll ../../dist/plugins/   # 或部署目录 plugins\
```

- 工具 `pet_greet(text?)`：用当前角色口吻问候（读 `GetPetInfo()`）。
- 设定 `greeting`（字符串）/ `stamp`（布尔，最终回复末尾加签名）。
- 聊天里让角色「用 pet_greet 打个招呼」即可触发工具路由。

## 6. 常见问题

| 现象 | 原因/处理 |
|---|---|
| 日志「类型加载失败（可能 PluginApi 版本不匹配）」 | 插件引用的 PluginApi 与宿主不一致；重新用 `plugins\api\` 下的 dll 引用并构建 |
| 工具没被模型调用 | systemPrompt 只注入**启用中**插件的 Tools；检查设置页是否启用、Tools/ToolNames 是否配对 |
| 禁用后旧工具名仍被模型使用 | 历史消息里的示例会诱导模型；宿主已把未知工具名回喂为错误，模型会自行纠正 |
| `SendChatAsync` 返回 false | 聊天未启用或管线忙时排队超时前被取消；检查角色设置里聊天开关 |
