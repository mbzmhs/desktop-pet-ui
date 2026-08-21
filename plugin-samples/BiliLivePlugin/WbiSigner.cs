using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BiliLivePlugin;

/// <summary>
/// WBI 签名 + getDanmuInfo（弹幕 comet 服务的 token 与线路列表）。
/// mixin_key 从 nav 接口的 wbi_img 提取，缓存 1 小时。
/// </summary>
internal sealed class WbiSigner
{
    private static readonly int[] MixinTab =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40, 61,
        26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36,
        20, 34, 44, 52
    };

    private readonly Action<string> _log;
    private string _mixinKey = "";
    private DateTime _keyFetchedAtUtc;

    public WbiSigner(Action<string> log) => _log = log;

    /// <summary>getDanmuInfo（WBI 签名）→ (token, 线路列表)。失败抛异常。</summary>
    public async Task<(string Token, List<(string Host, int Port)> Hosts)> GetDanmuInfoAsync(int roomId, string cookie, CancellationToken ct)
    {
        if (_mixinKey.Length == 0 || (DateTime.UtcNow - _keyFetchedAtUtc).TotalHours > 1)
            await RefreshMixinKeyAsync(cookie, ct);

        var wts = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        var p = new SortedDictionary<string, string>
        {
            ["id"] = roomId.ToString(),
            ["type"] = "0",
            ["web_location"] = "444.8",
            ["wts"] = wts.ToString(),
        };
        var query = string.Join("&", p.Select(kv => JsEncode(kv.Key) + "=" + JsEncode(kv.Value)));
        var wRid = Md5Hex(query + _mixinKey);

        using var req = BiliWsClient.MakeRequest(
            "https://api.live.bilibili.com/xlive/web-room/v1/index/getDanmuInfo?" + query + "&w_rid=" + wRid, roomId);
        BiliWsClient.SetCookieHeader(req, cookie);
        using var resp = await BiliWsClient.Http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var code = BiliWsClient.GetInt(root, "code");
        if (code != 0) throw new Exception($"getDanmuInfo 失败 code={code} msg={BiliWsClient.GetStr(root, "message")}");

        var data = root.GetProperty("data");
        var token = BiliWsClient.GetStr(data, "token");
        if (string.IsNullOrEmpty(token)) throw new Exception("getDanmuInfo 未返回 token（需要登录态 Cookie）");

        var hosts = new List<(string, int)>();
        if (data.TryGetProperty("host_list", out var hl) && hl.ValueKind == JsonValueKind.Array)
            foreach (var h in hl.EnumerateArray())
            {
                var host = BiliWsClient.GetStr(h, "host");
                var port = BiliWsClient.GetInt(h, "wss_port");
                if (!string.IsNullOrEmpty(host) && port > 0) hosts.Add((host, port));
            }
        if (hosts.Count == 0) throw new Exception("getDanmuInfo 未返回可用线路");
        return (token, hosts);
    }

    /// <summary>nav 接口 → wbi_img → mixin_key（img/sub key 按混淆表重排取前 32 位）。</summary>
    private async Task RefreshMixinKeyAsync(string cookie, CancellationToken ct)
    {
        try
        {
            using var req = BiliWsClient.MakeRequest("https://api.bilibili.com/x/web-interface/nav");
            BiliWsClient.SetCookieHeader(req, cookie);
            using var resp = await BiliWsClient.Http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var d) || !d.TryGetProperty("wbi_img", out var wbi)) return;

            var imgKey = BiliWsClient.GetStr(wbi, "img_url").Split('/').Last().Split('.')[0];
            var subKey = BiliWsClient.GetStr(wbi, "sub_url").Split('/').Last().Split('.')[0];
            if (imgKey.Length < 32 || subKey.Length < 32) return;

            var orig = imgKey + subKey;
            var sb = new StringBuilder(64);
            foreach (var i in MixinTab) sb.Append(orig[i]);
            _mixinKey = sb.ToString(0, 32);
            _keyFetchedAtUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _log("WBI key 刷新失败：" + ex.Message);
        }
    }

    /// <summary>encodeURIComponent 等价（RFC3986 基础上放行 !'()*）。</summary>
    internal static string JsEncode(string s) =>
        Uri.EscapeDataString(s)
            .Replace("%21", "!").Replace("%27", "'")
            .Replace("%28", "(").Replace("%29", ")").Replace("%2A", "*");

    internal static string Md5Hex(string s)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
