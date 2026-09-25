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

                // ---- 浏览器取凭据：采集规则（CredentialCapture）----
                // ⚠ 2026-09-25：把「面板里登录一次就拿到凭据」的**规则**从浏览器宿主里拆出来，
                //   唯一目的就是让它有资格被验 —— 真浏览器不会为了让我们验证而变形，
                //   规则若只能靠「点一次看看」来验，就等于没有判据（判据纪律 7：先证明它能失败）。
                string hook = CredentialCapture.BuildHookScript(CredentialCapture.WorkbuddyCapturePattern);
                Check(hook.Contains("\"/billing/meter/get-user-resource\""), "钩子里嵌入抓取目标（走 JSON 编码）", ref pass, ref fail);
                Check(!hook.Contains("__PATTERN__"), "占位符已被替换（没有残留）", ref pass, ref fail);
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
                string oldUrl, oldBody;
                var oldHeaders = CredentialCapture.ParseRawSecretText(oldText, out oldUrl, out oldBody);
                Check(oldUrl == "https://www.workbuddy.cn/billing/meter/get-user-resource" && oldHeaders.Count == 3
                    && oldHeaders.Any(h => h.Key == "x-user-id" && h.Value == "abc"),
                    "能拆出旧凭据的 URL 与请求头（只换 cookie 那条路靠它）", ref pass, ref fail);
                string replayed = CredentialCapture.ComposeSecret(oldUrl, oldHeaders, "session=new; session_2=new2", null);
                Check(replayed.Contains("user-agent: UA") && replayed.Contains("x-user-id: abc")
                    && replayed.Contains("session=new; session_2=new2") && !replayed.Contains("old=1"),
                    "旧 cookie 被新 cookie 顶掉，其它头原样保留（不出现两个 cookie 行）", ref pass, ref fail);

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
