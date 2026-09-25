// 余额配置离线回归测试：只写系统临时目录，不联网、不碰真实配置和凭据。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AzhuPet
{
    internal static class BalanceConfigTest
    {
        public static int Run()
        {
            int pass = 0, fail = 0;
            string old = Environment.GetEnvironmentVariable("AZHU_SECRET_DIR");
            string dir = Path.Combine(Path.GetTempPath(), "AzhuPet-BalanceConfigTest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("AZHU_SECRET_DIR", dir);
            try
            {
                var source = new BalanceSource { Name = "示例余额", Url = "https://example.com/balance", Method = "GET", PathExpr = "data.remain", Unit = "积分", Enabled = true };
                Check(BalanceSources.Validate(source).Count == 0, "合法配置通过校验", ref pass, ref fail);
                BalanceSources.Save(new List<BalanceSource> { source }, BalanceSources.ConfigPath());
                Check(File.Exists(BalanceSources.ConfigPath()), "首次保存产生 balances.json", ref pass, ref fail);

                List<BalanceSource> loaded; string error;
                Check(BalanceSources.TryLoad(out loaded, out error) && loaded.Count == 1 && loaded[0].Name == "示例余额", "保存后可无损读回", ref pass, ref fail);

                source.Name = "修改后";
                BalanceSources.Save(new List<BalanceSource> { source }, BalanceSources.ConfigPath());
                Check(File.Exists(BalanceSources.ConfigPath() + ".bak"), "覆盖保存前生成 .bak", ref pass, ref fail);
                string backup = File.ReadAllText(BalanceSources.ConfigPath() + ".bak");
                using (var doc = JsonDocument.Parse(backup))
                    Check(doc.RootElement[0].GetProperty("Name").GetString() == "示例余额", "备份保留上一个版本", ref pass, ref fail);

                File.WriteAllText(BalanceSources.ConfigPath(), "{ broken json");
                Check(!BalanceSources.TryLoad(out loaded, out error) && !string.IsNullOrEmpty(error), "坏 JSON 显式报错", ref pass, ref fail);
                Check(File.ReadAllText(BalanceSources.ConfigPath()) == "{ broken json", "加载失败不覆盖原文件", ref pass, ref fail);

                BalanceSources.SaveSecret("custom-test.secret.txt", "authorization: test-only");
                Check(File.Exists(BalanceSources.ResolveSecret("custom-test.secret.txt")), "凭据只写首选本机目录", ref pass, ref fail);
                Check(BalanceSources.HasSensitiveInlineHeaders(new BalanceSource { HeadersText = "cookie: should-move" }), "识别误放在普通配置中的敏感头", ref pass, ref fail);
                Check(!BalanceSources.HasSensitiveInlineHeaders(new BalanceSource { HeadersText = "accept: application/json" }), "普通请求头不误报", ref pass, ref fail);

                // ---- HTTP 失败文案：401 必须能自证「为什么」----
                // ⚠ 2026-09-25：此前所有失败分支都只输出状态码（气泡上就是光秃秃的「401」），
                //   于是「凭据过期」「缺请求头」「网关拦截」长得一模一样 —— 区分它们的唯一线索
                //   在响应体里，而它当场被丢掉了。文案已抽成纯函数 StatusProbe.DescribeHttpFailure，
                //   这里不联网就能把这四条钉住（含「无响应体」「超长」「含换行」三种退化形状）。
                Check(StatusProbe.DescribeHttpFailure(401, @" {""code"":1001,""msg"":""unauthorized""}").Contains("unauthorized"),
                    "401 文案带出响应体线索（可区分过期/拦截）", ref pass, ref fail);
                Check(StatusProbe.DescribeHttpFailure(401, null) == "401 · 凭据可能已过期",
                    "401 直接点明「凭据可能已过期」（结论前置；空体不出现空括号）", ref pass, ref fail);
                Check(StatusProbe.DescribeHttpFailure(403, "a\r\nb").IndexOf('\n') < 0,
                    "响应体换行被压平（气泡只显示一行）", ref pass, ref fail);
                Check(StatusProbe.DescribeHttpFailure(403, "denied").StartsWith("403 · 凭据可能已过期", StringComparison.Ordinal),
                    "403 同样算凭据问题", ref pass, ref fail);
                Check(StatusProbe.DescribeHttpFailure(500, null) == "500",
                    "非凭据类且无响应体时退化为纯状态码", ref pass, ref fail);
                Check(StatusProbe.DescribeHttpFailure(404, "not found") == "404（not found）",
                    "负对照：404 不误报凭据问题（否则提示会变噪声）", ref pass, ref fail);
                Check(StatusProbe.LooksLikeCredentialProblem(401) && StatusProbe.LooksLikeCredentialProblem(403)
                    && !StatusProbe.LooksLikeCredentialProblem(500) && !StatusProbe.LooksLikeCredentialProblem(404),
                    "凭据问题的判据只认 401/403", ref pass, ref fail);
                Check(StatusProbe.DescribeHttpFailure(500, new string('x', 5000)).Length < 200,
                    "超长响应体被截断，不灌爆气泡", ref pass, ref fail);
                // 负对照（判据纪律 7：先证明它能失败）：旧行为只回状态码 ⇒ 带响应体时**必须**不再退化为纯「401」。
                Check(StatusProbe.DescribeHttpFailure(401, @" {""msg"":""unauthorized""}") != "401",
                    "负对照：带响应体时不再退化为纯「401」（旧行为在此判红）", ref pass, ref fail);

                // ---- 同样是 401，网关层与应用层要分家（修法相反）----
                // ⚠ 2026-09-25 用户现场：页面左侧明明白白显示着余额，回测却回 401 —— 而且响应体是
                //   nginx/APISIX 的 HTML 页。这说明请求**根本没到应用**，不是凭据失效。
                //   项目本来就写着这条口径（WorkBuddyBalanceAsync 的注释：「完整请求头需原样带上，
                //   否则 APISIX 网关会 401」），只是从来没让用户看到过。
                const string gwBody = "<html><head><title>401 Authorization Required</title></head><body><center><h1>401 Authorization Required</h1></center></body></html>";
                Check(StatusProbe.LooksLikeGatewayPage(gwBody), "认得网关拒绝页（HTML）", ref pass, ref fail);
                Check(StatusProbe.DescribeHttpFailure(401, gwBody).Contains("被网关挡下"),
                    "网关 401 直接说「请求没到应用，多半是请求头不完整」", ref pass, ref fail);
                Check(!StatusProbe.DescribeHttpFailure(401, gwBody).Contains("凭据可能已过期"),
                    "负对照：网关 401 不再误报成「凭据过期」（那会让用户白换一次凭据）", ref pass, ref fail);
                Check(StatusProbe.DescribeHttpFailure(401, @" {""code"":""TOKEN_EXPIRED""}").Contains("凭据可能已过期"),
                    "应用层的 JSON 401 仍然说「凭据可能已过期」", ref pass, ref fail);
                Check(!StatusProbe.LooksLikeGatewayPage(@" {""msg"":""<b>denied</b>""}"),
                    "负对照：JSON 响应体里带 HTML 片段不算整页网关页（否则会把过期误报成拦截）", ref pass, ref fail);
                Check(!StatusProbe.LooksLikeGatewayPage(null) && !StatusProbe.LooksLikeGatewayPage(""),
                    "负对照：空响应体不算网关页", ref pass, ref fail);

                // ---- 浏览器取凭据：采集规则（CredentialCapture）----
                // ⚠ 2026-09-25：把「面板里登录一次就拿到凭据」的**规则**从浏览器宿主里拆出来，
                //   唯一目的就是让它有资格被验 —— 真浏览器不会为了让我们验证而变形，
                //   规则若只能靠「点一次看看」来验，就等于没有判据（判据纪律 7：先证明它能失败）。
                string hook = CredentialCapture.BuildHookScript(CredentialCapture.WorkbuddyCapturePattern);
                Check(hook.Contains("\"/billing/meter/get-user-resource\""), "钩子里嵌入抓取目标（走 JSON 编码）", ref pass, ref fail);
                Check(!hook.Contains("__PATTERN__"), "占位符已被替换（没有残留）", ref pass, ref fail);
                Check(hook.Contains("__azhuSeen"),
                    "钩子会记录所有请求路径（诊断用；缺了就只能干说「还没看到余额请求」）", ref pass, ref fail);
                // ⚠ 这里**不断言转义成了哪种写法**：JsonSerializer 会把 " 编成 \u0022 而不是 \"，
                //   第一版判据就是写死了 \" 才红的 —— 那是在断言实现细节。真正要保证的性质是
                //   「PAT 那段字面量解回来必须等于原模式」，所以把那段摘出来当 JSON 解一遍。
                string nastyPattern = "a\"b\\c\nd";
                string nastyScript = CredentialCapture.BuildHookScript(nastyPattern);
                int at = nastyScript.IndexOf("PAT=", StringComparison.Ordinal) + 4;
                int semi = nastyScript.IndexOf(';', at);
                Check(JsonSerializer.Deserialize<string>(nastyScript.Substring(at, semi - at)) == nastyPattern,
                    "负对照：模式含引号/反斜杠/换行时，注入的仍是一段等值的 JS 字符串字面量", ref pass, ref fail);

                Check(CredentialCapture.IsTarget("https://www.workbuddy.cn/billing/meter/get-user-resource", CredentialCapture.WorkbuddyCapturePattern)
                    && CredentialCapture.IsTarget("https://WWW.WorkBuddy.CN/Billing/Meter/Get-User-Resource", CredentialCapture.WorkbuddyCapturePattern),
                    "抓取目标：子串匹配、忽略大小写", ref pass, ref fail);
                Check(!CredentialCapture.IsTarget("https://www.workbuddy.cn/billing/other", CredentialCapture.WorkbuddyCapturePattern)
                    && !CredentialCapture.IsTarget(null, CredentialCapture.WorkbuddyCapturePattern),
                    "负对照：别的接口不算命中（否则会抄错请求）", ref pass, ref fail);

                // ---- 同站判定：决定「站内新窗口要不要拉回本窗口」----
                // ⚠⚠ 2026-09-25 的现场故障：不接管 WebView2 的 NewWindowRequested 时，页面上点开
                //   的站内链接会**另开一个 popup 窗口**（官方文档：Handled / NewWindow 都没设就会
                //   开 popup）。而钩子只挂在原来那个 WebView2 上 ⇒ popup 里的请求一个都抄不到，
                //   现象是「用户在新窗口里看得到余额、程序一直说还没看到余额请求」。
                //   修法：同站的新窗口拉回本窗口；跨站的放行（第三方登录弹窗靠 window.opener 通信）。
                Check(CredentialCapture.IsSameSite("https://www.workbuddy.cn/billing/x", "https://www.workbuddy.cn/"),
                    "同域 → 同站（站内新窗口改在本窗口打开）", ref pass, ref fail);
                Check(CredentialCapture.IsSameSite("https://workbuddy.cn/x", "https://www.workbuddy.cn/"),
                    "裸域与 www 算同站", ref pass, ref fail);
                Check(CredentialCapture.IsSameSite("https://app.workbuddy.cn/x", "https://www.workbuddy.cn/"),
                    "兄弟子域算同站（工作台在 app. 子域上很常见；判成跨站就又开一个 popup，钩子白装）", ref pass, ref fail);
                Check(!CredentialCapture.IsSameSite("https://open.weixin.qq.com/x", "https://www.workbuddy.cn/"),
                    "负对照：第三方登录域不算同站（拉回来会把登录弹窗弄坏）", ref pass, ref fail);
                Check(!CredentialCapture.IsSameSite("https://evilworkbuddy.cn/x", "https://www.workbuddy.cn/"),
                    "负对照：evilworkbuddy.cn 不是同站（裸 EndsWith 会误判）", ref pass, ref fail);
                Check(!CredentialCapture.IsSameSite("https://a.com.cn/x", "https://b.com.cn/"),
                    "负对照：com.cn 这类二级后缀下，不同注册域不算同站（只取最后两段会误判）", ref pass, ref fail);
                Check(!CredentialCapture.IsSameSite("", "https://www.workbuddy.cn/")
                    && !CredentialCapture.IsSameSite("not-a-url", "https://www.workbuddy.cn/"),
                    "负对照：空/非法 URL 不算同站（否则会对空地址调 Navigate）", ref pass, ref fail);

                // ---- 诊断列表：抄不到时把「这个窗口发过哪些请求」说清楚 ----
                string seen = CredentialCapture.FormatSeenForUser(new[] { "/api/a", "/static/app.js", "/api/b", "/api/a" });
                Check(seen.Contains("/api/a") && seen.Contains("/api/b") && !seen.Contains(".js"),
                    "诊断列表：留接口路径、丢静态资源（否则真接口被挤出去）", ref pass, ref fail);
                Check(seen.Split('\n').Count(l => l.Contains("/api/a")) == 1,
                    "诊断列表去重", ref pass, ref fail);
                Check(CredentialCapture.FormatSeenForUser(Enumerable.Range(0, 30).Select(i => "/p" + i)).Split('\n').Length <= 8,
                    "诊断列表有上限（不灌满窗口）", ref pass, ref fail);
                Check(CredentialCapture.FormatSeenForUser(null) == "" && CredentialCapture.FormatSeenForUser(new string[0]) == "",
                    "负对照：没有请求时给空串（界面据此换另一句提示，而不是显示空标题）", ref pass, ref fail);

                // ---- 抄到的那份请求**自己**成不成功：401 分家的判据 ----
                // ⚠⚠ 2026-09-25 二修之后仍卡在 401 时才想通的那件事：此前从来没问过「网站自己发这个请求
                //   到底成功了没有」。钩子只抄**第一个**命中且不带响应码 ⇒ 抄到的可能是一次**失败**请求
                //   （页面刚打开、会话还没就绪），而页面上余额又确实显示着（那次成功的发得更晚，被
                //   「只记第一个命中」挡住了）。两种原因修法**相反**，而此前长得一模一样。
                Check(hook.Contains("status:0") && hook.Contains("loadend"),
                    "钩子给请求留了状态码位，且 fetch 与 XHR 两条路都会回填它", ref pass, ref fail);
                Check(hook.Contains("__azhuCapObj"),
                    "钩子记住「谁占着定稿位」—— 这样 2xx 才能抢走它，且已成功的定稿不会被后来的失败请求降级",
                    ref pass, ref fail);

                string hitJson = "{\"url\":\"https://www.workbuddy.cn/billing/meter/get-user-resource\","
                    + "\"method\":\"POST\",\"headers\":{\"x-user-id\":\"u\"},\"body\":\"{}\",\"ua\":\"UA\","
                    + "\"href\":\"https://www.workbuddy.cn/dashboard\",\"org\":\"https://www.workbuddy.cn\","
                    + "\"lang\":\"zh-CN\",\"status\":200}";
                var capOk = CredentialCapture.ParseCaptured(hitJson);
                Check(capOk != null && capOk.Status == 200 && capOk.Method == "POST",
                    "抄回的请求带着它自己的响应码与真实方法（不再把 method 丢掉）", ref pass, ref fail);
                Check(capOk != null && capOk.PageHref == "https://www.workbuddy.cn/dashboard" && capOk.PageUserAgent == "UA",
                    "抄回的请求带着页面上下文（补浏览器自动添加的那套头的依据）", ref pass, ref fail);

                // 负对照：status 缺席（老版钩子写出来的 JSON）必须退回 0 ＝「还不知道」，**不是**「失败」。
                // 否则会把「响应还没回来」误报成「网站自己也被拒」，把用户往错的方向指。
                var capNoStatus = CredentialCapture.ParseCaptured(
                    "{\"url\":\"https://www.workbuddy.cn/x\",\"method\":\"GET\",\"headers\":{}}");
                Check(capNoStatus != null && capNoStatus.Status == 0,
                    "负对照：JSON 里没有 status ⇒ 0（还不知道），不当作失败", ref pass, ref fail);

                Check(CredentialCapture.DescribeCapturedStatus(200).Contains("成功"),
                    "2xx ⇒ 告诉用户「网站自己发这个请求是成功的」（那问题在搬运）", ref pass, ref fail);
                string notOk = CredentialCapture.DescribeCapturedStatus(401);
                Check(notOk.Contains("401") && notOk.Contains("会话"),
                    "4xx ⇒ 告诉用户「网站自己发这个请求也 401」（那问题在会话，换凭据、补头都没用）",
                    ref pass, ref fail);
                Check(CredentialCapture.DescribeCapturedStatus(0) == "",
                    "负对照：status=0 一句结论都不下（宁可不说，也不把人往错方向指）", ref pass, ref fail);

                // ---- 请求头合并：抄到的头**天然缺**浏览器自动添加的那一套 ----
                // ⚠⚠ 2026-09-25 的现场故障（用户截图：页面左侧明明白白显示着余额，回测却被网关顶回 401）：
                //   此前是**二选一** —— 抄到了就用抄到的那份**整份替换**旧头。但 user-agent / accept /
                //   accept-language / origin / referer / sec-fetch-* 是浏览器**自动添加**的头，按规范
                //   脚本既设不了也读不到（forbidden header names）⇒「抄到的那份」必然缺它们 ⇒ 请求
                //   连应用都没到，被 nginx 直接拒（响应体是 HTML 错误页而不是 API 的 JSON）。
                //   实测旧凭据那 11 个头里这些**全都有**（09-18 从真浏览器抓的）—— 那才是稀缺的东西。
                //   修法：旧头打底 + 抄到的覆盖 + 页面上下文补缺，三层叠加。
                const string WbApi = "https://www.workbuddy.cn/billing/meter/get-user-resource";
                var oldH = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("user-agent", "Mozilla/5.0 (Real) Edg/153"),
                    new KeyValuePair<string, string>("accept", "application/json"),
                    new KeyValuePair<string, string>("accept-language", "zh"),
                    new KeyValuePair<string, string>("origin", "https://www.workbuddy.cn"),
                    new KeyValuePair<string, string>("referer", "https://www.workbuddy.cn/app"),
                    new KeyValuePair<string, string>("x-user-id", "OLD-ID"),
                    new KeyValuePair<string, string>("sec-fetch-dest", "empty"),
                    new KeyValuePair<string, string>("sec-fetch-mode", "cors"),
                    new KeyValuePair<string, string>("sec-fetch-site", "same-origin"),
                    new KeyValuePair<string, string>("content-type", "application/json"),
                    new KeyValuePair<string, string>("cookie", "session=STALE"),
                };
                var capH = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("content-type", "application/json;charset=utf-8"),
                    new KeyValuePair<string, string>("x-user-id", "NEW-ID"),
                };
                Func<List<KeyValuePair<string, string>>, string, string> hv = (list, n) =>
                {
                    var one = list.FirstOrDefault(h => string.Equals(h.Key, n, StringComparison.OrdinalIgnoreCase));
                    return one.Key == null ? null : one.Value;
                };
                Func<List<KeyValuePair<string, string>>, string, int> hc = (list, n) =>
                    list.Count(h => string.Equals(h.Key, n, StringComparison.OrdinalIgnoreCase));

                var mergedH = CredentialCapture.BuildFinalHeaders(oldH, capH, WbApi,
                    "Mozilla/5.0 (Page) Edg/154", "https://www.workbuddy.cn/console", "zh-CN");
                Check(hv(mergedH, "user-agent") == "Mozilla/5.0 (Real) Edg/153",
                    "旧凭据的 user-agent 被保住 —— 抄到的头里根本没有它（这条就是那次 401 的根因）", ref pass, ref fail);
                Check(hv(mergedH, "sec-fetch-site") == "same-origin" && hv(mergedH, "referer") != null && hv(mergedH, "accept") != null,
                    "旧凭据的 sec-fetch-* / referer / accept 被保住（脚本读不到这些）", ref pass, ref fail);
                Check(hv(mergedH, "x-user-id") == "NEW-ID",
                    "抄到的头覆盖同名旧头（拿到最新账号标识）", ref pass, ref fail);
                Check(hv(mergedH, "content-type") == "application/json;charset=utf-8",
                    "抄到的 content-type 覆盖旧值（以页面实际发出的为准）", ref pass, ref fail);
                Check(hc(mergedH, "x-user-id") == 1 && hc(mergedH, "content-type") == 1,
                    "同名头只留一份（两份会让服务器无所适从）", ref pass, ref fail);
                Check(hc(mergedH, "cookie") == 0,
                    "cookie 不进合并结果（由 cookieHeader 单独重建，旧的过期 cookie 混不进来）", ref pass, ref fail);
                Check(hc(mergedH, "host") == 0 && hc(mergedH, "content-length") == 0,
                    "传输层头不参与合并（host / content-length 交 HttpClient）", ref pass, ref fail);

                // 负对照（判据纪律 7）：合并**不是**「无脑补一个 user-agent」——
                // 既没旧凭据、页面也没给 UA 时，结果里就该没有它（编一个假的比缺更糟）。
                var bareH = CredentialCapture.BuildFinalHeaders(null, capH, WbApi, "", "", "");
                Check(hc(bareH, "user-agent") == 0,
                    "负对照：没有任何 UA 来源时不编造 user-agent", ref pass, ref fail);
                // 另一半：有页面上下文时必须补上 —— 这才是「没有旧凭据」时的真值来源
                var pageH = CredentialCapture.BuildFinalHeaders(null, capH, WbApi,
                    "Mozilla/5.0 (Page) Edg/154", "https://www.workbuddy.cn/console", "zh-CN");
                Check(hv(pageH, "user-agent") == "Mozilla/5.0 (Page) Edg/154",
                    "没有旧凭据时用 navigator.userAgent 补上（页面读到的真值，不是猜的）", ref pass, ref fail);
                Check(hv(pageH, "origin") == "https://www.workbuddy.cn" && hv(pageH, "referer") == "https://www.workbuddy.cn/console",
                    "origin 由目标地址算出、referer 用当前页（同源）", ref pass, ref fail);
                Check(hv(pageH, "accept-language") == "zh-CN,zh;q=0.9",
                    "accept-language 由 navigator.language 推出", ref pass, ref fail);

                // 跨源：旧凭据里的 origin / referer 不能被照搬到别的站点上
                var crossH = CredentialCapture.BuildFinalHeaders(new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("origin", "https://evil.example"),
                    new KeyValuePair<string, string>("referer", "https://evil.example/x"),
                    new KeyValuePair<string, string>("user-agent", "UA"),
                }, null, WbApi, "", "", "");
                Check(hv(crossH, "origin") == "https://www.workbuddy.cn" && hv(crossH, "referer") == "https://www.workbuddy.cn/",
                    "负对照：旧凭据里别的站点的 origin/referer 不被沿用（回落目标站点）", ref pass, ref fail);
                Check(hv(crossH, "user-agent") == "UA",
                    "但 user-agent 与站点无关，照旧保留", ref pass, ref fail);

                // 三层都空：回落骨架模板（而不是给一份空头）
                var noneH = CredentialCapture.BuildFinalHeaders(null, null, WbApi, "", "", "");
                Check(hv(noneH, "accept") != null && hv(noneH, "origin") == "https://www.workbuddy.cn",
                    "三层都空时回落骨架模板（至少 origin 算得准）", ref pass, ref fail);
                // 负对照：目标地址不是绝对地址时不编造 origin —— 宁可缺，也不要一个假的来源
                var relH = CredentialCapture.BuildFinalHeaders(null, capH, "/billing/meter/get-user-resource", "UA", "", "");
                Check(hc(relH, "origin") == 0,
                    "负对照：目标地址非绝对时不编造 origin（宁可缺，也不要假的来源）", ref pass, ref fail);

                Check(CredentialCapture.IsDroppedHeader("Content-Length") && CredentialCapture.IsDroppedHeader("acCEPT-encoding")
                    && CredentialCapture.IsDroppedHeader("cookie") && CredentialCapture.IsDroppedHeader("Host"),
                    "传输层头与 cookie 一律丢弃（大小写无关）", ref pass, ref fail);
                Check(!CredentialCapture.IsDroppedHeader("content-type") && !CredentialCapture.IsDroppedHeader("x-user-id"),
                    "负对照：content-type / x-user-id 不许被丢掉", ref pass, ref fail);

                var jar = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("session_2", "bbb"),
                    new KeyValuePair<string, string>("session", "aaa"),
                    new KeyValuePair<string, string>("", "空名应被丢弃"),
                };
                Check(CredentialCapture.BuildCookieHeader(jar) == "session=aaa; session_2=bbb",
                    "cookie 头按名字排序拼接（连点两次产出逐字节相同，可 diff）", ref pass, ref fail);

                var cap = CredentialCapture.ParseCaptured(@"{
                    ""url"": ""https://www.workbuddy.cn/billing/meter/get-user-resource"",
                    ""method"": ""POST"",
                    ""headers"": { ""content-type"": ""application/json"", ""x-user-id"": ""abc"", ""content-length"": ""2"" },
                    ""body"": ""{}"" }");
                Check(cap != null && cap.Method == "POST" && cap.Headers.Count == 3
                    && cap.Headers.Any(h => h.Key == "x-user-id" && h.Value == "abc"),
                    "解析抄到的请求（URL / 方法 / 头 / body）", ref pass, ref fail);
                Check(CredentialCapture.ParseCaptured("\"just-a-string\"") == null
                    && CredentialCapture.ParseCaptured("{ broken") == null
                    && CredentialCapture.ParseCaptured("{}") == null,
                    "负对照：非对象／坏 JSON／无 URL 一律返回 null 而不是抛（TryGetProperty 对非 Object 会抛）", ref pass, ref fail);

                // 客户端提示与状态码是一起从浏览器那圈 JSON 里带回来的。
                // ⚠ 这里刻意让值里**带双引号**（sec-ch-ua 的真实形态就是 `"Chromium";v="140"`）：
                //   钩子那头是 JSON.stringify，C# 这头是 JsonDocument —— 转义错一格就会静默变空。
                string hitHint = "{\"url\":" + JsonSerializer.Serialize(WbApi)
                    + ",\"chua\":" + JsonSerializer.Serialize("\"Chromium\";v=\"140\"")
                    + ",\"chuam\":\"?0\",\"chuap\":" + JsonSerializer.Serialize("\"Windows\"")
                    + ",\"status\":200}";
                var capHint = CredentialCapture.ParseCaptured(hitHint);
                Check(capHint != null && capHint.PageChUa == "\"Chromium\";v=\"140\""
                    && capHint.PageChUaMobile == "?0" && capHint.PageChUaPlatform == "\"Windows\""
                    && capHint.Status == 200,
                    "解析抄到的请求时带出三项客户端提示与状态码（值里带引号也能过 JSON 这一圈）", ref pass, ref fail);
                Check(CredentialCapture.ParseCaptured("{\"url\":\"https://x/y\",\"chua\":123}").PageChUa == "",
                    "负对照：客户端提示不是字符串时按空处理，不抛", ref pass, ref fail);

                // 拼出来的文件必须能被**正式读取路径**读回去 —— 同一份数据不许有两个解析口径。
                BalanceSources.SaveSecret("wb-compose-test.secret.txt", CredentialCapture.ComposeSecret(
                    cap.Url, cap.Headers, CredentialCapture.BuildCookieHeader(jar), cap.Body));
                var rt = BalanceSources.ReadSecret("wb-compose-test.secret.txt");
                Check(rt.headers.Split('\n').Any(l => l.StartsWith("cookie: session=aaa; session_2=bbb", StringComparison.Ordinal))
                    && rt.headers.Split('\n').Any(l => l.StartsWith("x-user-id: abc", StringComparison.Ordinal)),
                    "拼出的凭据能被正式读取路径读回（cookie 与 x-user-id 都在）", ref pass, ref fail);
                Check(!rt.headers.Contains("content-length") && !rt.headers.Contains("accept-encoding"),
                    "传输层头没有写进文件", ref pass, ref fail);
                Check(rt.body == "{}", "body 段被读取路径识别", ref pass, ref fail);

                // 负对照（判据纪律 7）：不拼 cookie 罐 → 文件里就不该有 cookie 行。
                // 这正是最危险的错法：JS 看不到 cookie 头，只抄请求头会得到一份
                // 「看起来完整、其实没有登录态」的凭据，然后现象是 401。
                string noCookie = CredentialCapture.ComposeSecret(cap.Url, cap.Headers, "", null);
                Check(!noCookie.Contains("cookie:"),
                    "负对照：不拼 cookie 罐时文件里没有 cookie 行（否则上面那条判据不成立）", ref pass, ref fail);
                Check(noCookie.Split('\n')[0].Trim() == cap.Url, "URL 恒在第一行（读取路径靠这个约定）", ref pass, ref fail);
                Check(CredentialCapture.ComposeSecret("", cap.Headers, "a=1", null) == "",
                    "没有 URL 时宁可不写（返回空串，不产生半截凭据）", ref pass, ref fail);

                var tpl = CredentialCapture.DerivedTemplate("https://www.workbuddy.cn/billing/meter/get-user-resource");
                Check(tpl.Any(h => h.Key == "origin" && h.Value == "https://www.workbuddy.cn") && tpl.Any(h => h.Key == "referer"),
                    "兜底模板的 origin 由 URL 算出", ref pass, ref fail);
                Check(CredentialCapture.DerivedTemplate("not-a-url").Count == 0, "负对照：非法 URL 不产出模板", ref pass, ref fail);

                // 「直接抓取」那条路：沿用旧凭据的请求头，只把 cookie 换掉。
                string oldText = "https://www.workbuddy.cn/billing/meter/get-user-resource\r\nuser-agent: UA\r\ncookie: old=1\r\nx-user-id: abc\r\n";
                string oldUrl, oldBody, oldMethod;
                var oldHeaders = CredentialCapture.ParseRawSecretText(oldText, out oldUrl, out oldBody, out oldMethod);
                Check(oldMethod == null,
                    "负对照：旧格式（没有 ---method--- 段）读出的方法是 null，不是替它猜一个 POST 出来", ref pass, ref fail);
                Check(oldUrl == "https://www.workbuddy.cn/billing/meter/get-user-resource" && oldHeaders.Count == 3
                    && oldHeaders.Any(h => h.Key == "x-user-id" && h.Value == "abc"),
                    "能拆出旧凭据的 URL 与请求头（只换 cookie 那条路靠它）", ref pass, ref fail);
                string replayed = CredentialCapture.ComposeSecret(oldUrl, oldHeaders, "session=new; session_2=new2", null);
                Check(replayed.Contains("user-agent: UA") && replayed.Contains("x-user-id: abc")
                    && replayed.Contains("session=new; session_2=new2") && !replayed.Contains("old=1"),
                    "旧 cookie 被新 cookie 顶掉，其它头原样保留（不出现两个 cookie 行）", ref pass, ref fail);

                // ---- method / body：抄到的事实必须原样落盘、原样发出去 ----
                // 2026-09-25 现场：这两段**从来没被发出去过**（回测写死 POST + 空 body `{}`），
                // 而实测 GET 同一个地址返回 404 —— 方法错一格，连「地址不存在」和「没通过鉴权」都分不开。
                string withMethod = CredentialCapture.ComposeSecret(cap.Url, cap.Headers, "session=a", "{\"PageSize\":20}", "POST");
                string rUrl2, rBody2, rMethod2;
                var rHeaders2 = CredentialCapture.ParseRawSecretText(withMethod, out rUrl2, out rBody2, out rMethod2);
                Check(rMethod2 == "POST" && rUrl2 == cap.Url, "method 段落盘后能读回（URL 仍在第一行）", ref pass, ref fail);
                Check(rBody2 == "{\"PageSize\":20}", "body 段落盘后能读回", ref pass, ref fail);
                // ⚠ 必须同时钉死「头还在」：`All()` 在空列表上**恒为真** —— 只写 All 的话，
                //   「method 段把后面的请求头整段吞掉」这个 bug 照样全绿（本轮就真的这么空过一次）。
                Check(rHeaders2.Count == 3 && rHeaders2.Any(h => h.Key == "content-type")
                    && rHeaders2.Any(h => h.Key == "x-user-id" && h.Value == "abc")
                    && rHeaders2.Any(h => h.Key == "cookie"),
                    "method 段不吞掉后面的请求头（content-type / x-user-id / cookie 都还在）", ref pass, ref fail);
                Check(rHeaders2.All(h => !h.Key.Contains("{") && !h.Key.Contains("}")
                        && h.Key.IndexOf("PageSize", StringComparison.OrdinalIgnoreCase) < 0),
                    "body 那行 JSON 不会被当成请求头（解析器必须认识 ---body--- 段）", ref pass, ref fail);
                Check(rHeaders2.All(h => !h.Key.Equals("POST", StringComparison.OrdinalIgnoreCase)),
                    "---method--- 的值不会被当成一行请求头", ref pass, ref fail);
                BalanceSources.SaveSecret("wb-method-test.secret.txt", withMethod);
                var rtMethod = BalanceSources.ReadSecret("wb-method-test.secret.txt");
                Check(!rtMethod.headers.Contains("POST") && rtMethod.body == "{\"PageSize\":20}"
                    && rtMethod.headers.Contains("x-user-id: abc") && rtMethod.headers.Contains("cookie: session=a"),
                    "另一条读取路径（自定义余额源）同样不吞头、也不把 method 当请求头", ref pass, ref fail);
                Check(CredentialCapture.ComposeSecret(cap.Url, cap.Headers, "session=a", null, null)
                        .IndexOf("---method---", StringComparison.Ordinal) < 0,
                    "负对照：没抄到方法时不写 method 段（不编一个默认值冒充事实）", ref pass, ref fail);

                // ---- 客户端提示：只有页面给了真值才补 ----
                var hNoHint = CredentialCapture.BuildFinalHeaders(null, null, WbApi, "UA", WbApi, "zh-CN");
                Check(!hNoHint.Any(h => h.Key == "sec-ch-ua"),
                    "负对照：没有真客户端提示时不补 sec-ch-ua（编一个只会让指纹更不一致）", ref pass, ref fail);
                var hHint = CredentialCapture.BuildFinalHeaders(null, null, WbApi, "UA", WbApi, "zh-CN",
                    "\"Chromium\";v=\"140\"", "?0", "\"Windows\"");
                Check(hHint.Any(h => h.Key == "sec-ch-ua" && h.Value == "\"Chromium\";v=\"140\"")
                    && hHint.Any(h => h.Key == "sec-ch-ua-mobile") && hHint.Any(h => h.Key == "sec-ch-ua-platform"),
                    "页面给了真值就补上三项客户端提示", ref pass, ref fail);

                // ---- 传输矩阵：唯一能把「协议／代理」与「客户端指纹」分开的判据 ----
                // 现场事实：同一套 cookie、同一组请求头，浏览器 200、.NET 401。
                // 那两层都排除后，只剩出口 IP 与协议版本 —— 这两个各有一个开关，必须先能单独试。
                Check(StatusProbe.PickWorkingTransport(new[] { 401, 200, 401, 401 }) == 1,
                    "矩阵：只有直连是 2xx 时选直连", ref pass, ref fail);
                Check(StatusProbe.PickWorkingTransport(new[] { 401, 401, 200, 200 }) == 2,
                    "矩阵：并列 2xx 时取序号靠前（离现状改动最小的那个）", ref pass, ref fail);
                Check(StatusProbe.PickWorkingTransport(new[] { 0, 404, 500, -1 }) == -1,
                    "负对照：无响应(0/-1)、404、500 一律不算通过（不许把「没试成」当「能过」）", ref pass, ref fail);
                Check(StatusProbe.DescribeTransportVerdict(new[] { 401, 401, 401, 401 }, -1).Contains("全部被拒"),
                    "矩阵全失败时结论必须点明「不是协议、也不是代理」", ref pass, ref fail);
                Check(StatusProbe.DescribeTransportVerdict(new[] { 401, 200, 401, 401 }, 1).Contains("系统代理"),
                    "矩阵选出直连时结论必须点明差异在系统代理（否则用户不知道下一步做什么）", ref pass, ref fail);
                Check(StatusProbe.DescribeTransportVerdict(new[] { 401, 401, 401, 401 }, -1).Contains("401 / 401"),
                    "矩阵结论里带上四次的状态码（证据要与结论一起出现）", ref pass, ref fail);

                Check(new StatusReport().WorkbuddyStatus == -1
                    && !StatusProbe.LooksLikeCredentialProblem(new StatusReport().WorkbuddyStatus),
                    "负对照：没拿到响应（-1）不被误判成凭据问题（否则网络一断就让你去换凭据）", ref pass, ref fail);

                var report = new StatusReport();
                StatusProbe.ApplyCustomResults(report, new Action<StatusReport>[]
                {
                    r => r.DynamicRows.Add(new BalanceCell { Name = "随想余额", Ok = true, Value = 8.57 })
                });
                Check(report.DynamicRows.Count == 1 && report.DynamicRows[0].Name == "随想余额" && report.DynamicRows[0].Ok,
                    "自定义余额异步结果写入气泡报告", ref pass, ref fail);
                string reportJson = StatusProbe.ToJson(report, null);
                using (var doc = JsonDocument.Parse(reportJson))
                    Check(doc.RootElement.GetProperty("dynamic_balances")[0].GetProperty("name").GetString() == "随想余额",
                        "无头报告包含动态余额", ref pass, ref fail);
            }
            catch (Exception ex)
            {
                fail++;
                Console.WriteLine("[FAIL] 未处理异常：" + ex.Message);
            }
            finally
            {
                Environment.SetEnvironmentVariable("AZHU_SECRET_DIR", old);
                try
                {
                    string full = Path.GetFullPath(dir);
                    if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileName(full).StartsWith("AzhuPet-BalanceConfigTest-", StringComparison.Ordinal))
                        Directory.Delete(full, true);
                }
                catch { }
            }
            Console.WriteLine("余额配置测试：PASS " + pass + " / FAIL " + fail);
            return fail == 0 ? 0 : 1;
        }

        private static void Check(bool ok, string name, ref int pass, ref int fail)
        {
            if (ok) { pass++; Console.WriteLine("[PASS] " + name); }
            else { fail++; Console.WriteLine("[FAIL] " + name); }
        }
    }
}
