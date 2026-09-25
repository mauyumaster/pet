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
        private DateTime _openedAt;
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
            var close = SmallButton("关闭", (s, e) => Close());
            btns.Children.Add(_reload); btns.Children.Add(_manual); btns.Children.Add(close);
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
                string dataDir = Path.Combine(StatusProbe.SecretDir(), "browser");
                Directory.CreateDirectory(dataDir);
                var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
                await _view.EnsureCoreWebView2Async(env);

                var core = _view.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;      // 这个窗口的用途是登录，不是调试台
                core.Settings.IsStatusBarEnabled = false;
                await core.AddScriptToExecuteOnDocumentCreatedAsync(CredentialCapture.BuildHookScript(_capturePattern));

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
            string json = DecodeJsString(raw);
            if (string.IsNullOrEmpty(json)) { Nudge(); return; }
            var req = CredentialCapture.ParseCaptured(json);
            if (req == null || !CredentialCapture.IsTarget(req.Url, _capturePattern)) return;
            _timer.Stop();
            await FinishAsync(req, false);
        }

        /// <summary>等久了给点方向 —— 光转圈不解释，用户会以为卡死。</summary>
        private void Nudge()
        {
            double sec = (DateTime.UtcNow - _openedAt).TotalSeconds;
            if (sec < 20) return;
            SetState("还没看到余额请求，等你打开「计费 / 用量」页面…", Muted);
            _step.Text = "③ 如果登录后一直没反应：在页面里找到并打开「计费 / 用量 / 余额」页面；"
                + "或点右下角「我已登录，直接抓取」（那条路只换 cookie，请求头沿用旧凭据）。";
        }

        /// <summary>ExecuteScriptAsync 的返回值是一段 JSON；字符串结果会带一层引号。
        /// 解不出来就当空 —— 宁可多等一拍，也不要拿半截字符串去拼凭据。</summary>
        private static string DecodeJsString(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw == "null") return "";
            try { return JsonSerializer.Deserialize<string>(raw) ?? ""; }
            catch { return ""; }
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
                var jar = await _view.CoreWebView2.CookieManager.GetCookiesAsync(_defaultUrl);
                string cookieHeader = CredentialCapture.BuildCookieHeader(
                    (jar ?? new List<CoreWebView2Cookie>()).Select(c => new KeyValuePair<string, string>(c.Name, c.Value)));
                if (cookieHeader.Length == 0)
                {
                    Fail2("浏览器里没有 " + _platform + " 的 cookie —— 看起来还没登录成功。",
                        "请在下面把登录走完（能看到账号名/头像），再点「我已登录，直接抓取」。原凭据未改动。");
                    return;
                }

                // ---- 第 2 条腿：请求头。优先抄到的，其次沿用旧凭据，最后才是兜底模板 ----
                string oldPath = BalanceSources.ResolveSecret(_secretFile);
                if (oldPath != null && File.Exists(oldPath)) oldRaw = File.ReadAllText(oldPath);
                string oldUrl, oldBody;
                var oldHeaders = CredentialCapture.ParseRawSecretText(oldRaw, out oldUrl, out oldBody);

                List<KeyValuePair<string, string>> headers;
                string url, source, originNote = "";
                if (captured != null)
                {
                    headers = captured.Headers; url = captured.Url;
                    source = "抄到网站自己的请求（" + headers.Count + " 个头）";
                }
                else if (oldHeaders.Count > 0)
                {
                    headers = oldHeaders; url = string.IsNullOrWhiteSpace(oldUrl) ? _defaultUrl : oldUrl;
                    source = "沿用旧凭据的请求头，只换了 cookie";
                }
                else
                {
                    url = _defaultUrl;
                    headers = CredentialCapture.DerivedTemplate(url);
                    source = "兜底请求头";
                    originNote = " 这份是兜底模板（没有旧凭据可沿用）：origin 能算准，referer 只能猜，user-agent 给不了 —— 可能仍被网关拒绝。";
                }

                string composed = CredentialCapture.ComposeSecret(url, headers, cookieHeader, captured == null ? null : captured.Body);
                if (composed.Length == 0) { Fail2("组装失败：没有可用的请求地址。", "原凭据未改动。"); return; }

                // ---- 回测：真的去打一次余额接口 ----
                // ⚠ 顺序上是「先写文件再回测」：探针读数的唯一入口就是这个文件。所以失败要还原回去，
                //   否则用户原本（可能还能用）的凭据就被一次失败的尝试顶掉了。
                BalanceSources.SaveSecret(_secretFile, composed);
                int headerLines = composed.Split('\n').Count(l => l.Contains(": "));
                SetState("凭据已组装，正在回测真实接口…", Muted);
                _detail.Text = "来源：" + source + "；cookie " + cookieHeader.Split(';').Length + " 项，请求头 " + headerLines + " 行。"
                    + originNote;

                var rep = await new StatusProbe().CheckAsync();
                if (rep.WorkbuddyOk)
                {
                    _finished = true; Saved = true;
                    _timer?.Stop();
                    SetState("✓ 已获取并验证通过：" + _platform + " " + Math.Round(rep.WorkbuddyRemain).ToString("0") + " 积分", Good);
                    _detail.Text = "凭据已写入 " + BalanceSources.ResolveSecret(_secretFile) + "\n"
                        + "来源：" + source + "。以后 cookie 再过期时，回到这里点一下即可，通常不用重新输密码。";
                    _step.Text = "完成。可以关闭本窗口了。";
                    _afterSave?.Invoke();
                }
                else if (StatusProbe.LooksLikeCredentialProblem(rep.WorkbuddyStatus))
                {
                    if (oldRaw != null) BalanceSources.SaveSecret(_secretFile, oldRaw); else SafeDelete(_secretFile);
                    Fail2("服务器仍然拒绝这份凭据（" + rep.WorkbuddyError + "）",
                        "已把原凭据还原回去，你的文件没被改坏。常见原因：登录还没真正完成，或这个账号在这个浏览器里还没进过「计费」页面。");
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
