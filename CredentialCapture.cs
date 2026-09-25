// 凭据采集的**纯逻辑层**：把「浏览器里那个请求」变成一份凭据文件。
// 刻意不引用任何 WebView2 类型 —— 于是这里的每条规则都能被 --balanceconfigtest 用合成输入
// 离线逼红，不必真的开窗口、登一次、撞一次真接口。
// ⚠ 判据纪律 7（先证明它能失败）：真浏览器不会为了让我们验证而变形；规则若只能靠
//   「登一次看看」来验，就等于没有判据。本项目为此栽过（积分口径判定）。
//
// 为什么是「抄」而不是「拼」：页面凭什么带上 x-user-id、将来会不会多一个风控签名头，
// 我们并不知道（只知道文件里现在是 11 个头）。抄下来的那份天生是对的；拼出来的只能对一次。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AzhuPet
{
    /// <summary>从页面里抄下来的那个请求（URL / 方法 / 头 / body）。</summary>
    internal sealed class CapturedRequest
    {
        public string Url = "";
        public string Method = "POST";
        public List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();
        public string Body = "";
    }

    internal static class CredentialCapture
    {
        /// <summary>WorkBuddy 网页入口：内嵌浏览器从这里开始。</summary>
        public const string WorkbuddyLoginUrl = "https://www.workbuddy.cn/";

        /// <summary>WorkBuddy 余额接口。**这是兜底默认值，不是第二处真值** ——
        /// 一旦抄到页面自己发的那个请求就以抄到的 URL 为准（凭据文件第一行本来就是这份数据）。</summary>
        public const string WorkbuddyBalanceUrl = "https://www.workbuddy.cn/billing/meter/get-user-resource";

        /// <summary>抓取目标：URL 里含这段子串的请求就是要抄的那个。</summary>
        public const string WorkbuddyCapturePattern = "/billing/meter/get-user-resource";

        /// <summary>拼凭据文件时一律丢掉的头。
        /// 三类理由：① 传输层由 HttpClient 自己管（content-length / accept-encoding / host / connection）
        /// —— 原样回放会得到一个长度对不上的请求；② cookie 必须由我们重建（见 HookScript 的注释）；
        /// ③ transfer-encoding 属于逐跳头，重放没有意义。</summary>
        private static readonly string[] DroppedHeaders =
            { "cookie", "content-length", "accept-encoding", "host", "connection", "transfer-encoding" };

        /// <summary>注入页面的钩子脚本（在页面脚本执行**之前**注入，见窗口里的 AddScriptToExecuteOnDocumentCreatedAsync）。
        /// 钩住 fetch 与 XHR，把命中抓取目标的那个请求的 URL / 方法 / **真实请求头** / body 存成一个 JSON 串。
        /// ⚠ 这里**拿不到 cookie**：浏览器规范把 cookie 列为「禁止由脚本设置」的请求头名，
        ///   所以 cookie 必须另从宿主侧的 CookieManager 取 —— 而那一侧反而能拿到 httpOnly 的 session。
        ///   **两条腿缺一不可**，只靠本脚本永远拼不出完整凭据（这是最容易想漏的一步）。
        /// ⚠ 模板里用单引号，只留下 __PATTERN__ 一个占位符：模式是按 JSON 字符串注入的，
        ///   于是模式里就算带引号也破坏不了脚本结构（见 BuildHookScript 的判据）。
        /// ⚠ 只记第一个命中（if(window.__azhuCapture)return）：后续重复请求不该覆盖掉先抄到的那份。</summary>
        private const string HookTemplate = @"(function(){
if(window.__azhuHook)return;window.__azhuHook=1;window.__azhuCapture='';
var PAT=__PATTERN__;
function hdrs(h){var o={};try{
if(!h)return o;
if(typeof h.forEach==='function'&&!(h instanceof Array)){h.forEach(function(v,k){o[k]=v;});return o;}
if(h instanceof Array){for(var i=0;i<h.length;i++){var p=h[i];if(p&&p.length===2)o[p[0]]=p[1];}return o;}
if(typeof h==='object'){for(var k in h){if(Object.prototype.hasOwnProperty.call(h,k))o[k]=''+h[k];}}
}catch(e){}return o;}
function rec(u,m,h,b){try{
if(window.__azhuCapture)return;
if(!u||(''+u).indexOf(PAT)<0)return;
window.__azhuCapture=JSON.stringify({url:''+u,method:''+(m||'GET'),headers:hdrs(h),body:(b==null?'':''+b)});
}catch(e){}}
var _f=window.fetch;
if(_f){window.fetch=function(i,init){try{
var u=(typeof i==='string')?i:((i&&i.url)||'');
var m=(init&&init.method)||(i&&i.method)||'GET';
rec(u,m,init&&init.headers,init&&init.body);
}catch(e){}return _f.apply(this,arguments);};}
var _o=XMLHttpRequest.prototype.open,_h=XMLHttpRequest.prototype.setRequestHeader,_s=XMLHttpRequest.prototype.send;
XMLHttpRequest.prototype.open=function(m,u){try{this.__azhu={m:m,u:u,h:{}};}catch(e){}return _o.apply(this,arguments);};
XMLHttpRequest.prototype.setRequestHeader=function(k,v){try{if(this.__azhu)this.__azhu.h[k]=v;}catch(e){}return _h.apply(this,arguments);};
XMLHttpRequest.prototype.send=function(b){try{if(this.__azhu)rec(this.__azhu.u,this.__azhu.m,this.__azhu.h,b);}catch(e){}return _s.apply(this,arguments);};
})();";

        /// <summary>把抓取目标注入钩子模板。模式走 JSON 编码再嵌入，避免它把脚本本身拼坏。</summary>
        public static string BuildHookScript(string capturePattern)
        {
            return HookTemplate.Replace("__PATTERN__", JsonSerializer.Serialize(capturePattern ?? ""));
        }

        /// <summary>这个 URL 是不是要抄的那个请求（纯函数）。
        /// 忽略大小写：主机名大小写无关，而路径我们也不打算跟站点较真大小写差异。</summary>
        public static bool IsTarget(string url, string capturePattern)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(capturePattern)) return false;
            return url.IndexOf(capturePattern, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>这个头在拼文件时该不该丢掉（纯函数）。</summary>
        public static bool IsDroppedHeader(string name)
        {
            string n = (name ?? "").Trim();
            return DroppedHeaders.Any(d => string.Equals(d, n, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>把 cookie 罐里的东西拼成一行 cookie 头（纯函数）。
        /// 按名字排序而不是照浏览器给的顺序 —— 这样连点两次「重新获取」产生**逐字节相同**的文件，
        /// 出问题时能 diff 得出来；顺序对 cookie 语义没有影响。</summary>
        public static string BuildCookieHeader(IEnumerable<KeyValuePair<string, string>> cookies)
        {
            if (cookies == null) return "";
            var list = cookies
                .Where(c => !string.IsNullOrWhiteSpace(c.Key))
                .OrderBy(c => c.Key, StringComparer.Ordinal)
                .Select(c => c.Key.Trim() + "=" + (c.Value ?? ""))
                .ToList();
            return string.Join("; ", list);
        }

        /// <summary>解析钩子存下来的那段 JSON（纯函数）。解析不出来一律返回 null，**绝不抛**。
        /// ⚠ 先判 ValueKind 再 TryGetProperty：JsonElement.TryGetProperty 对非 Object 元素是
        ///   **抛 InvalidOperationException**，不是返回 false（本项目在积分解析那里被这条打过脸）。</summary>
        public static CapturedRequest ParseCaptured(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return null;
                    var req = new CapturedRequest();
                    if (root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String) req.Url = u.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(req.Url)) return null;
                    if (root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String) req.Method = m.GetString() ?? "POST";
                    if (root.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
                        foreach (var p in h.EnumerateObject())
                            req.Headers.Add(new KeyValuePair<string, string>(p.Name,
                                p.Value.ValueKind == JsonValueKind.String ? (p.Value.GetString() ?? "") : p.Value.ToString()));
                    if (root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String) req.Body = b.GetString() ?? "";
                    return req;
                }
            }
            catch { return null; }
        }

        /// <summary>读一份**已有的**凭据文本，拆出 URL / 头 / body（纯函数）。
        /// 规则与 BalanceSources.ReadSecret 一致（同一份数据没有第二个解析口径），
        /// 差别只在吃字符串而不是吃路径 —— 这样才能离线喂合成文本。</summary>
        public static List<KeyValuePair<string, string>> ParseRawSecretText(string text, out string url, out string body)
        {
            url = null; body = null;
            var headers = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(text)) return headers;
            var bodyLines = new List<string>();
            bool inBody = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                string t = line.Trim();
                if (t.StartsWith("---body", StringComparison.Ordinal)) { inBody = true; continue; }
                if (t.StartsWith("---", StringComparison.Ordinal)) continue;
                if (inBody) { bodyLines.Add(line); continue; }
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal)) continue;
                if (url == null) { url = t; continue; }
                int c = line.IndexOf(':');
                if (c > 0) headers.Add(new KeyValuePair<string, string>(line.Substring(0, c).Trim(), line.Substring(c + 1).Trim()));
            }
            if (bodyLines.Count > 0) body = string.Join("\n", bodyLines).Trim();
            return headers;
        }

        /// <summary>没抄到、也没有旧文件时的兜底请求头（纯函数）。
        /// ⚠ 这是**退路，不是等价物**：origin 能精确算出来（就是 URL 的来源），
        ///   referer 只能猜成来源加一个斜杠，user-agent 之类根本给不了。
        ///   所以走这条路时界面上必须说清「这是兜底，可能仍被网关拒绝」，不能假装一样可靠。</summary>
        public static List<KeyValuePair<string, string>> DerivedTemplate(string url)
        {
            var list = new List<KeyValuePair<string, string>>();
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return list;
            string origin = u.Scheme + "://" + u.Authority;
            list.Add(new KeyValuePair<string, string>("accept", "application/json"));
            list.Add(new KeyValuePair<string, string>("accept-language", "zh-CN,zh;q=0.9"));
            list.Add(new KeyValuePair<string, string>("content-type", "application/json"));
            list.Add(new KeyValuePair<string, string>("origin", origin));
            list.Add(new KeyValuePair<string, string>("referer", origin + "/"));
            return list;
        }

        /// <summary>拼出凭据文件的正文（纯函数）：第一行 URL，随后「名字: 值」，可选 ---body--- 段。
        /// 行尾用 CRLF —— 与现有 workbuddy_secret.txt（CRLF=15）保持一致，用户在编辑器里看着正常。
        /// URL 为空一律返回空串：宁可不写，也不写一份连请求地址都没有的凭据进去。</summary>
        public static string ComposeSecret(string url, IEnumerable<KeyValuePair<string, string>> headers,
            string cookieHeader, string body)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            var sb = new System.Text.StringBuilder();
            sb.Append(url.Trim()).Append("\r\n");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in headers ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                string name = (h.Key ?? "").Trim();
                string value = (h.Value ?? "").Trim();
                if (name.Length == 0 || value.Length == 0) continue;
                if (IsDroppedHeader(name)) continue;      // cookie 也在这里被丢掉，下面由 cookieHeader 重建
                if (!seen.Add(name)) continue;            // 同名头只留第一个，避免出现两行 content-type
                sb.Append(name).Append(": ").Append(value).Append("\r\n");
            }
            if (!string.IsNullOrWhiteSpace(cookieHeader) && seen.Add("cookie"))
                sb.Append("cookie: ").Append(cookieHeader.Trim()).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(body))
                sb.Append("---body---\r\n").Append(body.Trim()).Append("\r\n");
            return sb.ToString();
        }
    }
}
