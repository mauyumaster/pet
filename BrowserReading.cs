using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace AzhuPet
{
    /// <summary>浏览器通道取到的那一次读数：**原始响应体 + 时刻**，落一个小文件。
    ///
    /// ⚠ 缓存里存的是**原始响应体**，不是解析出来的数字。理由：余额口径只能有一处
    ///   （<see cref="StatusProbe.ParseWorkbuddyJson"/>）。缓存若存数字，就多出来一个口径 ——
    ///   而「同一份数据两个口径」在本项目已经咬过人（method 段的两个解析器）。
    ///
    /// 为什么需要这个文件：本机直连被网关挡成 401，而同一个会话在浏览器里 200。浏览器通道能
    /// 取到真数据，但**开一次浏览器是有代价的**（一个 Chromium 实例）。于是分两层：
    ///   · 刚取到 → 直接用，并写在这里；
    ///   · 下次刷新时浏览器开不起来（运行时不在了 / 另一个进程占着 user-data-dir）→ 拿这份
    ///     有时刻的读数顶上，界面**标明来源与时刻**，而不是把「取不到」当成「没有」。
    /// </summary>
    internal static class BrowserReading
    {
        public const string FileName = "workbuddy_browser_reading.txt";

        /// <summary>这份缓存最多认多久。超过就宁可说「取不到」也不拿半天前的数字冒充当下 ——
        /// 余额是活读数，一个标着「昨天」的数字比一句「取不到」更容易误导。</summary>
        public const double MaxAgeSeconds = 1800;

        /// <summary>两次真开浏览器之间的最小间隔。设得**小于** StatusProbe.CacheSeconds(60)，
        /// 这样正常刷新（≥60 秒一次）永远不会被它挡，而「面板点一下 + 顺手又点一下桌宠」这类
        /// 挨得很近的重复请求不会连着开两个 Chromium。</summary>
        public const double ThrottleSeconds = 45;

        public static string Path_() { return System.IO.Path.Combine(StatusProbe.SecretDir(), FileName); }

        /// <summary>拼缓存正文（纯函数）。时刻一律用 UTC 的 ISO 8601（带 Z），跨时区/夏令时都不会读错。</summary>
        public static string Compose(DateTime atUtc, string url, string body)
        {
            var sb = new StringBuilder();
            sb.Append("at=").Append(atUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("url=").Append((url ?? "").Trim()).Append('\n');
            sb.Append("---body---").Append('\n').Append(body ?? "");
            return sb.ToString();
        }

        /// <summary>读缓存正文（纯函数）。**body 逐字节保留** —— 它是要拿去解析的 JSON，任何
        /// 「顺手规整一下行尾」都可能把一份本来合法的响应体改坏，而错处要到解析失败时才显形。
        /// 做法是按 `---body---` 之后**第一个换行的位置**整段切下来，而不是按行收集再拼回去
        /// （按行拼会吃掉 CR、还会给结尾补一个换行 —— 看上去无所谓，但那就不是「原样」了）。</summary>
        public static bool TryParse(string text, out DateTime atUtc, out string url, out string body)
        {
            atUtc = DateTime.MinValue; url = ""; body = "";
            if (string.IsNullOrEmpty(text)) return false;
            int mark = text.IndexOf("---body---", StringComparison.Ordinal);
            if (mark < 0) return false;
            int nl = text.IndexOf('\n', mark);
            body = nl < 0 ? "" : text.Substring(nl + 1);
            string head = text.Substring(0, mark);
            foreach (string raw in head.Split('\n'))
            {
                string t = raw.Trim();
                int eq = t.IndexOf('=');
                if (eq <= 0) continue;
                string k = t.Substring(0, eq).Trim().ToLowerInvariant();
                string v = t.Substring(eq + 1).Trim();
                if (k == "at")
                {
                    DateTime d;
                    if (DateTime.TryParse(v, CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                        atUtc = d;
                }
                else if (k == "url") url = v;
            }
            return atUtc != DateTime.MinValue && body.Length > 0;
        }

        /// <summary>这份读数还能不能当读数用（纯函数）。
        ///
        /// ⚠ 两条都算「不新鲜」，而且都不是洁癖：
        ///   · **来自未来**的一律不认（时钟被改过/时区算错时，一份「未来」的读数会让
        ///     `now - at` 永远是负的 ⇒ 它会**永远新鲜**，把过期数据洗成最新的）。
        ///   · maxAge ≤ 0 一律不认（那就等于「没有缓存」，不该意外变成「无限期有效」）。</summary>
        public static bool IsFresh(DateTime atUtc, DateTime nowUtc, double maxAgeSeconds)
        {
            if (atUtc == DateTime.MinValue) return false;
            if (maxAgeSeconds <= 0) return false;
            double age = (nowUtc - atUtc).TotalSeconds;
            if (age < -60) return false;          // 容 60 秒的时钟抖动，再多就是错的
            if (age < 0) age = 0;
            return age <= maxAgeSeconds;
        }

        /// <summary>把读数时刻说成人话（纯函数）：气泡里要能一眼看出「这个数字是什么时候的」。</summary>
        public static string DescribeAge(DateTime atUtc, DateTime nowUtc)
        {
            if (atUtc == DateTime.MinValue) return "时刻不明";
            double age = (nowUtc - atUtc).TotalSeconds;
            if (age < -60) return "时刻异常";
            if (age < 0) age = 0;
            if (age < 90) return "刚刚";
            if (age < 3600) return ((int)(age / 60)) + " 分钟前";
            return atUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        /// <summary>该不该**换到浏览器通道**（纯函数）。
        ///
        /// 判据只有两条，且各自有理由：
        ///   · 已经知道它管用（本会话成功过，或凭据里写着 via=browser）⇒ 直接走，省一次注定失败的直连；
        ///   · 原始通道**被网关挡下**（401/403，或响应体是网关的 HTML 错误页）⇒ 这就是要换通道的那种故障。
        /// ⚠ 超时/离线/应用层错误**不换**：那些情况浏览器同样救不了，白开一个 Chromium 只会让
        ///   「明明是断网」看起来像「凭据又坏了」。
        /// </summary>
        public static bool ShouldUseBrowser(int rawStatus, bool gatewayPage, bool knowBrowserWorks)
        {
            if (knowBrowserWorks) return true;
            if (rawStatus == 401 || rawStatus == 403) return true;
            return gatewayPage;
        }

        /// <summary>节流：距上次真开浏览器不足 ThrottleSeconds 就不再开一次（纯函数）。
        /// `secondsSinceLastTry &lt; 0` = 本次会话还没开过。`force` 供「面板上手动测一次」用。</summary>
        public static bool ThrottleAllows(double secondsSinceLastTry, bool force)
        {
            if (force) return true;
            if (secondsSinceLastTry < 0) return true;
            return secondsSinceLastTry >= ThrottleSeconds;
        }

        /// <summary>落盘（出错就吞掉：缓存写不进去绝不该让取数失败）。</summary>
        public static bool TrySave(DateTime atUtc, string url, string body)
        {
            try
            {
                Directory.CreateDirectory(StatusProbe.SecretDir());
                File.WriteAllText(Path_(), Compose(atUtc, url, body), new UTF8Encoding(false));
                return true;
            }
            catch { return false; }
        }

        /// <summary>读盘（读不到/格式不对都返回 false —— 判据在 TryParse 里，这里不重复判）。</summary>
        public static bool TryLoad(out DateTime atUtc, out string url, out string body)
        {
            atUtc = DateTime.MinValue; url = ""; body = "";
            try
            {
                string p = Path_();
                if (!File.Exists(p)) return false;
                return TryParse(File.ReadAllText(p), out atUtc, out url, out body);
            }
            catch { return false; }
        }
    }
}
