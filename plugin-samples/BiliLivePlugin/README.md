# BiliLive 插件

接入 B 站直播间，让桌宠以角色口吻回应弹幕 / 礼物 / 醒目留言（SC）/ 互动事件。直连官方 WebSocket 协议（comet），无需第三方 token 或网页代理。

## 工作原理

```
房间号 ──► getDanmuInfo（WBI 签名 + Cookie）──► token + 6 条 wss 线路
                                                │
        断线退避重连 ◄── 看门狗(90s无帧) ──┐      ▼
        多线路回退       心跳(30s)     ◄──┴─ wss://host:2245/sub（brotli + JSON）
                                                │
                     弹幕 / 礼物 / SC / 互动事件
                                                │
                     FIFO 队列 → 合并窗口 → 最小间隔节流
                                                │
                                     ctx.SendEventAsync()
                                                ▼
                                  Pet 聊天管线（事件以叙述者身份进入上下文）
```

- **协议**：2026 comet 帧格式 `[len u32 BE][hdrLen=16][proto][op][flag] + body`，proto=3 为 brotli 压缩（递归解包），心跳 op=2 / 应答 op=3。
- **登录态**：弹幕服务要求登录，Cookie 至少含 `SESSDATA`；`DedeUserID`/`buvid3` 缺失时插件自动从 nav/spi 接口补取并缓存。
- **昵称**：登录后弹幕带完整昵称 + uid（匿名只有打码名，uid=0）。

## 事件类型与消息格式

| 事件 | WS cmd | 发给 Pet 的文本 |
|---|---|---|
| 弹幕 | `DANMU_MSG` | `【直播间】弹幕：观众「昵称」说「内容」`（多条合并时编号列表） |
| 礼物 | `SEND_GIFT` | `【直播间】礼物：观众「昵称」送出 礼物名 xN（价值¥N）` |
| 醒目留言 | `SUPER_CHAT_MESSAGE` | `【直播间】醒目留言：观众「昵称」留言「内容」（¥N）` |
| 互动 | `INTERACT_WORD_V2` | `【直播间】互动：观众「昵称」关注了主播 / 特别关注了主播 / 和主播互粉了 / 分享了直播间` |

说明：

- **进场事件**（`msg_type=1`）不响应——热门房间进场太频繁，会刷屏。
- `INTERACT_WORD_V2` 是互动事件（关注/分享等），不是弹幕；弹幕只走 `DANMU_MSG`。
- 所有消息都带全角 `【直播间】` 前缀 + 「观众」称谓，配合 system prompt 里的 LIVE ROOM MODE 规则，明确告诉模型这是第三方观众的发言、不是用户本人输入。
- **事件身份**：经 `ctx.SendEventAsync(text, allowAgent: false)` 注入——历史记 `Role="event"`，对模型呈现为 `system`（叙述者）而非 `user`；聊天窗用独立紧凑行（» 前缀小字），与用户蓝气泡/角色灰气泡区分。
- **安全边界**：`allowAgent=false` 使观众事件轮次不启用 agent 工具链——即使全局开了 Agent，弹幕里的"把电脑关了/删文件"也只能被当作对话内容回应或跳过，无法触发真实操作。

### 跳过不想回应的弹幕（[SKIP] 协议）

插件向 system prompt 尾部注入 `LIVE ROOM MODE` 片段（连接中才注入），告知模型：遇到刷屏广告、跑题、已回应过等不想回应的事件时，最终回答只输出 `[SKIP]`。宿主消息链解析到恰好为 `[SKIP]` 的最终回答即静默——不朗读、不出气泡，历史只留一条紧凑的「本轮未回应」标记。**与 Agent 开关无关**（本插件不注册工具：观众事件以 `allowAgent=false` 发送，工具链不会启用，跳过只能靠提示词约定 + 消息链解析）。

## 设定项

| 名称 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `roomCode` | string | 空 | 直播间号（纯数字；留空=不连接） |
| `cookie` | string | 空 | B 站 Cookie（必填，登录态）。浏览器 F12 → Network → 任意 live.bilibili.com 请求的完整 Cookie 头 |
| `respondDanmaku` | bool | true | 回应聊天弹幕 |
| `respondGift` | bool | true | 回应礼物 |
| `respondSc` | bool | true | 回应醒目留言（SC） |
| `respondInteract` | bool | true | 回应互动事件（关注/特别关注/互粉/分享；进场不响应） |
| `minIntervalMs` | int | 2000 | 两次回应的最小间隔毫秒（0=不限；防突发刷屏） |
| `mergeWindowMs` | int | 1500 | 弹幕合并窗口毫秒：窗口内多条弹幕合成一条只回应一次（0=严格逐条） |
| `maxQueue` | int | 32 | FIFO 队列上限（满时丢弃新事件，保队首优先） |
| `minGiftPrice` | double | 0 | 触发回应的最低礼物价格（元；0=全部回应） |
| `minScPrice` | double | 0 | 触发回应的最低 SC 价格（元；0=全部回应） |
| `blockKeywords` | string | 空 | 屏蔽关键词：昵称或内容含任一则不回应（逗号/换行分隔） |
| `blockUsers` | string | 空 | 屏蔽用户 mid 列表（逗号/空格分隔；该用户的弹幕/礼物/SC 一律不回应） |

所有设定在设置界面保存后即时生效，无需重启（改房间号/Cookie 会自动重连）。

### 防刷屏策略

1. **合并窗口**：`mergeWindowMs` 内的多条弹幕合成一条消息（最多 10 条/批），只占用一次回应。
2. **最小间隔**：两次 `SendEventAsync` 之间至少间隔 `minIntervalMs`，期间新事件继续排队。
3. **队列上限**：`maxQueue` 满时丢弃最新事件（保队首优先），防止突发刷屏拖垮聊天管线。
4. **价格阈值**：低价礼物/SC 直接过滤。

## 部署

```bash
dotnet build -c Release
# 复制产物到桌宠运行目录的 plugins/ 下
cp bin/Release/net8.0/BiliLivePlugin.dll <desktop-pet-ui 运行目录>/plugins/
```

重启桌宠（或重新加载插件）后，在设置 → 插件 → BiliLive 中填写直播间号与 Cookie 即可。

## 文件结构

| 文件 | 职责 |
|---|---|
| `BiliLivePlugin.cs` | `IPlugin` 实现：注册/设定、FIFO 分发器（合并窗口 + 最小间隔节流） |
| `BiliWsClient.cs` | comet WebSocket 客户端：房间解析、身份补取、token 获取、多线路连接、认证/心跳/看门狗、brotli+JSON 收包、事件解析（含 InteractWord protobuf 迷你解析器） |
| `WbiSigner.cs` | WBI 签名（img_key/sub_key + mixin key 缓存 1h）与 `getDanmuInfo` |
| `LiveEvent.cs` | 事件模型、过滤规则（类型开关/屏蔽名单/价格阈值/关键词）、消息格式化 |

## 已知限制

- 弹幕昵称依赖登录态；匿名连接只能拿到打码名。
- 未开播的房间不建连，插件会周期性复查，开播后自动接入。
- `INTERACT_WORD_V2` 的 `Link`（msg_type=6，自定义文案）类型暂未解析。
