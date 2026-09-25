using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AzhuPet
{
    /// <summary>一次浏览器取数的结果。</summary>
    internal sealed class BrowserFetch
    {
        public bool Ok;                 // 拿到了 2xx
        public int Status = -1;         // -1 = 压根没拿到响应（导航失败 / 超时 / 异常）
        public string Body = "";        // **完整**响应体（要拿去解析余额，不是诊断预览）
        public string Why = "";         // 失败原因（人话，直接给用户看）
    }

    /// <summary>离屏浏览器取数：用桌宠自己那个 WebView2、在**页面上下文里**发一次目标请求。
    ///
    /// 为什么这是正路而不是歪路：本机直连四种传输（系统代理/直连 × HTTP/1.1/HTTP/2）全被网关
    /// 挡成 401，而同一个会话在浏览器里 200 —— 出口 IP、cookie（8 项逐项对齐）、请求头、method、
    /// body 全部排除之后，剩下的差异落在 .NET **复制不了**的那一层：TLS 指纹、HTTP/2
    /// SETTINGS、以及请求头顺序。与其继续伪造客户端，不如**直接用那个客户端**。
    ///
    /// ⚠ 四条硬约束（每条都是踩过才写下来的）：
    ///  ① **必须在界面线程上发起**（WebView2 是线程亲和的）。从别的线程进来会返回一句明确的
    ///     原因而不是静默失败 —— 用 <see cref="FetchSafeAsync"/> 自动切线程。
    ///  ② **和登录窗口共用一个 CoreWebView2Environment**（<see cref="EnvAsync"/>）。同一个
    ///     user-data-dir 起两个环境要互相踢；更要紧的是**登录态就在那个目录里**，换目录 = 没登录。
    ///     跨进程也会冲突（配置中心是独立进程打开时）⇒ 起不来时报「另一个窗口正占着浏览器数据目录」，
    ///     这是可解释的失败，不是坏掉。
    ///  ③ 窗口**离屏**（Left/Top = -4000）且不进任务栏、不抢焦点：取数是后台行为，不该让人看见。
    ///     离屏窗口会被 Chromium 当成「被遮挡」而节流，所以启动参数里关掉三种节流。
    ///  ④ 同一时刻只跑一个（<see cref="Gate"/>）：两个离屏窗口同时导航＝白烧两份内存。
    /// </summary>
    internal static class BrowserBalance
    {
        private static CoreWebView2Environment _env;
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        /// <summary>本机能不能走这条通道：需要有人（WPF 消息循环）能开窗口。
        /// 控制台模式（--statustest 等）没有界面线程 ⇒ 返回 false，调用方老老实实回落。</summary>
        public static bool Available
        {
            get
            {
                var app = Application.Current;
                return app != null && app.Dispatcher != null && !app.Dispatcher.HasShutdownStarted;
            }
        }

        /// <summary>共用的 WebView2 环境。**登录窗口也用这一个** —— 数据目录是同一个，
        /// 所以「用户登录过一次」这件事在取数时是天然继承的，不需要重新输密码。</summary>
        public static async Task<CoreWebView2Environment> EnvAsync()
        {
            if (_env != null) return _env;
            string dataDir = Path.Combine(StatusProbe.SecretDir(), "browser");
            Directory.CreateDirectory(dataDir);
            var opt = new CoreWebView2EnvironmentOptions
            {
                // 离屏窗口会被判为「被遮挡/后台」而节流定时器与渲染进程 ——
                // 取数本身不依赖渲染，但 throttling 会让页面脚本迟迟不跑。
                AdditionalBrowserArguments =
                    "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows"
            };
            _env = await CoreWebView2Environment.CreateAsync(null, dataDir, opt);
            return _env;
        }

        /// <summary>从任意线程发起（自动切到界面线程）。</summary>
        public static async Task<BrowserFetch> FetchSafeAsync(string pageUrl, string url, string method,
            string body, int timeoutMs)
        {
            var app = Application.Current;
            if (app == null || app.Dispatcher == null || app.Dispatcher.HasShutdownStarted)
                return new BrowserFetch { Why = "没有可用的界面线程（控制台模式），浏览器通道不可用" };
            if (app.Dispatcher.CheckAccess())
                return await FetchAsync(pageUrl, url, method, body, timeoutMs);
            try
            {
                // ⚠ 先切到界面线程**再**开始，之后的 await 都会回到界面线程（没人为改上下文）——
                //   这样每一处 WebView2 调用都天然落在正确的线程上，不用到处 Invoke。
                var op = app.Dispatcher.InvokeAsync(() => FetchAsync(pageUrl, url, method, body, timeoutMs));
                var inner = await op;
                return await inner;
            }
            catch (Exception ex) { return new BrowserFetch { Why = Short(ex.Message) }; }
        }

        /// <summary>在离屏窗口里取一次数。**必须在界面线程上调用**（见 <see cref="FetchSafeAsync"/>）。</summary>
        public static async Task<BrowserFetch> FetchAsync(string pageUrl, string url, string method,
            string body, int timeoutMs)
        {
            var r = new BrowserFetch();
            if (string.IsNullOrWhiteSpace(url)) { r.Why = "没有请求地址"; return r; }
            if (!Available) { r.Why = "没有可用的界面线程，浏览器通道不可用"; return r; }
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                // 宁可明确报错，也不要在一个错线程上开窗口 —— 那会以随机的形式炸在别处
                r.Why = "内部错误：浏览器取数必须在界面线程上发起";
                return r;
            }
            if (timeoutMs <= 0) timeoutMs = 15000;
            bool got = false;
            try { got = await Gate.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch { got = false; }
            if (!got) { r.Why = "另一个浏览器取数还在进行"; return r; }

            Window win = null; WebView2 view = null;
            try
            {
                string origin = OriginOf(url);
                var env = await EnvAsync();

                win = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    ResizeMode = ResizeMode.NoResize,
                    Width = 420, Height = 320,
                    Left = -4000, Top = -4000,     // 离屏：不打扰用户、也不占任何显示器
                    Title = "阿助取数（离屏）"
                };
                view = new WebView2();
                win.Content = view;
                win.Show();

                try { await view.EnsureCoreWebView2Async(env); }
                catch (Exception ex)
                {
                    // 最常见的失败是「同一个 user-data-dir 已经被另一个环境占着」（配置中心是独立
                    // 进程打开的，它会拿着这个目录）。这里只把**可能性**说出来，不假装知道原因 ——
                    // 从错误文案里嗅探具体成因，正是本项目反复吃亏的那种写法。
                    r.Why = "浏览器起不来：" + Short(ex.Message)
                          + "（若是「配置中心」或另一个登录窗口正开着，先关掉它再试）";
                    return r;
                }
                var core = view.CoreWebView2;
                if (core == null) { r.Why = "浏览器内核没起来"; return r; }
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;

                // 导航到**抄到请求时所在的那个页面**：fetch 只能从同源的文档里发出去。
                // 没记下来就退回站点根 —— 「能落在同源文档上」是这一发的前提，不是细节。
                string navUrl = !string.IsNullOrWhiteSpace(pageUrl) ? pageUrl.Trim() : origin + "/";
                var nav = new TaskCompletionSource<bool>();
                EventHandler<CoreWebView2NavigationCompletedEventArgs> h =
                    (s, e) => { try { nav.TrySetResult(e.IsSuccess); } catch { } };
                core.NavigationCompleted += h;
                core.Navigate(navUrl);
                var navDone = await Task.WhenAny(nav.Task, Task.Delay(timeoutMs));
                core.NavigationCompleted -= h;
                if (navDone != nav.Task) { r.Why = "打开 " + navUrl + " 超时"; return r; }
                if (!await nav.Task) { r.Why = "页面打不开：" + navUrl; return r; }

                // 落点必须和目标接口**同源**：跨源发 fetch 只会得到一个含糊的 CORS 报错，
                // 不如在这里就说清「浏览器落到了别的站点（多半是登录页）」，人一眼能懂。
                string landed = (await JsAsync(core, "location.origin")).TrimEnd('/');
                if (landed.Length > 0 && !string.Equals(landed, origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                {
                    r.Why = "浏览器落到了 " + landed + "，和目标接口不同源（多半是被跳到登录页了）。"
                          + "在「浏览器登录获取」窗口里把取数页面打开一次，再回来重试。";
                    return r;
                }

                await core.ExecuteScriptAsync(CredentialCapture.BuildFetchScript(url, method, body, "__azhuOffscreen"));
                for (int i = 0; i < 20; i++)          // 最多等 6 秒
                {
                    await Task.Delay(300);
                    string raw = await core.ExecuteScriptAsync("JSON.stringify(window.__azhuOffscreen||{})");
                    bool done; int st; string bd, er;
                    if (!CredentialCapture.ReadFetchState(CredentialCapture.DecodeJsString(raw), out done, out st, out bd, out er))
                        continue;
                    if (done) { r.Status = st; r.Body = bd ?? ""; r.Ok = st >= 200 && st < 300; return r; }
                    if (!string.IsNullOrEmpty(er)) { r.Why = "页面里发请求失败：" + Short(er); return r; }
                }
                r.Why = "等页面回话超时（接口可能一直没响应）";
                return r;
            }
            catch (Exception ex) { r.Why = Short(ex.Message); return r; }
            finally
            {
                try { if (win != null) win.Close(); } catch { }
                try { if (view != null) view.Dispose(); } catch { }
                Gate.Release();
            }
        }

        private static async Task<string> JsAsync(CoreWebView2 core, string js)
        {
            try { return CredentialCapture.DecodeJsString(await core.ExecuteScriptAsync(js)); }
            catch { return ""; }
        }

        private static string OriginOf(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out u)) return "";
            return u.Scheme + "://" + u.Authority;
        }

        private static string Short(string s)
        {
            string t = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length > 160 ? t.Substring(0, 160) + "…" : t;
        }
    }
}
