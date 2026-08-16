# desktop-pet-ui — 静态图片桌面宠物

一个纯静态图片驱动的 Windows 桌面宠物。背景完全透明，透明区域鼠标可穿透到桌面，
非透明区域可交互（拖动、点击角色）。内置对话 / 语音 / 主动搭话功能。

- **不依赖 WebView2**，无需额外运行时。
- **不启动 HTTP 服务**，不接受任何外部请求，所有交互都在进程内完成。
- 角色图片按情绪放在 `character/` 目录，托盘菜单可随时切换角色。
- 仓库只附带一个角色「鲸鱼娘」，更多角色按同结构放入 `character/` 即可。

## 技术栈（追求运行时体积/内存最轻）

| 层 | 选型 | 说明 |
|---|---|---|
| 宿主 | C# WPF (.NET 8) 文件夹发布 | 无 WebView2 / 无 HTTP 服务 |
| 渲染 | WPF `Image` + 预计算 alpha 掩码 | 情绪同名 PNG，点击穿透按像素 alpha 采样 |
| 音频 | `System.Media.SoundPlayer`（系统原生） | 播放 TTS 返回的 wav |
| 点击穿透 | 全局 `WH_MOUSE_LL` 钩子 + 像素 alpha 采样 + `WS_EX_TRANSPARENT` 切换 | 透明处点击落到桌面/后窗 |

## 目录结构

```
desktop-pet-ui/
  app/            C# 宿主（Windows 构建）
  character/      角色图片（情绪同名 png；子目录=另一个角色）
```

## 构建与运行（Windows）

前置：安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。
运行机需装 .NET 8 Desktop Runtime。

```powershell
# 发布（x64，framework-dependent）
dotnet publish app/desktop-pet-ui.csproj -c Release -r win-x64 --self-contained false -o out

# 运行
out\desktop-pet-ui.exe

# 首次启动生成 out\config.json；运行日志写到 out\pet.log
```

## 配置 `config.json`（首次运行自动生成）

```json
{
  "topmost": true,             // 是否置顶
  "clickThroughAuto": true,    // 是否启用透明处自动穿透
  "x": null, "y": null,        // 桌面坐标，null=居中（退出时自动保存）
  "alphaThreshold": 20.0,      // 判定“不透明”的 alpha 阈值(0-255)
  "sampleThrottleMs": 24,      // 像素采样节流

  "character": {
    "dir": "character",        // 角色根目录（相对 exe）
    "current": "鲸鱼娘",         // 当前角色名（character/ 下的子文件夹名）
    "scale": 1.0,              // 角色显示缩放
    "idleEmotion": "idle",     // 空闲默认状态图片名
    "idleIntervalSec": 6.0,    // 空闲时每隔几秒随机换一张 idle 图（<=0 关闭）
    "bubbleReserve": 140,      // 窗口顶部为气泡预留的透明高度(px)
    "crossFade": false,        // 图片切换是否用淡入淡出过渡（默认关，直接切换）
    "width": 512, "height": 720
  },

  "chat": {
    "enabled": true,               // 对话功能总开关（热键/点击角色弹框/语音气泡）
    "provider": "openai",          // 接口格式：openai（OpenAI 兼容，默认）
    "apiKey": "",                  // API Key（设置页掩码显示；本地服务可不填）
    "apiBaseUrl": "",              // OpenAI 兼容地址：可填 deepseek/openai/siliconflow/自建服务；留空=用 llama.url
    "apiModel": "",                // 模型名（留空=用 llama.model；设置页可「获取模型」下拉选择）
    "providerExtraParams": {       // 各接口格式的高级参数（JSON，随请求合并进 body）
      "openai": "{\"thinking\":{\"type\":\"disabled\"}}",
      "anthropic": "{\"thinking\":{\"type\":\"disabled\"}}"
    },
    "proxy": { "mode": "system", "address": "" },  // 网络代理：system(系统代理) | none(不使用) | custom(自定义)
    "llama": {                     // llama.cpp OpenAI 兼容地址（本地 provider 的默认地址/模型）
      "url": "http://127.0.0.1:8080",
      "model": "local",
      "temperature": 0.7,
      "maxTokens": 512,
      "systemPrompt": "你是一个住在用户桌面上的陪伴型聊天助手，温柔体贴，像朋友一样关心用户，让人感到安心。请始终使用简体中文回复，语气自然亲切，可以活泼、调侃或偶尔撒娇，但不要油腻。回复要简短（2句以内），像日常聊天一样自然，不要长篇大论，不要重复用户的话。回复结尾请附上1个情感标签，具体可选标签见本系统提示末尾的【情感标签】一节。"
    },
    "tts": {                       // 语音合成
      "provider": "gptsovits",     // gptsovits（本地服务） | windows（Windows 自带语音）
      "url": "http://127.0.0.1:9880",   // 仅 gptsovits 使用
      "voiceId": null,             // gptsovits=音色ID(null=当前激活)；windows=已安装语音名(null=默认)
      "textLang": "auto",          // auto / zh / en / ja（仅 gptsovits）
      "emotion": "neutral",        // neutral/happy/sad/angry/surprised/afraid/shy/confused
      "speedFactor": 1.0,          // windows 引擎会换算成语速(-10~10)
      "streaming": false           // 流式 TTS（仅 gptsovits；全局设置页可勾选，默认关）
    },
    "hotkey": { "modifiers": "Ctrl|Alt", "key": "Space" },
    "ui": {
      "popupFollowsPet": true,     // 输入弹框贴宠物窗口；false=跟随光标
      "alwaysOnTop": true,
      "width": 420, "height": 180,
      "maxBubbleChars": 120        // 气泡文字截断长度
    },
    "contextLength": 20,           // 上下文长度：保留的最大历史消息条数（>0）
    "proactive": false,            // 主动搭话开关（默认关）
    "proactiveIntervalSec": 30.0,  // 主动搭话间隔（秒）
    "screenAware": false,          // 观察屏幕开关：截取鼠标所在屏幕发给 llama 多模态识别
    "screenAwareChance": 0.3,      // 每次空闲定时器触发时去观察屏幕的概率(0~1)
    "userAddress": ""              // 默认称呼（角色未设置时生效，如「主人」「亲爱的」）
  }
}
```

## 对话功能（内置人机接口）

零 NuGet 依赖的精简对话编排，把「文本 → LLM → TTS → 角色演出」串成一条链路：

```
全局热键 或 点击角色 → 弹出输入框 → 回车提交
  → POST LLM /v1/chat/completions        （OpenAI 兼容，地址可配）
  → POST 本地TTS /tts                     （GPT-SoVITS，返回 wav；可流式）
  → 进程内 SpeakAsync：
      切换情绪同名图片 + 头顶气泡 + System.Media.SoundPlayer 播放
```

- **触发方式**：全局热键（`chat.hotkey`，`RegisterHotKey`）或点击角色。托盘菜单「对话」。
- **输入框**：无边框置顶小窗，Enter 提交、Esc 关闭，默认贴在宠物窗口上方（可改跟随光标）。
- **会话历史**：内存保留多轮上下文。系统提示词优先取当前角色 `character.json` 的
  `llm.systemPrompt`，未配置才用全局 `chat.llama.systemPrompt`（temperature / maxTokens 同理）。
- **上下文长度与压缩**：`chat.contextLength` 控制保留的最大历史条数，超限时把最旧的一半
  交给 LLM 压缩成摘要随请求携带，既控 token 又保留长期记忆。
- **主动搭话**：`chat.proactive=true` 时每 `proactiveIntervalSec` 秒（宠物可见、空闲、输入框
  未打开时）主动搭话一次，走完整 TTS + 语音 + 气泡链路，并计入历史。
- **观察桌面（多模态）**：`chat.screenAware=true` 时（需 LLM 加载 **vision 模型**）按概率截取
  鼠标所在屏幕，经图片消息发给 LLM 识别成文字，随之后对话作为上下文携带。
- **时间上下文**：每次请求的系统提示始终附带当前日期/时间，角色能感知时间。
- **持久化记忆**：切换角色与退出时把「摘要 + 历史」保存到 `character/<角色名>/memory.json`。
- **情感联动**：情绪来源是**角色文件夹的子文件夹**（`character/<角色>/<情绪>/`）。管线解析
  模型回复末尾的情感标签后切换对应情绪图片；TTS 不支持的平台或音色时回退 `neutral`。
- **流式 TTS**：勾选全局设置「TTS 流式输出」后（仅 gptsovits），按句子分段合成、逐段排队播放。
- **并发**：管线互斥锁串行，回复播放期间再提交会被忽略。

## PNG 角色模式

- **目录结构**：每个角色一个子文件夹，`character/<角色名>/`，其中**每个形态（含 idle）一个
  子文件夹**，文件夹里**所有 PNG 都是该形态的随机选项**：
  ```
  character/鲸鱼娘/
    character.json        # 角色 LLM/TTS 参数（可选）
    idle/                 # 默认状态，文件夹里所有 png 随机挑一张
      neutral.png
      ...
    happy/   neutral/  sad/  angry/  surprised/  afraid/  shy/  confused/
  ```
  某形态缺失时回退到 `idle/`；也兼容单文件 `character/<角色名>/<形态>.png`。
- **随机形态**：每次进入该形态随机选一张；点击角色随机换一张 idle 图，空闲时按
  `idleIntervalSec` 自动再换。
- **鼠标反馈**：鼠标悬停在角色不透明像素上时指针变为手型，移出恢复箭头。
- **切换角色**：托盘菜单「切换角色」，或设置窗口「设为当前角色」。切换后角色人设随之生效。
- **角色专属 LLM 设置**：每个角色文件夹的 `character.json` 可单独配置：
  ```json
  {
    "name": "鲸鱼娘",
    "llm": {
      "systemPrompt": "你是一个住在用户桌面上的陪伴型聊天助手……（结尾附情感标签）",
      "temperature": 0.7,
      "maxTokens": 512
    },
    "tts": {                    // 角色专属 TTS（留空字段=继承全局 chat.tts）
      "provider": null,         // none | gptsovits | windows（留空继承；默认 none=不语音）
      "url": null,
      "voiceId": null,
      "textLang": null,         // auto / zh / en / ja
      "emotion": null,
      "speedFactor": null,
      "streaming": null
    },
    "proactiveTemperature": 0.9,  // 主动搭话专用 Temperature（留空=继承 llm.temperature）
    "userAddress": "主人"          // 对用户的称呼（留空=继承全局 chat.userAddress）
  }
  ```
  设置窗口的角色页可编辑这些字段，保存到各自文件夹，并可一键「设为当前角色」。
  角色未配置 TTS 时默认**不调用语音**（`provider=none`），只显示气泡。
- **交互**：拖拽角色移动窗口；单击角色（无拖动）随机换一张 idle 图并弹出对话输入框；
  透明像素点击穿透到桌面。
- **语音**：`System.Media.SoundPlayer` 播放 TTS 返回的 wav。

## 点击穿透原理

1. 全局 `WH_MOUSE_LL` 钩子跟踪光标（窗口处于穿透态时收不到 WM_MOUSEMOVE，必须用钩子）。
2. 光标进入窗口矩形时，宿主按节流频率采样该点 alpha（读当前角色图片预计算的 alpha 掩码）。
3. alpha < 阈值 → 对窗口句柄置 `WS_EX_TRANSPARENT`（点击穿透到桌面/后窗）；否则清除（正常交互）。
4. 窗口加 `WS_EX_NOACTIVATE`，不抢焦点；拖动由鼠标事件驱动，宿主按增量移动窗口。

## 故障排查

- **启动异常 / 无响应**：先看 exe 旁 `pet.log`，各步骤都有记录。
- **点击到角色却不响应**：检查 `clickThroughAuto` / `alphaThreshold` 是否配置合理；
  或角色图透明区域过大导致判定为“透明”。
- **TTS 无声**：确认 gptsovits 地址可达、音色已激活；`windows` 引擎需系统装有中文语音。