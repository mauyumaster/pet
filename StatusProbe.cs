// 状态探针：网络连通性 + VPN(google) 出口延迟/开关注 + DeepSeek 余额（换算成剩余 token）。
// 全程后台 async、三路并行，绝不占用渲染线程；余额接口带 60s 缓存，桌宠常驻不打太勤。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AzhuPet
{
    internal sealed class StatusProbe
    {
        private const string Host = "api.deepseek.com";          // 通用连通性目标
        private const string GoogleHost = "www.google.com";      // VPN 出口目标（通常需绕 VPN 才通）
        private const int Port = 443;
        private const int NetTimeoutMs = 4000;
        public const double CacheSeconds = 60;           // 凭据/余额缓存时长（气泡与 --statustest 共用同一口径，别各自写死）
        /// <summary>主口径：WorkBuddy 积分只求和 CapacityType == 本值的条目。</summary>
        // ⚠ 口径本身就是判据，不能靠命名猜。2026-09-18 与 WorkBuddy 界面**同刻对照**过：
        //   界面 1056 / 接口 type=1 求和 1055（差 1 ＝ 活读数漂移）；而接口 TotalDosage（含 type=4 那笔 500）
        //   是 1555，**界面并不显示它**。改这个常量等于改结论 ⇒ 必须重新对照界面。
        public const int MainCreditType = 1;
        private const double OutputPricePerM = 8.0;      // deepseek-chat 输出价 ¥/百万 tokens，换算剩余 token 用
        private static readonly double TokensPerYuan = 1_000_000.0 / OutputPricePerM;

        private readonly HttpClient _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        }) { Timeout = TimeSpan.FromSeconds(6) };
        private StatusReport _cache;
        private DateTime _cacheAt;
        private readonly object _lock = new object();

        public string ApiKey { get; set; }                     // DeepSeek key
        public string BalanceUrl = "";                    // 自定义余额接口 URL；空 = 走 DeepSeek
        public string BalanceToken = "";                  // 自定义：Bearer token（可选）
        public string BalanceHeader = "";                 // 自定义：整段 header，如 "cookie: a=1"（可选）
        public string BalanceKey = "total_balance";       // 自定义：JSON 数值字段名
        public string BalanceUnit = "";                   // 自定义：显示单位，如 "积分"
        public List<BalanceSource> CustomSources = new List<BalanceSource>();   // 模板化自定义余额源(balances.json)
        // 凭据文件（URL + `---headers---` 段 + `---body---` 段）：
        //   balance_secret.txt   —— TRAE 登录态（authorization 头）
        //   workbuddy_secret.txt —— WorkBuddy 积分（cookie + x-user-id 头）
        // ⚠ 这两个文件里是**活的登录凭据**，绝不能放进被云同步的目录。
        //   首选落点 = %LOCALAPPDATA%\AzhuPet（与 config.json 同处，不进 Obsidian 库 ⇒ 不进坚果云）。
        public const string TraeSecretFile = "balance_secret.txt";
        public const string WorkBuddySecretFile = "workbuddy_secret.txt";

        /// <summary>凭据目录：优先环境变量 AZHU_SECRET_DIR，否则 %LOCALAPPDATA%\AzhuPet。</summary>
        public static string SecretDir()
        {
            string env = Environment.GetEnvironmentVariable("AZHU_SECRET_DIR");
            if (!string.IsNullOrEmpty(env)) return env;
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzhuPet");
        }

        /// <summary>定位某个凭据文件：先查 SecretDir()，找不到再退回 exe 目录向上回溯 6 层
        /// （兼容把凭据留在工程目录里的老用法 —— **不推荐，那个目录在坚果云同步范围内**）。
        /// 都不存在时返回「首选落点」，便于报错时直接把该放哪儿告诉用户。</summary>
        private static string SecretPath(string fileName)
        {
            string preferred = Path.Combine(SecretDir(), fileName);
            if (File.Exists(preferred)) return preferred;
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 6; i++)
            {
                string p = Path.Combine(dir, fileName);
                if (File.Exists(p)) return p;
                var up = Directory.GetParent(dir);
                if (up == null) break;
                dir = up.FullName;
            }
            return preferred;
        }

        private static string TraeSecretPath() => SecretPath(TraeSecretFile);
        private static string WorkBuddySecretPath() => SecretPath(WorkBuddySecretFile);

        /// <summary>凭据文件缺失时的报错文本：点明是「文件没找到」而不是「接口取不到」，并给出该放哪儿。
        /// 这两种失败在气泡里长得一样是上一版的坑 —— 「取不到」三个字囊括了太多东西。</summary>
        private static string MissingSecretNote(string fileName) =>
            "未找到凭据文件 " + fileName + "（该放 " + SecretDir() + "）";

        /// <summary>口径漂移哨兵（纯函数）：主口径 + 非主口径 应当恒等于接口的全桶合计。</summary>
        // 不等 ⇒ 要么接口加了新桶、要么 TotalDosage 换了语义 —— 两种情况都会让「主口径」这个
        // 结论失效，而它正是气泡上那个数字的唯一来源。
        // 抽成纯函数是为了**能被离线证伪**：--calibertest 直接喂不一致的三个数，看它会不会报；
        // 只写在 --statustest 里的话，真接口一辈子不会配合我们变形 ⇒ 这条判据就没资格变红。
        // 阈值取绝对值 0.5（判据纪律 3：判据要带绝对阈值），避开浮点噪声。
        public static bool CaliberDrift(double remain, double other, double allBuckets)
        {
            if (double.IsNaN(remain) || double.IsNaN(other) || double.IsNaN(allBuckets)) return false;  // 取不到 ≠ 漂移
            return Math.Abs(remain + other - allBuckets) > 0.5;
        }

        /// <summary>宽容取数：CapacityRemain 现在是数字，但同一响应里 CapacityRemainPrecise 已经是**字符串**。
        /// 主字段哪天跟着变成字符串，旧写法就读不出来了 —— 而且 TryGetDouble 对非 Number 元素是**抛异常**，
        /// 不是返回 false（见 IntEl 的注释）。数字与字符串两种都收。</summary>
        private static bool NumOf(JsonElement obj, string name, out double v)
        {
            v = double.NaN;
            if (!obj.TryGetProperty(name, out var el)) return false;
            if (el.ValueKind == JsonValueKind.Number) return el.TryGetDouble(out v);
            if (el.ValueKind == JsonValueKind.String)
                return double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            return false;
        }

        /// <summary>宽容取整数：数字与数字字符串都收；其它类型一律当「没这个字段」。</summary>
        // ⚠ 为什么非得自己写一个：**JsonElement.TryGetInt32 / TryGetDouble 遇到非 Number 元素会抛
        //   InvalidOperationException，而不是返回 false**（网上很多示例都写成「Try 就是安全」）。
        //   2026-09-18 由 --calibertest 的一组合成响应当场打脸：真接口一直给 Number，所以这种坑
        //   联网测试一辈子照不出来。抛在解析中途的后果**不是「少算」而是「整条读数丢失」** ——
        //   c.Remain 还没赋值就跳进 catch，气泡上只剩「取不到（解析失败 …）」。
        private static bool IntEl(JsonElement el, out int v)
        {
            v = 0;
            if (el.ValueKind == JsonValueKind.Number) return el.TryGetInt32(out v);
            if (el.ValueKind == JsonValueKind.String)
                return int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
            return false;
        }

        /// <summary>宽容取浮点（元素版，用于 TotalDosage 这类直接拿到的字段）。</summary>
        private static bool DblEl(JsonElement el, out double v)
        {
            v = double.NaN;
            if (el.ValueKind == JsonValueKind.Number) return el.TryGetDouble(out v);
            if (el.ValueKind == JsonValueKind.String)
                return double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            return false;
        }

        /// <summary>凭据文件的解析结果（路径 + 是否存在）。供气泡报错与 --statustest 用；**不返回任何密文**。</summary>
        public static (string path, bool exists)[] SecretSlots()
        {
            var a = SecretPath(TraeSecretFile);
            var b = SecretPath(WorkBuddySecretFile);
            return new[] { (a, File.Exists(a)), (b, File.Exists(b)) };
        }

        /// <summary>缓存是否仍新鲜（避免频繁打余额 API）。</summary>
        public bool FresherThan(double seconds) => _cache != null && (DateTime.UtcNow - _cacheAt).TotalSeconds < seconds;

        public StatusReport GetCached() { lock (_lock) return _cache; }

        /// <summary>配置变更后主动丢弃旧读数；否则面板说“已保存”，气泡最长仍会显示 60 秒旧结果。</summary>
        public void ClearCache() { lock (_lock) { _cache = null; _cacheAt = default(DateTime); } }

        /// <summary>设置面板的“测试连接”。复用正式执行器，避免测试成功、实际气泡失败的两套实现。</summary>
        public async Task<BalanceCell> TestSourceAsync(BalanceSource src)
        {
            var apply = await GenericBalanceAsync(src);
            var report = new StatusReport();
            apply(report);
            return report.DynamicRows.Count == 0
                ? new BalanceCell { Name = src?.Name ?? "余额源", Ok = false, Error = "没有返回测试结果" }
                : report.DynamicRows[0];
        }

        public async Task<StatusReport> CheckAsync()
        {
            var rep = new StatusReport();
            // 网络可用性：deepseek 直连（国内本来就通，用来判本机离线与否）。
            // 出口延迟：若开着 Clash 系统代理，则经由代理发 HTTP CONNECT 到 google——测到的才是走节点的真实延迟；
            //           没开代理则退化为直连（此时 google 通常不通＝出口不通）。
            string proxy = SystemProxy();
            var link = ProbeDirectAsync(Host, Port);
            var google = proxy != null ? ProbeViaProxyAsync(GoogleHost, Port, proxy) : ProbeDirectAsync(GoogleHost, Port);
            var balance = BalanceAsync();
            string traePath = TraeSecretPath();
            string wbPath = WorkBuddySecretPath();
            bool traeFound = File.Exists(traePath);
            bool wbFound = File.Exists(wbPath);
            Task<Action<StatusReport>> wb = wbFound ? WorkBuddyBalanceAsync(wbPath) : null;

            var all = new List<Task> { link, google, balance };
            if (wb != null) all.Add(wb);
            var custom = new List<Task<Action<StatusReport>>>();
            foreach (var src in CustomSources)
            {
                if (!src.Enabled) continue;
                var task = GenericBalanceAsync(src);
                custom.Add(task);
                all.Add(task);
            }
            await Task.WhenAll(all);

            (rep.Reachable, rep.LatencyMs) = link.Result;
            (rep.GoogleReachable, rep.GoogleLatencyMs) = google.Result;
            rep.VpnName = VpnActiveName();
            rep.VpnTunnel = rep.VpnName != null;
            balance.Result(rep);
            if (wb != null) wb.Result(rep);
            ApplyCustomResults(rep, custom.Select(x => x.Result));

            // ⚠ 凭据文件「不见了」绝不允许静默跳过 —— 上一版正是如此：文件一没，
            //   「WorkBuddy 积分」那一行**整个从气泡里消失**，看图的人只看到「少了一行」，
            //   完全不知道是文件没找到。规则：同一安装里另一份凭据还在 ⇒ 这人确实在走凭据这条路，
            //   那么缺的这一份就显式报出来（只用 DeepSeek 的人不会被这句话打扰）。
            if (!traeFound && wbFound) rep.TraeError = MissingSecretNote(TraeSecretFile);
            if (!wbFound && traeFound) rep.WorkbuddyError = MissingSecretNote(WorkBuddySecretFile);

            lock (_lock) { _cache = rep; _cacheAt = DateTime.UtcNow; }
            return rep;
        }

        /// <summary>把并行完成的自定义余额结果写入报告。单独抽出，便于离线回归测试覆盖聚合链路。</summary>
        internal static void ApplyCustomResults(StatusReport report, IEnumerable<Action<StatusReport>> results)
        {
            foreach (var apply in results)
                apply(report);
        }

        /// <summary>带超时地 await 一个 Task；超时抛 TimeoutException。先 await 成功、后续再查 Connected，
        /// 避免连接刚完成时 Connected 还没刷新的竞态（表现为误报“离线”）。</summary>
        private static async Task Await(Task t, int ms)
        {
            var done = await Task.WhenAny(t, Task.Delay(ms));
            if (done != t) throw new TimeoutException("net");
            await t;
        }

        /// <summary>直连建联目标并计时；连不上或超时返回 (false, -1)。</summary>
        private static async Task<(bool ok, int ms)> ProbeDirectAsync(string host, int port)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using (var t = new TcpClient())
                {
                    await Await(t.ConnectAsync(host, port), NetTimeoutMs);
                    if (!t.Connected) return (false, -1);
                }
                sw.Stop();
                return (true, (int)sw.ElapsedMilliseconds);
            }
            catch { return (false, -1); }
        }

        /// <summary>解析系统代理地址为 host:port。兼容 Clash Verge 的多协议分号形式
        /// （`http=127.0.0.1:8888;https=127.0.0.1:8888`、`8888;https=127.0.0.1`、裸 `127.0.0.1:7897`）。
        /// 解析失败返回 (null,-1)。</summary>
        private static (string host, int port) SplitProxy(string proxyAddr)
        {
            if (string.IsNullOrEmpty(proxyAddr)) return (null, -1);
            foreach (string rawSeg in proxyAddr.Split(';'))
            {
                string s = rawSeg.Trim();
                int eq = s.IndexOf('=');
                if (eq > 0) s = s.Substring(eq + 1).Trim();   // 剥掉 http=/https=/socks= 前缀
                int ci = s.LastIndexOf(':');
                if (ci > 0 && s.Substring(0, ci).Trim().Length > 0
                    && int.TryParse(s.Substring(ci + 1).Trim(), out int p) && p >= 1 && p <= 65535)
                    return (s.Substring(0, ci).Trim(), p);      // host:port
                if (int.TryParse(s.Trim(), out int p2) && p2 >= 1 && p2 <= 65535)
                    return ("127.0.0.1", p2);                    // 裸端口 → 默认本机
            }
            return (null, -1);
        }

        /// <summary>经 HTTP 代理 CONNECT 到目标并计时（反映走节点后的真实出口延迟）。
        /// 所谓 VPN 客户端里的节点延迟外部读不到，这就是最贴近的估计。</summary>
        private static async Task<(bool ok, int ms)> ProbeViaProxyAsync(string host, int port, string proxyAddr)
        {
            try
            {
                var (ph, pp) = SplitProxy(proxyAddr);
                if (ph == null || pp <= 0) return (false, -1);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using (var t = new TcpClient())
                {
                    await Await(t.ConnectAsync(ph, pp), NetTimeoutMs);
                    if (!t.Connected) return (false, -1);
                    byte[] req = Encoding.ASCII.GetBytes(
                        "CONNECT " + host + ":" + port + " HTTP/1.1\r\nHost: " + host + ":" + port + "\r\n\r\n");
                    await t.GetStream().WriteAsync(req, 0, req.Length);

                    byte[] buf = new byte[4096];
                    int got = 0;
                    bool ok = false;
                    var deadline = DateTime.UtcNow.AddMilliseconds(NetTimeoutMs);
                    var stream = t.GetStream();
                    while (DateTime.UtcNow < deadline)
                    {
                        int remaining = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                        if (remaining <= 0) break;
                        int n;
                        try
                        {
                            using (var cts = new CancellationTokenSource(remaining))
                                n = await stream.ReadAsync(buf, got, buf.Length - got, cts.Token);
                        }
                        catch { break; }
                        if (n <= 0) break;
                        got += n;
                        string head = Encoding.ASCII.GetString(buf, 0, got);
                        if (head.IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0)
                        {
                            ok = head.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) ||
                                 head.StartsWith("HTTP/1.0 200", StringComparison.Ordinal);
                            break;
                        }
                        if (got >= buf.Length) break;
                    }
                    if (!ok) return (false, -1);
                }
                sw.Stop();
                return (true, (int)sw.ElapsedMilliseconds);
            }
            catch { return (false, -1); }
        }

        /// <summary>判 VPN/代理是否在走：命中活动 VPN/TUN 网卡，或系统代理开启，任一即算。返回描述名。</summary>
        private static string VpnActiveName()
        {
            string nic = ActiveVpnNic();
            if (nic != null) return nic;
            string proxy = SystemProxy();
            if (proxy != null) return "系统代理 " + proxy;
            return null;
        }

        /// <summary>扫描活动中的 VPN/隧道虚拟网卡（TUN 模式，如 clash/mihomo、TAP 加速器等）。
        /// 只认明确的 VPN/TUN 技术关键字；避免用 "virtual" 这类宽词——Intel Wi-Fi 的 Virtual WiFi
        /// FilterDriver 描述里就有 virtual，会误判成 VPN。</summary>
        private static string ActiveVpnNic()
        {
            string[] vpnKws = { "vpn", "tap", "tun", "wireguard", "tailscale", "openvpn", "clash", "mihomo", " wig", "tun2socks" };
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    string d = (ni.Description ?? "").ToLowerInvariant();
                    bool tech = ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel
                                || ni.NetworkInterfaceType == NetworkInterfaceType.Ppp;
                    foreach (var kw in vpnKws)
                        if (d.Contains(kw)) { tech = true; break; }
                    if (tech) return ni.Description;
                }
            }
            catch { }
            return null;
        }

        /// <summary>系统代理是否开启（Clash Verge 的系统代理模式不开虚拟网卡，只能看这里）。</summary>
        private static string SystemProxy()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser
                    .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (k == null) return null;
                    if (((int)(k.GetValue("ProxyEnable", 0) ?? 0)) == 0) return null;
                    string srv = k.GetValue("ProxyServer", null) as string;
                    return string.IsNullOrEmpty(srv) ? null : srv;
                }
            }
            catch { return null; }
        }

        /// <summary>余额：有 TRAE 凭据文件则走 TRAE，否则走自定义接口，否则走 DeepSeek。</summary>
        private async Task<Action<StatusReport>> BalanceAsync()
        {
            if (File.Exists(TraeSecretPath())) return await TraeBalanceAsync();
            return !string.IsNullOrEmpty(BalanceUrl) ? await CustomBalanceAsync() : await DeepSeekBalanceAsync();
        }

        /// <summary>TRAE 余额：读凭据文件（URL + headers + body），POST 取 usage_summary.total_amount / consumed_amount。</summary>
        private async Task<Action<StatusReport>> TraeBalanceAsync()
        {
            string file = TraeSecretPath();
            string err = null;
            try
            {
                string url = null;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string body = "";
                foreach (string raw in File.ReadAllLines(file))
                {
                    string t = raw.Trim();
                    if (t.StartsWith("---", StringComparison.Ordinal)) continue;
                    if (url == null && t.Length > 0) { url = t; continue; }
                    int c = raw.IndexOf(':');
                    if (c > 0)
                    {
                        string k = raw.Substring(0, c).Trim();
                        string v = raw.Substring(c + 1).Trim();
                        headers[k] = v;
                    }
                }
                if (string.IsNullOrEmpty(url)) return r => r.TraeError = "trae凭据无URL";

                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    string contentType = "text/plain";
                    foreach (var kv in headers)
                    {
                        if (string.Equals(kv.Key, "content-type", StringComparison.OrdinalIgnoreCase))
                            { int semi = kv.Value.IndexOf(';'); contentType = (semi > 0 ? kv.Value.Substring(0, semi) : kv.Value).Trim(); }
                        else if (string.Equals(kv.Key, "accept-encoding", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(kv.Key, "content-length", StringComparison.OrdinalIgnoreCase))
                            continue;   // accept-encoding 交 HttpClient 管理(避免收到 br/zstd 等它不会解的压缩体)；content-length 自动计算
                        else req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                    }
                    // StringContent 会依据 Encoding.UTF8 自带头带 charset；mediaType 只传不含 charset 的部分，避免格式非法。
                    req.Content = new StringContent(body, Encoding.UTF8, contentType);
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                    {
                        string respBody = await resp.Content.ReadAsStringAsync();
                        double total = ParseJsonDouble(respBody, "total_amount");
                        double consumed = ParseJsonDouble(respBody, "consumed_amount");
                        if (resp.IsSuccessStatusCode && !double.IsNaN(total))
                            return r => { r.TraeOk = true; r.TraeTotal = total; r.TraeConsumed = double.IsNaN(consumed) ? 0 : consumed; r.TraeAvailable = r.TraeTotal - r.TraeConsumed; };
                        string preview = (respBody ?? "").Length > 80 ? respBody.Substring(0, 80) : (respBody ?? "");
                        err = resp.IsSuccessStatusCode
                            ? "trae字段缺失(body=" + preview.Replace("\r", " ").Replace("\n", " ") + ")"
                            : DescribeHttpFailure((int)resp.StatusCode, respBody);
                    }
                }
            }
            catch (Exception ex) { err = Shrink(ex.Message); }
            var e2 = err;
            return r => r.TraeError = e2;
        }

        /// <summary>WorkBuddy 积分：读凭据文件(URL + headers含 cookie/x-user-id)，POST body {}，
        /// 从 data.Response.Data.Accounts[].CapacityRemain 求和作为可用积分。完整请求头需原样带上，
        /// 否则 APISIX 网关会 401。</summary>
        private async Task<Action<StatusReport>> WorkBuddyBalanceAsync(string file)
        {
            string err = null;
            try
            {
                string url = null;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in File.ReadAllLines(file))
                {
                    string t = raw.Trim();
                    if (t.StartsWith("---", StringComparison.Ordinal)) continue;
                    if (url == null && t.Length > 0) { url = t; continue; }
                    int c = raw.IndexOf(':');
                    if (c > 0) headers[raw.Substring(0, c).Trim()] = raw.Substring(c + 1).Trim();
                }
                if (string.IsNullOrEmpty(url)) return r => r.WorkbuddyError = "workbuddy凭据无URL";

                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    string contentType = "application/json";
                    foreach (var kv in headers)
                    {
                        if (string.Equals(kv.Key, "content-type", StringComparison.OrdinalIgnoreCase))
                            { int semi = kv.Value.IndexOf(';'); contentType = (semi > 0 ? kv.Value.Substring(0, semi) : kv.Value).Trim(); }
                        else if (string.Equals(kv.Key, "accept-encoding", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(kv.Key, "content-length", StringComparison.OrdinalIgnoreCase))
                            continue;   // 压缩与长度交 HttpClient 处理
                        else req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                    }
                    req.Content = new StringContent("{}", Encoding.UTF8, contentType);
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                    {
                        string respBody = await resp.Content.ReadAsStringAsync();
                        if (!resp.IsSuccessStatusCode) err = DescribeHttpFailure((int)resp.StatusCode, respBody);
                        else
                        {
                            // 解析与口径判定全在 ParseWorkbuddyJson 里（纯函数）——
                            // 这样口径才能被 --calibertest 用合成响应离线逼红。
                            var cal = ParseWorkbuddyJson(respBody);
                            if (cal.Ok)
                                return r =>
                                {
                                    r.WorkbuddyOk = true;
                                    r.WorkbuddyRemain = cal.Remain;
                                    r.WorkbuddyAllBucketsRemain = cal.AllBucketsRemain;
                                    r.WorkbuddyType1Count = cal.Type1Count;
                                    r.WorkbuddyAccountCount = cal.AccountCount;
                                    r.WorkbuddyTotalCount = cal.TotalCount;
                                    r.WorkbuddyOtherRemain = cal.OtherRemain;
                                };
                            string preview = (respBody ?? "").Length > 80 ? respBody.Substring(0, 80) : (respBody ?? "");
                            err = Shrink(cal.Why ?? ("字段缺失(" + preview.Replace("\r", " ").Replace("\n", " ") + ")"));
                        }
                    }
                }
            }
            catch (Exception ex) { err = Shrink(ex.Message); }
            var e2 = err;
            return r => r.WorkbuddyError = e2;
        }
        /// <summary>WorkBuddy 积分的口径判定，**纯函数**：只吃响应体字符串，不联网、不读凭据。</summary>
        // 抽出来的唯一理由（2026-09-18）：口径这种东西若只能靠真接口验，就永远没有资格失败 ——
        // 真接口不会因为我们想验就变形。把输入变成可合成的东西，--calibertest 才能拿
        // 退化形状 / 脏数据把这条判据逼红（判据纪律 7：先证明它能失败，再拿它下结论）。
        public static WbCaliber ParseWorkbuddyJson(string body)
        {
            var c = new WbCaliber { TotalCount = -1 };
            try
            {
                using (var doc = JsonDocument.Parse(body))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("code", out var codeEl) && IntEl(codeEl, out int code) && code != 0)
                    {
                        // 网关/风控回话时 data 会整个不在，只剩 code+msg。旧写法会把它
                        // 报成「字段缺失(一串 JSON)」—— 看不出真正原因。
                        string msg = root.TryGetProperty("msg", out var m) ? m.ToString() : "";
                        c.Why = "接口 code=" + code + " " + Shrink(msg);
                        return c;
                    }
                    if (!root.TryGetProperty("data", out var data)
                        || !data.TryGetProperty("Response", out var R)
                        || !R.TryGetProperty("Data", out var D)
                        || !D.TryGetProperty("Accounts", out var accs)
                        || accs.ValueKind != JsonValueKind.Array)
                    {
                        c.Why = "响应里没有 Accounts";
                        return c;
                    }
                    double sum = 0, sumOther = 0;
                    foreach (var a in accs.EnumerateArray())
                    {
                        c.AccountCount++;
                        if (!a.TryGetProperty("CapacityType", out var ct) || !IntEl(ct, out int typ)) continue;
                        double v;
                        if (!NumOf(a, "CapacityRemain", out v)) continue;   // 脏条目跳过：不炸，也**不许当 0 计入**
                        if (typ == MainCreditType) { sum += v; c.Type1Count++; }
                        else if (v > 0) sumOther += v;                       // 旁证：其余桶，不进主口径
                    }
                    if (c.Type1Count == 0)
                    {
                        c.Why = "返回 " + c.AccountCount + " 条额度里没有 type=" + MainCreditType + " 的条目（口径失效，不敢当 0 报）";
                        return c;
                    }
                    c.Remain = sum;
                    c.OtherRemain = sumOther;
                    if (D.TryGetProperty("TotalDosage", out var td) && DblEl(td, out double tv)) c.AllBucketsRemain = tv;
                    if (D.TryGetProperty("TotalCount", out var tc) && IntEl(tc, out int tcv)) c.TotalCount = tcv;
                    c.Ok = true;
                    return c;
                }
            }
            catch (Exception ex) { c.Why = "解析失败 " + Shrink(ex.Message); return c; }
        }

        /// <summary>积分解析结果：口径（<see cref="Remain"/>）＋ 旁证（其余桶、全桶合计）。Ok=false 时 Why 非空。</summary>
        public struct WbCaliber
        {
            public bool Ok;
            public string Why;
            public double Remain;            // 主口径：MainCreditType 的 CapacityRemain 之和
            public double OtherRemain;       // 非主口径桶里 >0 的余额之和（只作旁证）
            public double AllBucketsRemain;  // 接口 TotalDosage（实测 = 全桶余和，**不是消耗量**）
            public int Type1Count;
            public int AccountCount;
            public int TotalCount;           // -1 = 接口没给
        }

        /// <summary>通用余额源执行器：按 BalanceSource 配置发请求，用 PathJsonExtract 取数。
        /// 新增任何平台只需在 balances.json 加一条源，无需改代码。</summary>
        private async Task<Action<StatusReport>> GenericBalanceAsync(BalanceSource src)
        {
            var cell = new BalanceCell { Name = src.Name, Unit = src.Unit };
            try
            {
                void ApplyError(string e) { cell.Ok = false; cell.Error = e; }

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string raw in (src.HeadersText ?? "").Split('\n', '\r').Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    int c = raw.IndexOf(':'); if (c <= 0) continue;
                    headers[raw.Substring(0, c).Trim()] = raw.Substring(c + 1).Trim();
                }
                string secretHeaders, secretBody;
                (secretHeaders, secretBody) = BalanceSources.ReadSecret(src.SecretFile);
                foreach (string raw in secretHeaders.Split('\n').Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    int c = raw.IndexOf(':'); if (c <= 0) continue;
                    headers[raw.Substring(0, c).Trim()] = raw.Substring(c + 1).Trim();
                }

                bool got = false;
                if (string.IsNullOrEmpty(src.Url)) { ApplyError("无 URL"); }
                else
                {
                    string body = !string.IsNullOrWhiteSpace(src.Body) ? src.Body
                                  : (string.IsNullOrWhiteSpace(secretBody) ? "{}" : secretBody);

                    using (var req = new HttpRequestMessage(new HttpMethod(src.Method.Trim().ToUpperInvariant()), src.Url))
                    {
                        string contentType = "application/json";
                        foreach (var kv in headers)
                        {
                            if (string.Equals(kv.Key, "content-type", StringComparison.OrdinalIgnoreCase))
                                { int semi = kv.Value.IndexOf(';'); contentType = (semi > 0 ? kv.Value.Substring(0, semi) : kv.Value).Trim(); }
                            else if (string.Equals(kv.Key, "accept-encoding", StringComparison.OrdinalIgnoreCase)
                                     || string.Equals(kv.Key, "content-length", StringComparison.OrdinalIgnoreCase))
                                continue;   // 压缩与长度交 HttpClient 处理
                            else req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                        }
                        if (!string.Equals(src.Method.Trim(), "GET", StringComparison.OrdinalIgnoreCase))
                            req.Content = new StringContent(body, Encoding.UTF8, contentType);

                        using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                        {
                            string respBody = await resp.Content.ReadAsStringAsync();
                            if (!resp.IsSuccessStatusCode) { ApplyError(DescribeHttpFailure((int)resp.StatusCode, respBody)); }
                            else
                            {
                                try { using (var doc = JsonDocument.Parse(respBody)) { cell.Value = PathJsonExtract.Eval(src.PathExpr, doc); } }
                                catch { cell.Value = double.NaN; }
                                if (double.IsNaN(cell.Value))
                                {
                                    string preview = (respBody ?? "").Length > 80 ? respBody.Substring(0, 80) : (respBody ?? "");
                                    ApplyError("字段未匹配(" + preview.Replace("\r", " ").Replace("\n", " ") + ")");
                                }
                                else { cell.Ok = true; cell.Error = null; }
                            }
                        }
                    }
                    got = true;
                }
                _ = got;
            }
            catch (Exception ex) { cell.Ok = false; cell.Error = Shrink(ex.Message); }
            var rc = cell;
            return r => r.DynamicRows.Add(rc);
        }

        private async Task<Action<StatusReport>> CustomBalanceAsync()
        {
            string url = BalanceUrl;
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (!string.IsNullOrEmpty(BalanceToken))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BalanceToken);
                    if (!string.IsNullOrEmpty(BalanceHeader))
                    {
                        int c = BalanceHeader.IndexOf(':');
                        if (c > 0) req.Headers.TryAddWithoutValidation(
                            BalanceHeader.Substring(0, c).Trim(),
                            BalanceHeader.Substring(c + 1).Trim());
                    }
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                    {
                        string body = await resp.Content.ReadAsStringAsync();
                        double v = ParseJsonDouble(body, BalanceKey);
                        if (resp.IsSuccessStatusCode && !double.IsNaN(v))
                            return r => { r.BalanceOk = true; r.CustomValue = v; r.BalanceUnit = BalanceUnit; };
                        string err = resp.IsSuccessStatusCode
                            ? "字段[" + BalanceKey + "]缺失"
                            : DescribeHttpFailure((int)resp.StatusCode, body);
                        string e2 = err;
                        return r => r.Error = e2;
                    }
                }
            }
            catch (Exception ex)
            {
                string e2 = Shrink(ex.Message);
                return r => r.Error = e2;
            }
        }

        /// <summary>DeepSeek /user/balance：换算成剩余 token。</summary>
        private async Task<Action<StatusReport>> DeepSeekBalanceAsync()
        {
            if (string.IsNullOrEmpty(ApiKey)) return r => r.NoKey = true;
            string error = null;
            double bal = double.NaN;
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, "https://" + Host + "/user/balance"))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                    {
                        string body = await resp.Content.ReadAsStringAsync();
                        bal = ParseJsonDouble(body, "total_balance");
                        if (resp.IsSuccessStatusCode && !double.IsNaN(bal)
                            && (string.IsNullOrEmpty(body) || body.IndexOf("balance_infos", StringComparison.Ordinal) >= 0))
                            return r => { r.BalanceOk = true; r.BalanceCny = bal; r.RemainingTokens = (long)(bal * TokensPerYuan); };
                        error = resp.IsSuccessStatusCode ? "余额字段缺失" : DescribeHttpFailure((int)resp.StatusCode, body);
                    }
                }
            }
            catch (Exception ex) { error = Shrink(ex.Message); }
            var e2 = error;
            return r => r.Error = e2;
        }

        /// <summary>HTTP 失败时的错误文本（**纯函数**，可离线钉住）：状态码 ＋ 响应体摘要。
        /// ⚠ 2026-09-25 修的：此前所有失败分支只输出状态码（气泡上就是光秃秃的「401」），
        ///   于是「凭据过期」「缺请求头」「网关/WAF 拦截」三种完全不同的故障长得一模一样 ——
        ///   而区分它们的唯一线索（响应体里的 message）当场被丢掉了。
        ///   这与「成功但字段缺失时反而带 body preview」也不一致（同一个响应里两种待遇）。
        /// 阈值取绝对 120 字符（判据纪律 3）：截断要带绝对阈值，也避免把整页 HTML 灌进气泡。</summary>
        public static string DescribeHttpFailure(int status, string body)
        {
            string b = (body ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (b.Length == 0) return status.ToString(CultureInfo.InvariantCulture);
            if (b.Length > 120) b = b.Substring(0, 120) + "…";
            return status.ToString(CultureInfo.InvariantCulture) + "（" + b + "）";
        }

        private static string Shrink(string s)
        {
            return string.IsNullOrEmpty(s) ? "未知错误" : (s.Length > 60 ? s.Substring(0, 60) : s);
        }

        private static double ParseJsonDouble(string s, string key)
        {
            if (s == null) return double.NaN;
            int i = s.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return double.NaN;
            int c = s.IndexOf(':', i);
            if (c < 0) return double.NaN;
            int e = c + 1;
            while (e < s.Length && IsSpace(s[e])) e++;
            int end = e;
            while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.' || s[end] == '-' || s[end] == 'e' || s[end] == 'E')) end++;
            double d;
            return double.TryParse(s.Substring(e, Math.Max(0, end - e)), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : double.NaN;
        }

        private static bool IsSpace(char c)
        {
            return c == ' ' || c == '\t' || c == '\r' || c == '\n';
        }

        // ------------------------------------------------------------------ 渲染 / 序列化
        // 以下三件**气泡与 --statustest 共用同一个实现**。放这里而不是各写一份，
        // 免得「气泡显示什么」和「测试断言什么」两套口径慢慢跑偏（本项目的老毛病）。

        /// <summary>状态读数拆成**逐条气泡行**（网络一行 / 每路积分各自一行 / 每条动态源一行）。
        /// 给气泡流用：每条作为一条独立的气泡依次浮现。与 <see cref="Format"/> 同源（聚合口径，避免两套真值）。
        /// 原有精简纪律保留：连通性一行装下、积分一行一路只留数字、取证字段（Trae 已用/共、接口合计）不进气泡。</summary>
        public static List<string> FormatRows(StatusReport r)
        {
            var rows = new List<string>();
            if (r == null) { rows.Add("（无结果）"); return rows; }

            // ---- 连通性：一行装下（原来占三行）----
            rows.Add("网络 " + (r.Reachable ? "✅ " + r.LatencyMs + "ms" : "❌ 离线")
                     + " ｜ VPN " + (r.GoogleReachable ? "✅ " + r.GoogleLatencyMs + "ms" : "❌ 不通")
                     // ⚠ 隧道名**不进气泡**：实测「开(系统代理 127.0.0.1:7897)」会把一行顶到折行，
                     //   白白多占一行高度。气泡是「一眼看」的，要知道走哪个代理，去 --statustest 的 JSON 看 VpnName。
                     + " ｜ 隧道 " + (r.VpnTunnel ? "开" : "关"));

            // ---- 积分：一行一路，只留可用量 ----
            bool any = false;
            if (r.TraeOk) { rows.Add("Trae 积分 " + NumInt(r.TraeAvailable)); any = true; }
            else if (!string.IsNullOrEmpty(r.TraeError)) { rows.Add("Trae 积分 取不到（" + r.TraeError + "）"); any = true; }

            if (r.WorkbuddyOk)
            {
                // 主口径 = MainCreditType(1) 之和，已与 WorkBuddy 界面**同刻对照**
                // （2026-09-18：界面 1056 / 接口 1055，差 1 为活读数漂移）。
                rows.Add("WorkBuddy 积分 " + NumInt(r.WorkbuddyRemain));
                any = true;
            }
            else if (!string.IsNullOrEmpty(r.WorkbuddyError))
            {
                rows.Add("WorkBuddy 积分 取不到（" + r.WorkbuddyError + "）");
                any = true;
            }

            if (!any)   // 既没 Trae 也没 WorkBuddy 凭据 ⇒ 落到自定义接口 / DeepSeek 那条路
            {
                if (r.CustomValue >= 0) rows.Add("余额 " + r.CustomValue.ToString("0.##", CultureInfo.InvariantCulture) + r.BalanceUnit);
                else if (r.NoKey) rows.Add("余额 未配置 key");
                else if (r.BalanceOk) rows.Add("余额 ¥" + r.BalanceCny.ToString("0.00") + "（≈ " + ((double)r.RemainingTokens).ToString("N0") + " tokens）");
                else rows.Add("余额 取不到" + (string.IsNullOrEmpty(r.Error) ? "" : ("（" + r.Error + "）")));
            }

            // ---- 模板化自定义源：每一条一个通用行（balances.json）----
            foreach (var c in r.DynamicRows)
            {
                if (c.Ok)
                    rows.Add(c.Name + " " + Num(c.Value) + (string.IsNullOrEmpty(c.Unit) ? "" : " " + c.Unit));
                else if (!string.IsNullOrEmpty(c.Error))
                    rows.Add(c.Name + " 取不到（" + c.Error + "）");
            }
            return rows;
        }

        public static string Format(StatusReport r)
        {
            return string.Join("\r\n", FormatRows(r)) + "\r\n";
        }

        /// <summary>给 --statustest 用的 JSON。手写 —— 不为一个探针引序列化依赖。
        /// ⚠ 数字一律走 <see cref="JNum"/>（NaN → null，否则会写出非法的裸 `NaN`）；
        ///   字符串一律走 <see cref="JStr"/>（错误信息里常有引号/换行）。</summary>
        public static string ToJson(StatusReport r, Exception probeError)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"network\": {\"reachable\": ").Append(r.Reachable ? "true" : "false")
              .Append(", \"latency_ms\": ").Append(r.LatencyMs).Append("},\n");
            sb.Append("  \"vpn\": {\"google_reachable\": ").Append(r.GoogleReachable ? "true" : "false")
              .Append(", \"google_latency_ms\": ").Append(r.GoogleLatencyMs)
              .Append(", \"tunnel\": ").Append(r.VpnTunnel ? "true" : "false")
              .Append(", \"name\": ").Append(JStr(r.VpnName)).Append("},\n");
            sb.Append("  \"trae\": {\"ok\": ").Append(r.TraeOk ? "true" : "false")
              .Append(", \"available\": ").Append(JNum(r.TraeAvailable))
              .Append(", \"consumed\": ").Append(JNum(r.TraeConsumed))
              .Append(", \"total\": ").Append(JNum(r.TraeTotal))
              .Append(", \"error\": ").Append(JStr(r.TraeError)).Append("},\n");
            sb.Append("  \"workbuddy\": {\"ok\": ").Append(r.WorkbuddyOk ? "true" : "false")
              .Append(", \"remain\": ").Append(JNum(r.WorkbuddyRemain))
              .Append(", \"all_buckets_remain\": ").Append(JNum(r.WorkbuddyAllBucketsRemain))
              .Append(", \"type1_count\": ").Append(r.WorkbuddyType1Count)
              .Append(", \"account_count\": ").Append(r.WorkbuddyAccountCount)
              .Append(", \"total_count\": ").Append(r.WorkbuddyTotalCount)
              .Append(", \"other_remain\": ").Append(JNum(r.WorkbuddyOtherRemain))
              .Append(", \"error\": ").Append(JStr(r.WorkbuddyError)).Append("},\n");
            sb.Append("  \"fallback_balance\": {\"ok\": ").Append(r.BalanceOk ? "true" : "false")
              .Append(", \"no_key\": ").Append(r.NoKey ? "true" : "false")
              .Append(", \"custom_value\": ").Append(r.CustomValue >= 0 ? JNum(r.CustomValue) : "null")
              .Append(", \"unit\": ").Append(JStr(r.BalanceUnit))
              .Append(", \"balance_cny\": ").Append(JNum(r.BalanceCny))
              .Append(", \"remaining_tokens\": ").Append(r.RemainingTokens)
              .Append(", \"error\": ").Append(JStr(r.Error)).Append("},\n");
            sb.Append("  \"dynamic_balances\": [");
            for (int i = 0; i < r.DynamicRows.Count; i++)
            {
                var c = r.DynamicRows[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\": ").Append(JStr(c.Name))
                  .Append(", \"ok\": ").Append(c.Ok ? "true" : "false")
                  .Append(", \"value\": ").Append(JNum(c.Value))
                  .Append(", \"unit\": ").Append(JStr(c.Unit))
                  .Append(", \"error\": ").Append(JStr(c.Error)).Append('}');
            }
            sb.Append("],\n");
            var slots = SecretSlots();
            sb.Append("  \"secrets\": {\"trae_path\": ").Append(JStr(slots[0].path))
              .Append(", \"trae_exists\": ").Append(slots[0].exists ? "true" : "false")
              .Append(", \"workbuddy_path\": ").Append(JStr(slots[1].path))
              .Append(", \"workbuddy_exists\": ").Append(slots[1].exists ? "true" : "false").Append("},\n");
            sb.Append("  \"probe_error\": ").Append(JStr(probeError == null ? null : probeError.Message)).Append("\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        /// <summary>积分是整数语义，气泡上不显示小数（3617.66 → 3618）—— 精简用。</summary>
        private static string NumInt(double v) =>
            double.IsNaN(v) ? "—" : Math.Round(v).ToString("0", CultureInfo.InvariantCulture);

        /// <summary>JSON 数字：NaN / ∞ → null（裸 NaN 不是合法 JSON —— 本项目已两次产出非法 JSON）。</summary>
        private static string JNum(double v) =>
            double.IsNaN(v) || double.IsInfinity(v) ? "null" : v.ToString("0.####", CultureInfo.InvariantCulture);

        /// <summary>JSON 字符串：空 → null；引号、反斜杠、控制字符按 JSON 规则转义。</summary>
        private static string JStr(string s)
        {
            if (string.IsNullOrEmpty(s)) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append("\"").ToString();
        }
    }

    internal sealed class StatusReport
    {
        public bool Reachable;
        public int LatencyMs = -1;
        public bool GoogleReachable;             // google 能通（通常=正在走 VPN）
        public int GoogleLatencyMs = -1;         // 走 VPN 出口的延迟估计
        public bool VpnTunnel;                   // 系统里是否有活动 VPN 隧道网卡
        public string VpnName;                   // 检到的活动 VPN 网卡描述名
        public bool NoKey;                       // 没配 key → 不查余额
        public double BalanceCny = double.NaN;
        public double CustomValue = -1.0;            // 自定义接口的原始余额数值（>=0 表示有值）
        public string BalanceUnit = "";              // 自定义接口的显示单位，如 "积分"
        public bool BalanceOk;                   // 余额成功拿到
        public long RemainingTokens;
        public bool TraeOk;                      // TRAE 余额成功拿到
        public double TraeTotal = double.NaN;
        public double TraeConsumed = double.NaN;
        public double TraeAvailable = double.NaN;
        public string TraeError;                 // TRAE 侧错误（与 WorkBuddy 分开存 —— 共用一条会被后写的覆盖掉）
        public bool WorkbuddyOk;                      // WorkBuddy 积分成功拿到
        public double WorkbuddyRemain = double.NaN;   // 可用积分 = MainCreditType 的 CapacityRemain 之和（与界面同口径）
        // ⚠ 接口字段名就叫 TotalDosage，但它**不是消耗量**：实测随使用**下降**（1565→1562→1561→1555），
        //   且逐次都恰好等于「所有 type 的 CapacityRemain 之和」⇒ 它是**接口侧的全桶余额合计**。
        //   名字带 dosage 而被当成消耗量，是本项目「字段名标错 ⇒ 把正确读数读成错误结论」的又一例。
        //   ⇒ 本工程内部一律叫 AllBucketsRemain，除本注释外不再出现 Dosage 字样。
        public double WorkbuddyAllBucketsRemain = double.NaN;
        public int WorkbuddyType1Count;               // 参与求和的条数（0 条 = 口径失效，要报错、不许静默给 0）
        public int WorkbuddyAccountCount;             // 接口实际返回的 Accounts 条数
        public int WorkbuddyTotalCount = -1;          // 接口自称的总数；与返回条数不等 ⇒ 读到的可能不完整
        public double WorkbuddyOtherRemain = double.NaN;  // 非 type=1 的余额之和（只作旁证，不进主口径）
        public string WorkbuddyError;            // WorkBuddy 侧错误
        public string Error;                     // 余额侧的简短错误文本（自定义接口 / DeepSeek 那两条路）
        public List<BalanceCell> DynamicRows = new List<BalanceCell>();   // 模板化自定义源(balances.json)的一行一个
    }
}
