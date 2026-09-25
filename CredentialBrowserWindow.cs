// 「在面板里开个浏览器 → 登录 → 自动拿到凭据」。
//
// 本文件是**外壳**：只负责驱动 WebView2、把结果交给 CredentialCapture 与状态探针。
// 采集规则全在 CredentialCapture（纯逻辑，可被 --balanceconfigtest 离线钉住）——
// 哪天要换浏览器宿主（比如改成外挂系统 Edge），只该重写这一个文件，规则层原样搬。
//
// ⚠⚠ 关键一步想漏就会「取到了凭据但仍然 401」：**cookie 与请求头来自两个不同的地方**。
//   · 请求头：靠注入页面钩子抄页面自己发的那个请求（JS 能看见除 cookie 外的头）
//   · cookie：必须从宿主侧 CookieManager 取（JS 看不到 cookie 头，但宿主这侧能拿到 httpOnly 的 session）
//   两条腿缺一不可。见 CredentialCapture.HookTemplate 的注释。
//
// ⚠ 回测口径：写完凭据后**真的去打一次余额接口**（复用 StatusProbe.CheckAsync，与气泡同一个实现），
//   而不是「文件写成功了就报成功」。这是本窗口唯一有资格说「已获取」的理由。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AzhuPet
{
    internal sealed class CredentialBrowserWindow : Window
    {
        private static readonly Brush Bg = B(24, 26, 36), Panel = B(32, 35, 47), Panel2 = B(39, 43, 57);
        private static readonly Brush Muted = B(166, 174, 194), Accent = B(88, 132, 235);
        private static readonly Brush Good = B(82, 196, 132), Bad = B(238, 111, 111), Border = B(61, 66, 84);

        private readonly string _platform, _secretFile, _capturePattern, _loginUrl, _defaultUrl;
        private readonly Action _afterSave;
        private readonly TextBlock _status, _detail, _step;
        private readonly Button _manual, _reload;
        private WebView2 _view;
        private DispatcherTimer _timer;
        private DateTime _openedAt, _lastNudge = DateTime.MinValue;
        private bool _working, _finished;

        /// <summary>是否真的把凭据写进去了（宿主窗口据此刷新卡片状态）。</summary>
        public bool Saved { get; private set; }

        public CredentialBrowserWindow(string platform, string secretFile, string capturePattern,
            string loginUrl, string defaultUrl, Action afterSave)
        {
            _platform = platform ?? "凭据";
            _secretFile = secretFile;
            _capturePattern = capturePattern;
            _loginUrl = loginUrl;
            _defaultUrl = defaultUrl;
            _afterSave = afterSave;

            Title = "在浏览器中登录 · " + _platform;
            Width = 1000; Height = 760; MinWidth = 720; MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
            Topmost = true;   // 登录期间别被置顶的桌宠盖住
            Background = Bg; Foreground = Brushes.White; FontFamily = new FontFamily("Microsoft YaHei");

            var root = new Grid { Margin = new Thickness(18, 14, 18, 14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            head.Children.Add(new TextBlock { Text = "登录后自动获取凭据", FontSize = 20, FontWeight = FontWeights.SemiBold });
            _step = new TextBlock
            {
                Text = "① 在下面这个窗口里正常登录   ② 打开「计费 / 用量」页面（让网站自己发出余额请求）   ③ 凭据自动写入本机",
                Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap
            };
            head.Children.Add(_step);
            Grid.SetRow(head, 0); root.Children.Add(head);

            _view = new WebView2 { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            var host = new Border { Background = Panel, BorderBrush = Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7), Child = _view };
            Grid.SetRow(host, 1); root.Children.Add(host);

            var foot = new Border
            {
                Background = Panel, BorderBrush = Border, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(12, 9, 12, 9)
            };
            var fg = new Grid();
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _status = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Text = "准备中…", TextWrapping = TextWrapping.Wrap };
            _detail = new TextBlock { Foreground = Muted, FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
                Text = "凭据只写进本机，不会上传。这个窗口只是你机器上的一个浏览器。" };
            texts.Children.Add(_status); texts.Children.Add(_detail);
            fg.Children.Add(texts);
            var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _reload = SmallButton("重新加载", (s, e) => { try { _view.Source = new Uri(_loginUrl); } catch { } });
            _manual = SmallButton("我已登录，直接抓取", async (s, e) => await FinishAsync(null, true), true);
            var copy = SmallButton("复制状态", (s, e) => CopyState());
            var close = SmallButton("关闭", (s, e) => Close());
            btns.Children.Add(_reload); btns.Children.Add(_manual); btns.Children.Add(copy); btns.Children.Add(close);
            Grid.SetColumn(btns, 1); fg.Children.Add(btns);
            foot.Child = fg;
            Grid.SetRow(foot, 2); root.Children.Add(foot);
            Content = root;

            _manual.IsEnabled = false;
            _openedAt = DateTime.UtcNow;
            Loaded += async (s, e) => await InitAsync();
            Closed += OnClosed;
        }

        private static Brush B(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
        private Button SmallButton(string text, RoutedEventHandler click, bool primary = false)
        {
            var b = new Button
            {
                Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 5, 10, 5),
                Background = primary ? Accent : Panel2, Foreground = Brushes.White, BorderBrush = Border,
                BorderThickness = primary ? new Thickness(0) : new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            b.Click += click; return b;
        }

        /// <summary>初始化内嵌浏览器。⚠ 顺序有讲究：**先注入钩子，再导航** ——
        /// 反过来的话首页自己的脚本可能已经把余额请求发出去了，钩子装上时它已经跑完。</summary>
        private async Task InitAsync()
        {
            string ver;
            try { ver = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch (Exception ex)
            {
                // 框架依赖模式的老问题换了个马甲：终端用户没装 WebView2 运行时时，页面根本开不出来。
                // 硬报错比静默降级好 —— 否则用户只看到一个空白框，会以为是「桌宠坏了」。
                SetState("本机没有 WebView2 运行时，无法打开内嵌浏览器", Bad);
                _detail.Text = "装一次即可（Win11 通常已自带）：https://developer.microsoft.com/microsoft-edge/webview2/\n"
                    + "细节：" + ex.Message + "\n不使用这个窗口也不影响其它功能 —— 仍可在「更新凭据」里手工粘贴。";
                _manual.IsEnabled = false; _reload.IsEnabled = false;
                return;
            }
            try
            {
                // 浏览器数据放在我们自己的个人数据目录里（而不是 exe 同级的默认位置）：
                // ① 卸载器知道它在哪 ② 不往别人的目录里乱写 ③ 登录态能留住，cookie 下次过期时不必重新输密码。
                // ⚠ 环境走 BrowserBalance.EnvAsync()：**离屏取数器用的是同一个 user-data-dir**，
                //   同一个目录起两个环境会互相踢（跨进程也一样，配置中心是独立进程打开时就会撞上）。
                //   共用一个环境顺带保证「这里登录过」＝「取数时已登录」。
                var env = await BrowserBalance.EnvAsync();
                await _view.EnsureCoreWebView2Async(env);

                var core = _view.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;      // 这个窗口的用途是登录，不是调试台
                core.Settings.IsStatusBarEnabled = false;
                await core.AddScriptToExecuteOnDocumentCreatedAsync(CredentialCapture.BuildHookScript(_capturePattern));

                // ⚠⚠ 不接管这个事件时的默认行为是**开一个独立的 popup 窗口**（官方文档原话：
                //   "If either Handled or NewWindow properties are not set, the target content
                //    will be opened on a popup window."）。而钩子是挂在**本 WebView2** 上的 ——
                //   那个 popup 里的请求我们一个都抄不到。2026-09-25 用户报的现象正是这样：
                //   他在新窗口里看得到余额，这边却一直显示「还没看到余额请求」。
                core.NewWindowRequested += (s, e) => OnNewWindow(e);

                core.NavigationCompleted += (s, e) =>
                {
                    if (_finished) return;
                    if (e.IsSuccess) SetState("页面已加载，等你登录…", Muted);
                    else SetState("页面打不开：" + e.WebErrorStatus, Bad);   // 与「凭据错」分开报，别让用户去换凭据
                };
                core.SourceChanged += (s, e) =>
                {
                    if (_finished) return;
                    _detail.Text = "当前页面：" + core.Source + "\n凭据只写进本机，不会上传。";
                };

                _view.Source = new Uri(_loginUrl);
                _manual.IsEnabled = true;
                SetState("准备好（WebView2 " + ver + "），等你在下面登录", Muted);

                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _timer.Tick += OnTick;
                _timer.Start();
            }
            catch (Exception ex)
            {
                SetState("内嵌浏览器初始化失败", Bad);
                _detail.Text = ex.Message;
                _manual.IsEnabled = false; _reload.IsEnabled = false;
            }
        }

        /// <summary>轮询页面里那个全局变量。抄到了就收工 —— 只认第一个命中（钩子脚本同理）。</summary>
        private async void OnTick(object sender, EventArgs e)
        {
            if (_working || _finished || _view?.CoreWebView2 == null) return;
            string raw;
            try { raw = await _view.CoreWebView2.ExecuteScriptAsync("window.__azhuCapture||''"); }
            catch { return; }   // 导航过程中脚本宿主可能短暂不可用，下一拍再试，不当作错误
            string json = CredentialCapture.DecodeJsString(raw);
            if (string.IsNullOrEmpty(json)) { Nudge(); return; }
            var req = CredentialCapture.ParseCaptured(json);
            if (req == null || !CredentialCapture.IsTarget(req.Url, _capturePattern)) return;

            // ⚠ 「抄到」不等于「该收工」（2026-09-25 改）：__azhuCapture 在**第一个**命中时就非空了，
            //   而那个请求很可能是一次失败的（页面刚打开、会话还没就绪）。真正值得抄的是 2xx 那一份 ——
            //   钩子会在 2xx 时把它覆盖过来。所以这里只认 2xx；非 2xx 继续等，等满 20 秒再拿它交差：
            //   到那时它本身就是答案（**网站自己发这个请求也没通过**，那就是会话/路径的事，不是我们拼错）。
            if (req.Status >= 200 && req.Status < 300) { _timer.Stop(); await FinishAsync(req, false); return; }
            // 抄到的是一份「没成功」的样本。**要让用户看得见**（每 5 秒最多刷一次）：否则他会以为
            // 程序毫无反应，而实际上我们已经在盯着这个接口了 —— 「静默等待」是本项目反复吃亏的形态。
            if ((DateTime.UtcNow - _lastNudge).TotalSeconds >= 5)
            {
                _lastNudge = DateTime.UtcNow;
                SetState(req.Status == 0
                    ? "抄到了余额请求，还在等它的响应…"
                    : "抄到了余额请求，但网站自己发它也返回 " + req.Status + "，再等等看…", Brushes.Orange);
            }
            if ((DateTime.UtcNow - _openedAt).TotalSeconds < 20) return;
            _timer.Stop();
            await FinishAsync(req, false);
        }

        /// <summary>等久了给点方向 —— 光转圈不解释，用户会以为卡死。
        /// ⚠ 2026-09-25 起还会把「这个窗口发过哪些请求」列出来：只回一句「还没看到余额请求」
        ///   分不清是钩子没生效、页面不发 XHR、还是接口换了名字 —— 而那正是这一轮卡住的地方。</summary>
        private async void Nudge()
        {
            if ((DateTime.UtcNow - _openedAt).TotalSeconds < 20) return;
            if ((DateTime.UtcNow - _lastNudge).TotalSeconds < 5) return;   // 别每拍都去问页面一遍
            _lastNudge = DateTime.UtcNow;
            string seen = await ReadSeenAsync();
            if (_finished || _working) return;                             // 期间已经有结论了，别盖掉
            SetState("还没抄到余额请求", Muted);
            _step.Text = "③ 如果登录后一直没反应：在页面里找到并打开「计费 / 用量 / 余额」页面；"
                + "或点右下角「我已登录，直接抓取」（那条路只换 cookie，请求头沿用旧凭据）。";
            _detail.Text = seen.Length > 0
                ? "这个窗口里最近发出的请求（只到路径，不含网址参数）：\n" + seen
                  + "\n如果你已经在页面里看到余额了，点「复制状态」把这些行发给我，我就能对上接口。"
                : "这个窗口里还没记录到任何 fetch/XHR 请求。点「复制状态」可以把当前情况复制出来。";
        }

        /// <summary>站内新窗口拉回本窗口打开（原因见 InitAsync 里那段注释）。
        /// ⚠ 跨站的不动：第三方登录弹窗依赖 window.opener 通信，拉回来会把登录本身弄坏。
        /// ⚠ e.Uri 为空（window.open() 无地址、内容由脚本自己写的那种弹窗）也只能放行 —— 接管不了。</summary>
        private void OnNewWindow(CoreWebView2NewWindowRequestedEventArgs e)
        {
            try
            {
                if (_finished || string.IsNullOrWhiteSpace(e.Uri)) return;
                if (!CredentialCapture.IsSameSite(e.Uri, _loginUrl)) return;
                e.Handled = true;
                SetState("这个链接原本会另开一个窗口（那里抄不到请求），已拉回本窗口打开…", Muted);
                _view.CoreWebView2.Navigate(e.Uri);
            }
            catch { }
        }

        /// <summary>把状态区当前内容复制出来 —— 出问题时用户能一键把实况发给我，不用手抄。</summary>
        private void CopyState()
        {
            try { Clipboard.SetText(_status.Text + "\n" + (_detail.Text ?? "")); }
            catch { }
        }

        /// <summary>读钩子记下的请求路径（诊断用）。解不出来就当空 —— 宁可少说，也不显示半截东西。</summary>
        private async Task<string> ReadSeenAsync()
        {
            try
            {
                string raw = await _view.CoreWebView2.ExecuteScriptAsync("window.__azhuSeen||[]");
                if (string.IsNullOrEmpty(raw) || raw == "null") return "";
                return CredentialCapture.FormatSeenForUser(JsonSerializer.Deserialize<List<string>>(raw));
            }
            catch { return ""; }
        }

        /// <summary>在**页面上下文里**发一次目标接口 —— 它已经不只是诊断，而是**正式取数通道**。
        ///
        /// 为什么它成了正路：本机直连四种传输（系统代理/直连 × HTTP/1.1/HTTP/2）全被网关挡成
        /// 401，而同一会话在这里**三次都是 200 并带回真实数据**（TotalCount:29）。cookie（8 项
        /// 逐项对齐）、请求头、method、body、出口 IP 全部排除之后，剩下的差异落在 .NET 复制不了的
        /// 那一层（TLS/h2 指纹、请求头顺序）。与其继续伪造客户端，不如**用那个客户端**。
        /// 在此之前这段结果只写进日志就被丢掉了 —— 用户看到红色失败，而我们手里其实已经有答案。
        ///
        /// ⚠ 用「写一个全局状态位再轮询」，不用 await 返回值：ExecuteScriptAsync 对返回 Promise 的
        ///   表达式行为随运行时版本而变，而轮询与已有的 __azhuCapture 是同一套写法，稳。
        /// ⚠ 带回**完整**响应体（以前只带前 200 字符当证据）：它现在要拿去解析余额。
        ///   脚本本体在 CredentialCapture.BuildFetchScript 里，与离屏取数器**共用同一份实现**。
        /// </summary>
        private async Task<BrowserFetch> ReplayInBrowserAsync(string url, string method, string body)
        {
            var r = new BrowserFetch();
            try
            {
                await _view.CoreWebView2.ExecuteScriptAsync(
                    CredentialCapture.BuildFetchScript(url, method, body, "__azhuReplay"));
                for (int i = 0; i < 20; i++)   // 最多等 6 秒
                {
                    await Task.Delay(300);
                    string raw = await _view.CoreWebView2.ExecuteScriptAsync(
                        "JSON.stringify(window.__azhuReplay||{})");
                    bool done; int st; string bd, er;
                    if (!CredentialCapture.ReadFetchState(CredentialCapture.DecodeJsString(raw),
                            out done, out st, out bd, out er)) continue;
                    if (done) { r.Status = st; r.Body = bd ?? ""; r.Ok = st >= 200 && st < 300; return r; }
                    if (!string.IsNullOrEmpty(er)) { r.Why = "页面里发请求失败：" + OneLine(er); return r; }
                }
                r.Why = "超时（6 秒内页面没回话）";
                return r;
            }
            catch (Exception ex) { r.Why = OneLine(ex.Message); return r; }
        }

        /// <summary>把一次浏览器取数说成一行诊断文案（进日志，不进界面）。</summary>
        private static string DescribeReplay(BrowserFetch f)
        {
            if (f == null) return "浏览器内取数：未执行";
            if (f.Status >= 0)
                return "浏览器内取数 " + f.Status + (f.Body.Length > 0 ? "：" + OneLine(f.Body) : "");
            return "浏览器内取数失败：" + f.Why;
        }

        /// <summary>压成一行并截断（诊断文案用，纯函数式小工具）。</summary>
        private static string OneLine(string s)
        {
            string t = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length > 120 ? t.Substring(0, 120) + "…" : t;
        }

        /// <summary>组装并**回测**。captured == null 表示走「直接抓取」那条兜底路（只换 cookie）。</summary>
        private async Task FinishAsync(CapturedRequest captured, bool manual)
        {
            if (_working) return;
            _working = true;
            _manual.IsEnabled = false; _reload.IsEnabled = false;
            string oldRaw = null;
            try
            {
                SetState(manual ? "正在读取浏览器里的登录态…" : "抄到了网站自己的请求，正在组装凭据…", Muted);

                // ---- 第 1 条腿：cookie（必须从宿主侧取，JS 看不到）----
                // ⚠ 取的 URI 用**抄到的那个请求**（若同站）：cookie 是按 URI 的 host+path 匹配的，
                //   站点若把接口放在 app. 子域而登录页在 www.，拿 _defaultUrl 去问就会漏掉一部分。
                string cookieUrl = (captured != null && CredentialCapture.IsSameSite(captured.Url, _defaultUrl))
                    ? captured.Url : _defaultUrl;
                var jar = await _view.CoreWebView2.CookieManager.GetCookiesAsync(cookieUrl);
                var jarList = jar ?? new List<CoreWebView2Cookie>();
                string cookieHeader = CredentialCapture.BuildCookieHeader(
                    jarList.Select(c => new KeyValuePair<string, string>(c.Name, c.Value)));
                // ⚠ 另外把每条 cookie 的**名字 + Path** 记进日志（**不含值**）。同名 cookie 是按 Path 分成
                //   多条的，而服务端常常就靠 Path 分辨用途 —— 现场那条 `Set-Cookie: session_2=; Path=/billing`
                //   正是「要清除 /billing 那一条」的意思。日志里少了 Path 这一列，就永远看不出「要的那条在不在」。
                string cookieMap = jarList.Count == 0 ? "-" : string.Join(",",
                    jarList.Select(c => c.Name + "@" + (string.IsNullOrEmpty(c.Path) ? "/" : c.Path)
                        + "(" + (c.Value ?? "").Length + ")"));
                if (cookieHeader.Length == 0)
                {
                    Fail2("浏览器里没有 " + _platform + " 的 cookie —— 看起来还没登录成功。",
                        "请在下面把登录走完（能看到账号名/头像），再点「我已登录，直接抓取」。原凭据未改动。");
                    return;
                }

                // 旧凭据（可能为空）—— 它是「完整请求头」的唯一来源，抄到的那份天然缺浏览器自动头。
                string oldPath = BalanceSources.ResolveSecret(_secretFile);
                if (oldPath != null && File.Exists(oldPath)) oldRaw = File.ReadAllText(oldPath);
                string oldUrl, oldBody, oldMethod;
                var oldHeaders = CredentialCapture.ParseRawSecretText(oldRaw, out oldUrl, out oldBody, out oldMethod);

                // ---- 第 2 条腿：请求头。三条路**都走叠加重建**，不是二选一 ----
                // ⚠ 2026-09-25 修的事故：此前「抄到就用抄到的那份整份替换旧头」，而抄到的天然缺
                //   浏览器自动添加的那一套（user-agent / origin / referer / sec-fetch-* —— 按规范
                //   脚本既设不了也读不到）⇒ 回测请求连应用都没到，被 nginx 顶回 401，而页面上
                //   余额显示得好好的。现在统一 BuildFinalHeaders：旧头打底 + 抄到的覆盖 + 页面上下文补缺。
                string url = (captured != null && captured.Url.Length > 0)
                    ? captured.Url
                    : (!string.IsNullOrWhiteSpace(oldUrl) ? oldUrl : _defaultUrl);
                string source = captured != null
                    ? "抄到网站自己的请求（" + captured.Headers.Count + " 个头）＋ 旧凭据补齐浏览器自动头"
                    : (oldHeaders.Count > 0 ? "沿用旧凭据的请求头，只换了 cookie" : "兜底请求头（没有旧凭据可沿用）");
                var headers = CredentialCapture.BuildFinalHeaders(oldHeaders,
                    captured == null ? null : captured.Headers, url,
                    captured == null ? "" : captured.PageUserAgent,
                    captured == null ? "" : captured.PageHref,
                    captured == null ? "" : captured.PageLanguage,
                    captured == null ? "" : captured.PageChUa,
                    captured == null ? "" : captured.PageChUaMobile,
                    captured == null ? "" : captured.PageChUaPlatform);

                // ⚠ method 与 body 都是**抄到的事实**，必须一起落盘 —— 不落盘，回测就只能凭默认值说话，
                //   而实测 GET 同一个地址返回 404：方法一旦不对，连「地址不存在」和「没通过鉴权」都分不开。
                string composed = CredentialCapture.ComposeSecret(url, headers, cookieHeader,
                    captured == null ? null : captured.Body, captured == null ? null : captured.Method);
                if (composed.Length == 0) { Fail2("组装失败：没有可用的请求地址。", "原凭据未改动。"); return; }

                LogDiag("组装 source=" + source + " url=" + url
                    + " method=" + (captured == null ? "-" : captured.Method)
                    + " body=" + (captured == null || string.IsNullOrEmpty(captured.Body) ? "-" : captured.Body.Length + "字符")
                    + " chua=" + (captured == null || string.IsNullOrEmpty(captured.PageChUa) ? "-" : "有")
                    + " capStatus=" + (captured == null ? "-" : captured.Status.ToString())
                    + " page=" + (captured == null || captured.PageHref.Length == 0 ? "-" : captured.PageHref)
                    + " cookie=" + cookieHeader.Split(';').Length + "项"
                    + " jar=[" + cookieMap + "]"
                    + " old=[" + NamesOf(oldHeaders) + "]"
                    + " cap=[" + NamesOf(captured == null ? null : captured.Headers) + "]"
                    + " final=[" + NamesOf(headers) + "]");

                // ---- 浏览器自己的会话发一次：这已经不只是判据，而是**正式取数通道** ----
                // 为什么把它提到回测之前：本机直连四种传输（系统代理/直连 × HTTP/1.1/HTTP/2）全被网关
                // 挡成 401，而这一发**三次都是 200 并带回真实数据**（TotalCount:29）。它一旦成立，就
                // 没有理由再去撞那条注定失败的路 —— 而在此之前这段结果只写进日志就被丢掉：
                // 用户看到红色失败，我们手里其实已经握着答案（2026-09-25 卡住的那一轮）。
                SetState("正在用浏览器自己的会话取一次数…", Muted);
                var replay = await ReplayInBrowserAsync(url,
                    captured == null ? "POST" : captured.Method, captured == null ? null : captured.Body);
                LogDiag(DescribeReplay(replay));

                StatusProbe.WbCaliber? replayCal = replay.Ok
                    ? StatusProbe.ParseWorkbuddyJson(replay.Body) : (StatusProbe.WbCaliber?)null;
                if (replayCal.HasValue && replayCal.Value.Ok)
                {
                    // 把「哪条通道真取到过数」连同来源页一起写进凭据，并留下这次读数。
                    // ⚠ ---via--- 只在**成功过一次之后**才写：它记的是事实，不是偏好。写早了等于
                    //   把猜测固化成配置，下一次刷新会照着一个没验证过的通道走。
                    string viaText = CredentialCapture.ComposeSecret(url, headers, cookieHeader,
                        captured == null ? null : captured.Body, captured == null ? null : captured.Method,
                        CredentialCapture.ViaBrowser, captured == null ? null : captured.PageHref);
                    BalanceSources.SaveSecret(_secretFile, viaText.Length > 0 ? viaText : composed);
                    BrowserReading.TrySave(DateTime.UtcNow, url, replay.Body);
                    StatusProbe.NoteBrowserSuccess();   // 已知可用 + 顺手开节流：别紧接着再开一个 Chromium
                    _finished = true; Saved = true;
                    _timer?.Stop();
                    SetState("✓ 已取到：" + _platform + " " + Math.Round(replayCal.Value.Remain).ToString("0") + " 积分（浏览器通道）", Good);
                    _detail.Text = "凭据已写入 " + BalanceSources.ResolveSecret(_secretFile) + "\n"
                        + "这一发用的是**浏览器自己的会话**：本机直连（系统代理/直连 × HTTP/1.1/HTTP/2 四种都试过）"
                        + "被网关挡成 401，而同一个会话在浏览器里正常 —— cookie、请求头、method、body 已逐项"
                        + "对齐，剩下的差异落在脚本复制不了的那一层（TLS / HTTP/2 指纹、请求头顺序），"
                        + "所以从此改走浏览器通道。\n"
                        + "以后 cookie 再过期时，回到这里点一下即可，通常不用重新输密码。";
                    _step.Text = "完成。可以关闭本窗口了。";
                    LogDiag("浏览器通道取到 " + _platform + " = " + Math.Round(replayCal.Value.Remain).ToString("0")
                        + " status=" + replay.Status + " cookie=" + cookieHeader.Split(';').Length + "项");
                    _afterSave?.Invoke();
                    return;
                }

                // ---- 回测：真的去打一次余额接口 ----
                // ⚠ 顺序上是「先写文件再回测」：探针读数的唯一入口就是这个文件。所以失败要还原回去，
                //   否则用户原本（可能还能用）的凭据就被一次失败的尝试顶掉了。
                BalanceSources.SaveSecret(_secretFile, composed);
                int headerLines = composed.Split('\n').Count(l => l.Contains(": "));
                SetState("凭据已组装，正在回测真实接口…", Muted);
                _detail.Text = "来源：" + source + "；cookie " + cookieHeader.Split(';').Length + " 项，请求头 " + headerLines + " 行。";

                var rep = await new StatusProbe().CheckAsync();
                string note = StatusProbe.WbTransportNote;
                if (rep.WorkbuddyOk)
                {
                    _finished = true; Saved = true;
                    _timer?.Stop();
                    SetState("✓ 已获取并验证通过：" + _platform + " " + Math.Round(rep.WorkbuddyRemain).ToString("0") + " 积分", Good);
                    _detail.Text = "凭据已写入 " + BalanceSources.ResolveSecret(_secretFile) + "\n"
                        + "来源：" + source + "。以后 cookie 再过期时，回到这里点一下即可，通常不用重新输密码。"
                        // 传输方式被自检换过就得说出来 —— 悄悄换掉等于把「为什么以前不行」这个事实藏起来
                        + (note.Length > 0 ? "\n传输自检：" + note : "");
                    _step.Text = "完成。可以关闭本窗口了。";
                    LogDiag("回测通过 " + _platform + " = " + Math.Round(rep.WorkbuddyRemain).ToString("0")
                        + (note.Length > 0 ? " 传输自检=" + note : ""));
                    _afterSave?.Invoke();
                }
                else if (StatusProbe.LooksLikeCredentialProblem(rep.WorkbuddyStatus))
                {
                    if (oldRaw != null) BalanceSources.SaveSecret(_secretFile, oldRaw); else SafeDelete(_secretFile);
                    LogDiag("回测被拒 status=" + rep.WorkbuddyStatus + " cookie=" + cookieHeader.Split(';').Length
                        + "项 method=" + (captured == null ? "-" : captured.Method)
                        + " body=" + (captured == null || string.IsNullOrEmpty(captured.Body) ? "-" : captured.Body.Length + "字符")
                        + " final=[" + NamesOf(headers) + "] err=" + rep.WorkbuddyError
                        + (note.Length > 0 ? " 传输自检=" + note : ""));
                    string hint = CredentialCapture.DescribeCapturedStatus(captured == null ? 0 : captured.Status);
                    // 这一发是**浏览器自己发的**。连它也被拒 ⇒ 结论就不在「我们搬运丢了东西」这一层了：
                    // 这个浏览器里的登录态本身不成立。这句话必须说出来 —— 否则用户会一直去换凭据、
                    // 补请求头，而真正该做的是重新登录（2026-09-25 那一轮就卡在这里）。
                    string viaNote = (replay.Status == 401 || replay.Status == 403)
                        ? "\n⚠ 浏览器自己发这一发也被拒了（" + replay.Status + "）—— 说明这个浏览器里的登录态"
                          + "本身已经不成立（cookie 过期或被服务端清掉），而不是我们搬运时丢了东西。"
                          + "请在下面重新登录一次。"
                        : (replay.Status > 0
                            ? "\n浏览器自己发这一发是 " + replay.Status + "。"
                            : "\n浏览器自己发这一发没成功：" + replay.Why + "。");
                    Fail2("服务器仍然拒绝这份凭据（" + rep.WorkbuddyError + "）",
                        "已把原凭据还原回去，你的文件没被改坏。这次实际发出的是："
                        + cookieHeader.Split(';').Length + " 项 cookie，请求头 "
                        + string.Join("、", headers.Select(h => h.Key))
                        + "，方法 " + (captured == null ? "POST（默认）" : captured.Method)
                        + (captured != null && !string.IsNullOrEmpty(captured.Body)
                            ? "，body 按抄到的原样带上（" + captured.Body.Length + " 字符）"
                            : "，body 为空（发空 JSON）")
                        + "。"
                        + (hint.Length > 0 ? "\n" + hint : "")
                        + viaNote
                        + "\n" + DescribeReplay(replay)
                        + (note.Length > 0 ? "\n传输自检：" + note : "")
                        + "\n详细记录在 " + Path.Combine(StatusProbe.SecretDir(), "capture_log.txt"));
                }
                else
                {
                    _finished = true; Saved = true;
                    SetState("凭据已写入，但回测没做完：" + rep.WorkbuddyError, Brushes.Orange);
                    _detail.Text = "这看起来不是「凭据被拒」（更像网络或接口问题），所以新凭据保留了下来。"
                        + "等网络正常后点主面板的「测试全部」即可确认。";
                    _afterSave?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Fail2("获取失败：" + ex.Message, "原凭据未改动。");
            }
            finally
            {
                _working = false;
                if (!_finished) { _manual.IsEnabled = true; _reload.IsEnabled = true; }
            }
        }

        private void Fail2(string status, string detail) { SetState(status, Bad); _detail.Text = detail; }
        private void SetState(string text, Brush color) { _status.Text = text; _status.Foreground = color; }

        /// <summary>把一次采集的关键事实追加进日志。**只记头的名字与长度，绝不记值**（值里有 cookie）。
        /// ⚠ 为什么非要有它：2026-09-25 卡住时，界面上只有一句「服务器仍然拒绝」，而「钩子有没有命中、
        ///   抄到几个头、cookie 取到几项、最终发了哪些头」只有程序自己知道 —— 没有日志就只能靠推理。
        ///   这是本项目反复吃亏的那类事（判据被目录深度静默吃掉、401 只剩三个数字）。</summary>
        private static void LogDiag(string text)
        {
            try
            {
                File.AppendAllText(Path.Combine(StatusProbe.SecretDir(), "capture_log.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>头清单，形如 name(len),name(len) —— 只报名字与长度。</summary>
        private static string NamesOf(IEnumerable<KeyValuePair<string, string>> headers)
        {
            if (headers == null) return "-";
            return string.Join(",", headers.Select(h => (h.Key ?? "?") + "(" + ((h.Value ?? "").Length) + ")"));
        }

        private static void SafeDelete(string fileName)
        {
            try
            {
                string p = BalanceSources.ResolveSecret(fileName);
                if (p != null && File.Exists(p)) File.Delete(p);
            }
            catch { }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            try { _timer?.Stop(); } catch { }
            try { _view?.Dispose(); } catch { }   // 不 Dispose 会留下一个后台浏览器进程
        }
    }
}
