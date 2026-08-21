using System.IO.Compression;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace BiliLivePlugin;

/// <summary>
/// B 站直播弹幕直连客户端（2026 comet 协议）：
/// 房间号 → get_info（内部 room id / 开播状态）→ WBI 签名 getDanmuInfo（token + 线路列表）
/// → wss://host:2245/sub 认证/心跳/接收循环 → 断线退避重连、多线路回退。
/// 帧格式：[总长 u32 BE][头长 u16=16][体协议 u16（0/1=JSON, 3=brotli）][op u32][标志 u32] + 体；
/// brotli 体内是拼接的内层包，递归解析。事件经 onEvent 按到达顺序（FIFO）回调。
/// </summary>
internal sealed class BiliWsClient
{
    private const int MaxPacketBytes = 4 * 1024 * 1024; // 单 WS 消息缓冲上限
    private const int HeartbeatIntervalMs = 30_000;     // 心跳周期（服务器 ttl=30s）
    private const int StallTimeoutMs = 90_000;          // 无任何下行帧视为连接失效

    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    static BiliWsClient()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(Ua);
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    private const int OpHeartbeat = 2;   // 客户端心跳
    private const int OpHbAck = 3;       // 心跳应答
    private const int OpPush = 5;        // 数据推送
    private const int OpAuth = 7;        // 认证
    private const int OpAuthAck = 8;     // 认证结果 {"code":0}
    private const int ProtoBrotli = 3;   // 体协议：brotli 压缩（内层为拼接包）

    private readonly Action<string> _log;
    private readonly WbiSigner _wbi;

    public BiliWsClient(Action<string> log)
    {
        _log = log;
        _wbi = new WbiSigner(log);
    }

    private string _cookie = "";
    private long _autoUid;          // nav 接口补取的 uid（Cookie 缺 DedeUserID 时）
    private string _autoBuvid3 = ""; // spi 接口补取的 buvid3（Cookie 缺时）

    /// <summary>用户提供的 Cookie（至少需含 SESSDATA；DedeUserID/buvid3 缺失时自动从接口补取）。</summary>
    public string Cookie
    {
        get => _cookie;
        set
        {
            _cookie = value ?? "";
            _autoUid = 0; // Cookie 变化后缓存失效
            _autoBuvid3 = "";
        }
    }

    /// <summary>解析直播间号（只接受纯数字）；失败返回 null。</summary>
    public static int? ParseRoomId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.Length == 0 || s.Length > 12 || !s.All(char.IsDigit)) return null;
        return int.TryParse(s, out var id) && id > 0 ? id : null;
    }

    /// <summary>带浏览器头的 GET 请求。</summary>
    internal static HttpRequestMessage MakeRequest(string url, int roomId = 0)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (roomId > 0) req.Headers.TryAddWithoutValidation("Referer", "https://live.bilibili.com/" + roomId);
        return req;
    }

    internal static void SetCookieHeader(HttpRequestMessage req, string cookie)
    {
        var c = SanitizeCookie(cookie);
        if (c.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", c);
    }

    /// <summary>
    /// Cookie 净化为纯 ASCII：.NET 的 WS 握手/部分 HTTP 头校验拒绝非 ASCII 字符，
    /// 而浏览器 Cookie 值可能含中文等。非 ASCII 字符按 UTF-8 百分号编码（结构不变）。
    /// </summary>
    internal static string SanitizeCookie(string cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return "";
        var sb = new StringBuilder(cookie.Length + 16);
        foreach (var c in cookie.Trim())
        {
            if (c <= 127) { sb.Append(c); continue; }
            foreach (var b in Encoding.UTF8.GetBytes(c.ToString()))
                sb.Append('%').Append(((b >> 4) & 0xF).ToString("X")).Append((b & 0xF).ToString("X"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 连接主循环（阻塞至 ct 取消）：解析房间 → 取 token/线路 → 逐条线路尝试建连；
    /// 异常/断线退避重连。未开播时默认不建连、周期复查；connectWhenNotLive=true 则无视开播状态直接建连，
    /// 主播一开播弹幕即送达（省去轮询延迟）。
    /// </summary>
    public async Task RunAsync(int roomId, Action<LiveEvent> onEvent, bool connectWhenNotLive, CancellationToken ct)
    {
        var delayMs = 1_000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (internalId, uid, live) = await ResolveRoomAsync(roomId, ct);

                if (!live && !connectWhenNotLive)
                {
                    _log($"房间 {roomId} 当前未开播（uid={uid}），60s 后复查");
                    await Task.Delay(60_000, ct);
                    continue;
                }
                if (!live)
                    _log($"房间 {roomId} 当前未开播（uid={uid}），已开启「未开播也连接」，直接建连等待开播");

                var (myUid, buvid3, effCookie) = await EnsureIdentityAsync(ct);
                var (token, hosts) = await _wbi.GetDanmuInfoAsync(internalId, effCookie, ct);
                _log($"取到弹幕 token（{hosts.Count} 条线路，内部 room={internalId}）");

                Exception? lastErr = null;
                for (var i = 0; i < hosts.Count && !ct.IsCancellationRequested; i++)
                {
                    var (host, port) = hosts[i];
                    var wsUrl = $"wss://{host}:{port}/sub";
                    try
                    {
                        _log($"连接 {wsUrl}（线路 {i + 1}/{hosts.Count}）");
                        delayMs = 1_000; // 成功建连即重置退避
                        await RunSessionAsync(wsUrl, internalId, token, myUid, buvid3, effCookie, onEvent, ct);
                        return; // ct 取消才走到这
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        lastErr = ex;
                        _log($"线路 {host} 失败：{ex.Message}");
                    }
                }
                if (lastErr != null) _log("所有线路均失败：" + lastErr.Message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log("连接异常：" + ex.Message);
            }

            try { await Task.Delay(delayMs, ct); }
            catch (OperationCanceledException) { break; }
            delayMs = Math.Min(delayMs * 2, 30_000);
        }
    }

    // ---------------- 房间解析 ----------------

    /// <summary>get_info：公开房间号 → 内部 room id / 主播 uid / 开播状态。</summary>
    private async Task<(int RoomId, long Uid, bool Live)> ResolveRoomAsync(int publicId, CancellationToken ct)
    {
        using var req = MakeRequest("https://api.live.bilibili.com/room/v1/Room/get_info?room_id=" + publicId, publicId);
        SetCookieHeader(req, Cookie);
        using var resp = await Http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
            throw new Exception($"get_info 失败 code={code.GetInt32()} msg={GetStr(root, "message")}");

        var data = root.GetProperty("data");
        // 新版接口 room_id 直接在 data 下；兼容旧版 data.room_info
        var roomId = GetInt(data, "room_id") > 0 ? GetInt(data, "room_id") : publicId;
        var uid = GetLong(data, "uid");
        if (data.TryGetProperty("room_info", out var ri))
        {
            var r2 = GetInt(ri, "room_id");
            if (r2 > 0) roomId = r2;
            var u2 = GetLong(ri, "uid");
            if (u2 > 0) uid = u2;
        }
        var live = GetInt(data, "live_status") == 1;
        return (roomId, uid, live);
    }

    // ---------------- 登录身份 ----------------

    /// <summary>
    /// 确保登录身份：Cookie 缺 DedeUserID 时从 nav 接口取 mid，缺 buvid3 时从 spi 接口取（结果缓存）。
    /// token 与 buvid 绑定，所以必须在 getDanmuInfo 之前补齐并用于后续全部请求。
    /// </summary>
    private async Task<(long Uid, string Buvid3, string EffectiveCookie)> EnsureIdentityAsync(CancellationToken ct)
    {
        var cookie = _cookie.Trim();
        var eff = cookie;

        long uid;
        if (long.TryParse(GetCookieValue(cookie, "DedeUserID"), out var cu) && cu > 0) uid = cu;
        else if (_autoUid > 0) uid = _autoUid;
        else
        {
            try
            {
                using var req = MakeRequest("https://api.bilibili.com/x/web-interface/nav");
                SetCookieHeader(req, cookie);
                using var resp = await Http.SendAsync(req, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var d) && GetInt(d, "isLogin") == 1)
                    _autoUid = GetLong(d, "mid");
            }
            catch { /* 网络异常：保持 uid=0，下面统一报错 */ }
            uid = _autoUid;
        }
        if (uid <= 0)
            throw new WebSocketException("无法确定登录身份：Cookie 需含 SESSDATA（或 DedeUserID）");

        var buvid3 = GetCookieValue(cookie, "buvid3");
        if (buvid3.Length == 0)
        {
            if (_autoBuvid3.Length == 0)
            {
                try
                {
                    using var req = MakeRequest("https://api.bilibili.com/x/frontend/finger/spi");
                    SetCookieHeader(req, cookie);
                    using var resp = await Http.SendAsync(req, ct);
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("data", out var d))
                        _autoBuvid3 = GetStr(d, "b_3");
                }
                catch { /* 网络异常：保持空，下面统一报错 */ }
            }
            buvid3 = _autoBuvid3;
        }
        if (buvid3.Length == 0)
            throw new WebSocketException("无法获取 buvid3（spi 接口失败）");
        if (!cookie.Contains("buvid3=")) eff += "; buvid3=" + buvid3;

        return (uid, buvid3, eff);
    }

    // ---------------- 单次会话 ----------------

    private async Task RunSessionAsync(string wsUrl, int roomId, string token, long uid, string buvid, string cookie, Action<LiveEvent> onEvent, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        // 握手带浏览器头 + Cookie（comet 协议校验登录态身份）
        ws.Options.SetRequestHeader("User-Agent", Ua);
        ws.Options.SetRequestHeader("Origin", "https://live.bilibili.com");
        ws.Options.SetRequestHeader("Referer", "https://live.bilibili.com/" + roomId);
        var cookieHdr = SanitizeCookie(cookie);
        if (cookieHdr.Length > 0) ws.Options.SetRequestHeader("Cookie", cookieHdr);

        await ws.ConnectAsync(new Uri(wsUrl), ct);

        // 认证帧（op=7）：uid/buvid 必须与 token 的签发身份一致
        var authJson = JsonSerializer.Serialize(new
        {
            uid,
            roomid = roomId,
            protover = 3,
            buvid,
            support_ack = true,
            queue_uuid = RandQueueId(),
            scene = "room",
            platform = "web",
            type = 2,
            key = token,
        });
        await SendFrameAsync(ws, OpAuth, Encoding.UTF8.GetBytes(authJson), ct);

        // runCts：看门狗/错误时主动结束会话（外层 RunAsync 负责重连）
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lastData = DateTime.UtcNow;
        var clockLock = new object();

        var heartbeat = Task.Run(async () =>
        {
            try
            {
                while (!runCts.IsCancellationRequested)
                {
                    await Task.Delay(HeartbeatIntervalMs, runCts.Token);
                    await SendFrameAsync(ws, OpHeartbeat, "{}"u8.ToArray(), runCts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch { /* 发送失败：接收循环/看门狗会收尾 */ }
        });

        var watchdog = Task.Run(async () =>
        {
            try
            {
                while (!runCts.IsCancellationRequested)
                {
                    await Task.Delay(10_000, runCts.Token);
                    lock (clockLock)
                        if ((DateTime.UtcNow - lastData).TotalMilliseconds > StallTimeoutMs)
                        {
                            _log("接收超时（" + StallTimeoutMs / 1000 + "s 无下行帧），重连");
                            runCts.Cancel();
                            return;
                        }
                }
            }
            catch (OperationCanceledException) { }
        });

        // 接收缓冲：一个 WS 消息可能含多个包，也可能只含半个包
        var buf = new byte[64 * 1024];
        var bufLen = 0;

        try
        {
            while (true)
            {
                var tmp = new byte[64 * 1024];
                ValueWebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(new Memory<byte>(tmp), runCts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) throw new WebSocketException("连接已被服务器关闭");
                } while (r.Count <= 0);

                lock (clockLock) lastData = DateTime.UtcNow;

                if (bufLen + r.Count > buf.Length)
                {
                    Array.Resize(ref buf, Math.Min(bufLen + r.Count, MaxPacketBytes));
                    if (bufLen + r.Count > buf.Length) throw new InvalidDataException("下行数据超过缓冲上限");
                }
                Buffer.BlockCopy(tmp, 0, buf, bufLen, r.Count);
                bufLen += r.Count;

                var pos = 0;
                while (pos + 16 <= bufLen)
                {
                    var total = Be32(buf, pos);
                    var hdrLen = (buf[pos + 4] << 8) | buf[pos + 5];
                    var proto = (buf[pos + 6] << 8) | buf[pos + 7];
                    var op = Be32(buf, pos + 8);
                    if (total < 16 || total > MaxPacketBytes || hdrLen < 16 || pos + total > bufLen) break; // 等更多数据

                    var bodyOff = pos + hdrLen;
                    var bodyLen = total - hdrLen;
                    byte[] body = Array.Empty<byte>();
                    if (bodyLen > 0)
                    {
                        body = new byte[bodyLen];
                        Buffer.BlockCopy(buf, bodyOff, body, 0, bodyLen);
                    }

                    Dispatch(op, proto, body, onEvent);
                    pos += total;
                }
                if (pos > 0)
                {
                    Buffer.BlockCopy(buf, pos, buf, 0, bufLen - pos);
                    bufLen -= pos;
                }
            }
        }
        finally
        {
            runCts.Cancel();
            try { ws.Abort(); } catch { }
            await Task.WhenAll(heartbeat, watchdog).ContinueWith(_ => { }); // 不传播子任务异常
        }
    }

    /// <summary>按 op/体协议分发：brotli 体内是拼接的内层包，递归解析。</summary>
    private void Dispatch(int op, int proto, byte[] body, Action<LiveEvent> onEvent)
    {
        if (proto == ProtoBrotli && body.Length > 0)
        {
            byte[] inner;
            using (var ms = new MemoryStream(body))
            using (var br = new BrotliStream(ms, CompressionMode.Decompress))
            using (var outMs = new MemoryStream())
            {
                br.CopyTo(outMs);
                inner = outMs.ToArray();
            }
            var pos = 0;
            while (pos + 16 <= inner.Length)
            {
                var total = Be32(inner, pos);
                var hdrLen = (inner[pos + 4] << 8) | inner[pos + 5];
                var proto2 = (inner[pos + 6] << 8) | inner[pos + 7];
                var op2 = Be32(inner, pos + 8);
                if (total < 16 || total > MaxPacketBytes || hdrLen < 16 || pos + total > inner.Length) break;
                var body2 = new byte[total - hdrLen];
                Buffer.BlockCopy(inner, pos + hdrLen, body2, 0, body2.Length);
                Dispatch(op2, proto2, body2, onEvent);
                pos += total;
            }
            return;
        }

        switch (op)
        {
            case OpAuthAck: // {"code":0} 成功；非 0 → 抛异常换线路
            {
                if (!TryParseCode(body, out var code, out var msg) || code != 0)
                    throw new WebSocketException($"认证被拒 code={code}（{msg}）");
                _log("认证成功，开始接收弹幕");
                break;
            }
            case OpHbAck:
                break; // 心跳应答：忽略
            case OpPush:
                foreach (var ev in ParseCmd(body)) onEvent(ev);
                break;
            default:
                break;
        }
    }

    // ---------------- 帧编码 ----------------

    /// <summary>客户端帧：[总长 u32][头长=16 u16][体协议=1 u16][op u32][标志=1 u32] + JSON 体。</summary>
    private static byte[] MakeFrame(int op, byte[] body)
    {
        var f = new byte[16 + body.Length];
        WriteBe(f, 0, 16 + body.Length);
        f[4] = 0; f[5] = 16;          // header length = 16
        f[6] = 0; f[7] = 1;           // body protocol（JSON）
        WriteBe(f, 8, op);
        WriteBe(f, 12, 1);            // flag：请求
        Buffer.BlockCopy(body, 0, f, 16, body.Length);
        return f;
    }

    private static async Task SendFrameAsync(WebSocket ws, int op, byte[] body, CancellationToken ct)
    {
        var frame = MakeFrame(op, body);
        await ws.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, ct);
    }

    private static string RandQueueId()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var sb = new StringBuilder(8);
        for (var i = 0; i < 8; i++) sb.Append(chars[Random.Shared.Next(chars.Length)]);
        return sb.ToString();
    }

    private static int Be32(byte[] b, int off) =>
        (b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3];

    private static void WriteBe(byte[] b, int off, int v)
    {
        b[off] = (byte)(v >> 24);
        b[off + 1] = (byte)(v >> 16);
        b[off + 2] = (byte)(v >> 8);
        b[off + 3] = (byte)v;
    }

    private static bool TryParseCode(byte[] body, out int code, out string msg)
    {
        code = 0; msg = "";
        if (body.Length == 0) return true; // 空体按成功处理（部分线路）
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var c)) code = c.GetInt32();
            msg = GetStr(doc.RootElement, "msg") + GetStr(doc.RootElement, "message");
            return true;
        }
        catch { return false; }
    }

    // ---------------- 事件解析（cmd → LiveEvent） ----------------

    private DateTime _lastParseLogAt;

    /// <summary>op=5 推送 JSON：{"cmd":"...","data":{...}} → LiveEvent 列表。</summary>
    private List<LiveEvent> ParseCmd(byte[] body)
    {
        var result = new List<LiveEvent>();
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            LogParseFail(ex);
            return result;
        }

        var cmd = GetStr(root, "cmd");
        switch (cmd)
        {
            case "DANMU_MSG":
                // info[0][15] = {extra: JSON 字符串(content...), user: {uid, base{name,face}, medal...}}
                // 登录态收到完整昵称+uid；匿名才是打码名(我***)和 uid=0
                if (root.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Array)
                    foreach (var it in info.EnumerateArray())
                    {
                        if (it.ValueKind != JsonValueKind.Array || it.GetArrayLength() < 16) continue;
                        var el15 = it[15];
                        string? extraStr = el15.ValueKind == JsonValueKind.String ? el15.GetString() : null;
                        if (extraStr == null && el15.TryGetProperty("extra", out var e2) && e2.ValueKind == JsonValueKind.String)
                            extraStr = e2.GetString();
                        if (string.IsNullOrEmpty(extraStr)) continue;
                        try
                        {
                            using var edoc = JsonDocument.Parse(extraStr);
                            var content = GetStr(edoc.RootElement, "content").Trim();
                            if (content.Length == 0) continue;
                            var user = "";
                            var mid = 0L;
                            if (el15.ValueKind == JsonValueKind.Object && el15.TryGetProperty("user", out var u))
                            {
                                mid = GetLong(u, "uid");
                                if (u.TryGetProperty("base", out var b) && b.TryGetProperty("name", out var n))
                                    user = n.GetString() ?? "";
                            }
                            result.Add(new LiveEvent(LiveKind.Danmaku, user, mid, content, 0));
                        }
                        catch { /* 单条解析失败跳过 */ }
                    }
                break;

            case "INTERACT_WORD_V2":
                // data.pb = base64 protobuf (bilibili.live.xuserreward.v1.InteractWord)：
                // f2=uname，f5=msg_type（1进场 2关注 3分享 4特别关注 5互粉 6链接）
                // 注意：这不是弹幕！弹幕走 DANMU_MSG。进场(1)太频繁，不转事件
                if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
                {
                    var pbB64 = GetStr(d, "pb");
                    if (!string.IsNullOrEmpty(pbB64))
                    {
                        try
                        {
                            var (msgType, uname) = ParseInteractWordPb(Convert.FromBase64String(pbB64));
                            if (uname.Length > 0)
                            {
                                var action = msgType switch
                                {
                                    2 => "关注了主播",
                                    3 => "分享了直播间",
                                    4 => "特别关注了主播",
                                    5 => "和主播互粉了",
                                    _ => null, // 1=进场 / 6=链接(自定义文案)：不响应
                                };
                                if (action != null) result.Add(new LiveEvent(LiveKind.Interact, uname, 0, action, 0));
                            }
                        }
                        catch { /* pb 结构变化时跳过 */ }
                    }
                }
                break;

            case "SEND_GIFT":
                if (root.TryGetProperty("data", out var gd))
                {
                    var name = GetStr(gd, "giftName");
                    var count = Math.Max(1, GetInt(gd, "count"));
                    var priceYuan = GetDouble(gd, "price") / 100.0; // 金瓜子：1 元 = 100
                    var guardCount = GetInt(gd, "guard_count");
                    var text = name + (count > 1 ? " x" + count : "");
                    if (guardCount > 0)
                    {
                        text += $"（含月舰长x{guardCount}）";
                        priceYuan = Math.Max(priceYuan, guardCount * 198); // 至少按舰长价计
                    }
                    var uname = GetStr(gd, "uname"); if (uname.Length == 0) uname = GetStr(gd, "username"); // B站 SEND_GIFT 昵称字段是 uname（个别/旧版本 username）
                    result.Add(new LiveEvent(LiveKind.Gift, uname, GetLong(gd, "uid") > 0 ? GetLong(gd, "uid") : -1, text, priceYuan));
                }
                break;

            case "SUPER_CHAT_MESSAGE":
                if (root.TryGetProperty("data", out var sd) && sd.TryGetProperty("msg", out var m))
                {
                    var message = GetStr(m, "message").Trim();
                    if (message.Length > 0)
                        result.Add(new LiveEvent(LiveKind.Sc, GetStr(m, "nickname"), GetLong(m, "uid"), message, GetDouble(m, "price")));
                }
                break;

            default: // NOTICE_MSG/ENTRY_EFFECT/LIKE_INFO_V3_* 等：不处理
                break;
        }
        return result;
    }

    /// <summary>InteractWord pb 最小解析：f2=uname（字符串），f5=msg_type（varint）。</summary>
    private static (int MsgType, string Uname) ParseInteractWordPb(byte[] buf)
    {
        var msgType = 0;
        var uname = "";
        var pos = 0;
        while (pos < buf.Length)
        {
            if (!ReadVarint(buf, ref pos, out var tag)) break;
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            try
            {
                switch (wire)
                {
                    case 0:
                        if (field == 5 && ReadVarint(buf, ref pos, out var mt)) msgType = (int)mt;
                        else ReadVarint(buf, ref pos, out _);
                        break;
                    case 1: pos += 8; break;
                    case 5: pos += 4; break;
                    case 2:
                        if (!ReadVarint(buf, ref pos, out var lenL)) return (msgType, uname);
                        if (lenL > int.MaxValue || pos + lenL > buf.Length) return (msgType, uname);
                        var len = (int)lenL;
                        if (field == 2) uname = Encoding.UTF8.GetString(buf, pos, len);
                        pos += len;
                        break;
                    default: return (msgType, uname);
                }
            }
            catch { return (msgType, uname); }
        }
        return (msgType, uname);
    }

    private static bool ReadVarint(byte[] buf, ref int pos, out long value)
    {
        value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (pos >= buf.Length) return false;
            var b = buf[pos++];
            value |= (long)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }

    /// <summary>解析失败日志（30s 节流，防协议变更时刷屏）。</summary>
    private void LogParseFail(Exception ex)
    {
        if ((DateTime.UtcNow - _lastParseLogAt).TotalSeconds > 30)
        {
            _lastParseLogAt = DateTime.UtcNow;
            _log("事件解析失败：" + ex.Message);
        }
    }

    // ---------------- Cookie / JSON 小工具 ----------------

    /// <summary>从 Cookie 串取指定键值（name=value; ...）。</summary>
    internal static string GetCookieValue(string cookie, string name)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return "";
        foreach (var part in cookie.Split(';'))
        {
            var kv = part.Trim();
            var eq = kv.IndexOf('=');
            if (eq <= 0) continue;
            if (kv[..eq].Trim() == name) return kv[(eq + 1)..].Trim();
        }
        return "";
    }

    internal static string GetStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    internal static long GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : 0;

    internal static double GetDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0.0;
}
