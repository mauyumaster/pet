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

        // 页面上下文。**不是装饰**：抄到的请求头天然缺一整套「浏览器自动添加」的头
        // （见 CredentialCapture.BuildFinalHeaders 的说明），这几项正好能补上其中一部分。
        public string PageUserAgent = "";   // navigator.userAgent —— 这是真值，不是猜的
        public string PageHref = "";        // location 的 origin+path（已切掉 query，免得把 token 带进凭据文件）
        public string PageOrigin = "";      // location.origin（相对地址兜底用）
        public string PageLanguage = "";    // navigator.language

        // 浏览器客户端提示（Chromium 默认对 HTTPS 发这三项）。同样**不是装饰**：
        // user-agent 说自己是 Edge 140，却不带 sec-ch-ua，这本身就是「脚本伪装的浏览器」的
        // 典型指纹不一致 —— 站点若在网关层做风险控制，这一条足够让它拒绝（2026-09-25 的现场：
        // 同一套 cookie，浏览器 200、我们 401，cookie 与请求头已逐项对齐，只剩「客户端身份」这一层）。
        // ⚠ 值来自 navigator.userAgentData，是页面侧真值；拿不到就留空，一律不猜。
        public string PageChUa = "";          // sec-ch-ua
        public string PageChUaMobile = "";    // sec-ch-ua-mobile
        public string PageChUaPlatform = "";  // sec-ch-ua-platform

        /// <summary>这次请求最终拿到的 HTTP 状态码（0 = 钩子还没等到响应，或定稿时响应未回）。
        /// ⚠ 这个字段本身就是判据：抄回来的那份若 status 不是 2xx，说明「网站自己发这个请求」也没成功
        ///   —— 那时候回测 401 与我们的拼接无关，去换凭据是白换（2026-09-25 卡住的那一轮正是如此）。</summary>
        public int Status;
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
        /// ⚠ **2xx 的那条优先**（2026-09-25 改）：rec 只把「第一个命中」当作临时定稿，等响应码回来时
        ///   若那是一次 2xx，就覆盖成它。旧行为是「第一个命中永远不让位」—— 而页面刚打开时往往先发
        ///   一次失败请求（未登录／会话未就绪），于是我们抄到的可能永远是那个失败样本：回测必然 401，
        ///   而现场看起来「页面上余额明明显示着」。status 随定稿一起交给宿主，宿主据此才分得清
        ///   「抄到的是成功请求」还是「网站自己发这个请求也被拒」—— 后者换凭据没用。
        /// ⚠ 已回填 status 的对象按引用交给调用方（done(o,st)），**不走 __azhuLast 之类的全局暂存** ——
        ///   否则同一个接口并发两个请求时会把 A 的响应码安到 B 身上。
        /// ⚠ __azhuSeen 是**诊断用**的：把所有经过 fetch/XHR 的路径都记一份（哪怕是别的接口）。
        ///   没有它，「抄不到」只有一句「还没看到余额请求」—— 分不清是钩子没生效、页面压根不发
        ///   XHR、还是接口换了名字。2026-09-25 就是靠这个才看清「请求发去了另一个窗口」。</summary>
        private const string HookTemplate = @"(function(){
if(window.__azhuHook)return;window.__azhuHook=1;window.__azhuCapture='';window.__azhuCapObj=null;
window.__azhuSeen=[];
var PAT=__PATTERN__;
function hdrs(h){var o={};try{
if(!h)return o;
if(typeof h.forEach==='function'&&!(h instanceof Array)){h.forEach(function(v,k){o[k]=v;});return o;}
if(h instanceof Array){for(var i=0;i<h.length;i++){var p=h[i];if(p&&p.length===2)o[p[0]]=p[1];}return o;}
if(typeof h==='object'){for(var k in h){if(Object.prototype.hasOwnProperty.call(h,k))o[k]=''+h[k];}}
}catch(e){}return o;}
function seen(u){try{
var s=''+(u||'');if(!s)return;
var p;try{p=new URL(s,location.href).pathname;}catch(e){p=s;}
if(!p||p.length>120)return;
var a=window.__azhuSeen;
for(var i=0;i<a.length;i++){if(a[i]===p)return;}
if(a.length<20)a.push(p);
}catch(e){}}
function abs(u){try{return new URL(''+u,location.href).href;}catch(e){return ''+u;}}
function pg(){try{var s=''+(location.href||'');var i=s.indexOf('?');if(i>=0)s=s.substring(0,i);var j=s.indexOf('#');if(j>=0)s=s.substring(0,j);return s;}catch(e){return '';}}
function og(){try{if(location.origin)return ''+location.origin;var m=/^([a-z][a-z0-9+.-]*:\/\/[^\/]+)/i.exec(''+(location.href||''));return m?m[1]:'';}catch(e){return '';}}
function ch(){var o={chua:'',chuam:'',chuap:''};try{
var q=String.fromCharCode(34);
var d=navigator.userAgentData;if(!d)return o;
if(d.brands&&d.brands.length){var a=[];for(var i=0;i<d.brands.length;i++){var b=d.brands[i];if(b&&b.brand)a.push(q+b.brand+q+';v='+q+b.version+q);}o.chua=a.join(', ');}
if(typeof d.mobile==='boolean')o.chuam=d.mobile?'?1':'?0';
if(d.platform)o.chuap=q+d.platform+q;
}catch(e){}return o;}
var HH=ch();
function rec(u,m,h,b){try{
seen(u);
if(!u||(''+u).indexOf(PAT)<0)return null;
var o={url:abs(u),method:''+(m||'GET'),headers:hdrs(h),
body:((typeof b==='string')?b:''),
ua:(navigator.userAgent||''),href:pg(),org:og(),lang:(navigator.language||''),
chua:HH.chua,chuam:HH.chuam,chuap:HH.chuap,status:0};
if(!window.__azhuCapObj){window.__azhuCapObj=o;window.__azhuCapture=JSON.stringify(o);}
return o;
}catch(e){return null;}}
function ok2xx(st){var n=0+st;return n>=200&&n<300;}
function done(o,st){try{
if(!o)return;
o.status=(st==null?0:(0+st));
if(ok2xx(o.status)){window.__azhuCapObj=o;window.__azhuCapture=JSON.stringify(o);return;}
if(window.__azhuCapObj===o)window.__azhuCapture=JSON.stringify(o);
}catch(e){}}
var _f=window.fetch;
if(_f){window.fetch=function(i,init){var o=null;try{
var u=(typeof i==='string')?i:((i&&i.url)||'');
var m=(init&&init.method)||(i&&i.method)||'GET';
o=rec(u,m,init&&init.headers,init&&init.body);
}catch(e){}
var p=_f.apply(this,arguments);
try{if(o&&p&&p.then)p.then(function(r){done(o,r&&r.status);},function(){});}catch(e){}
return p;};}
var _o=XMLHttpRequest.prototype.open,_h=XMLHttpRequest.prototype.setRequestHeader,_s=XMLHttpRequest.prototype.send;
XMLHttpRequest.prototype.open=function(m,u){try{this.__azhu={m:m,u:u,h:{}};}catch(e){}return _o.apply(this,arguments);};
XMLHttpRequest.prototype.setRequestHeader=function(k,v){try{if(this.__azhu)this.__azhu.h[k]=v;}catch(e){}return _h.apply(this,arguments);};
XMLHttpRequest.prototype.send=function(b){try{
if(this.__azhu){var o=rec(this.__azhu.u,this.__azhu.m,this.__azhu.h,b);var x=this;
if(o&&x.addEventListener)x.addEventListener('loadend',function(){done(o,x.status);});}
}catch(e){}return _s.apply(this,arguments);};
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

        /// <summary>两个 URL 是否属于同一个站点（纯函数）。
        /// 用途：内嵌浏览器里被点开的新窗口，**同站**的拉回本窗口（钩子只挂在原来那个 WebView2 上，
        ///   站内新窗口若由 WebView2 自己开成 popup，那里的请求一个都抄不到）；
        ///   **跨站**的（第三方登录弹窗）保持弹窗语义 —— 那些流程依赖 window.opener 通信，
        ///   拉回本窗口会把「登录」本身弄坏。
        /// ⚠ 比较的是**注册域**，不是「一个 host 是不是另一个的后缀」：站点把工作台放在
        ///   app.workbuddy.cn 而登录在 www.workbuddy.cn 太常见了，后者那种算法会把兄弟子域
        ///   判成跨站 ⇒ 又开一个 popup ⇒ 钩子白装。而 evilworkbuddy.cn 必须**不**算同站
        ///   （裸 EndsWith 会误判 —— 与「'0.1.1.' 会误匹配 '0.1.10.0'」同类）。</summary>
        public static bool IsSameSite(string url, string siteUrl)
        {
            string a = RegistrableOf(HostOf(url)), b = RegistrableOf(HostOf(siteUrl));
            if (a.Length == 0 || b.Length == 0) return false;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string HostOf(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out u)) return "";
            return (u.Host ?? "").TrimEnd('.').ToLowerInvariant();
        }

        /// <summary>常见二级后缀。取注册域时要多看一段，否则 a.com.cn 与 b.com.cn 会被当成同一家。</summary>
        private static readonly string[] SecondLevelTlds =
        {
            "com.cn", "net.cn", "org.cn", "gov.cn", "edu.cn", "ac.cn",
            "co.uk", "org.uk", "ac.uk", "gov.uk",
            "com.hk", "org.hk", "com.tw", "org.tw", "com.au", "co.jp", "co.kr", "com.sg",
        };

        /// <summary>取 host 的注册域（近似 eTLD+1，纯函数）。
        /// ⚠ 这不是完整的 Public Suffix List，只覆盖上面那张表中的常见二级后缀。
        ///   够用的理由：这条判定两端分别是「我们自己的站点」与「第三方登录域」，差异大得离谱；
        ///   真做全 PSL 得拖进一张几十 KB 的表 —— 对一个桌宠不值（项目价值观：轻量）。
        /// ⚠ IP 地址整段返回：按标签切会得到 "0.1" 这种荒唐结果。</summary>
        private static string RegistrableOf(string host)
        {
            string h = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            if (h.Length == 0) return "";
            if (System.Net.IPAddress.TryParse(h, out _)) return h;
            var parts = h.Split('.');
            if (parts.Length <= 2) return h;
            string last2 = parts[parts.Length - 2] + "." + parts[parts.Length - 1];
            if (SecondLevelTlds.Contains(last2))
                return string.Join(".", parts.Skip(Math.Max(0, parts.Length - 3)));
            return last2;
        }

        /// <summary>把钩子记下来的请求路径整理成给人看的一小段（纯函数）。
        /// 用途：抄不到目标请求时，把「这个窗口到底发过哪些请求」显示出来 ——
        ///   否则用户只看到一句「还没看到余额请求」，我们也无从判断是钩子没生效、还是接口换了名字。
        /// ⚠ 丢掉静态资源后缀：它们极少走 XHR，但一旦混进来就会把真正像接口的那两条挤出列表。
        /// ⚠ 只显示 path（钩子根本没记 query）：query 里可能带 token，没必要显示在屏幕上。</summary>
        public static string FormatSeenForUser(IEnumerable<string> paths)
        {
            if (paths == null) return "";
            var kept = new List<string>();
            foreach (var raw in paths)
            {
                string p = (raw ?? "").Trim();
                if (p.Length == 0 || LooksLikeStaticAsset(p)) continue;
                if (kept.Contains(p)) continue;
                kept.Add(p);
                if (kept.Count >= 8) break;
            }
            return string.Join("\n", kept.Select(p => "  " + p));
        }

        /// <summary>看起来是静态资源路径（纯函数）。</summary>
        public static bool LooksLikeStaticAsset(string path)
        {
            string p = (path ?? "").Trim().ToLowerInvariant();
            int cut = p.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) p = p.Substring(0, cut);
            string[] ext = { ".js", ".mjs", ".css", ".png", ".jpg", ".jpeg", ".gif", ".webp",
                             ".svg", ".ico", ".woff", ".woff2", ".ttf", ".map", ".html" };
            return ext.Any(e => p.EndsWith(e, StringComparison.Ordinal));
        }

        /// <summary>这个头在拼文件时该不该丢掉（纯函数）。</summary>
        public static bool IsDroppedHeader(string name)
        {
            string n = (name ?? "").Trim();
            return DroppedHeaders.Any(d => string.Equals(d, n, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>把「抄到的那份请求**自己**的状态码」翻译成一句能指导下一步的话（纯函数）。
        ///
        /// ⚠ 这一句是本轮修复的全部意义（2026-09-25）：同样是「回测 401」，两种原因修法**相反** ——
        ///   ① 我们拼接丢了东西（该去补头） ② 网站自己发这个请求也没通过（去换凭据、补头都没用，
        ///   得先让浏览器里那个会话真的走到能取数的页面上）。此前界面上只有一句「服务器仍然拒绝这份凭据」，
        ///   两种原因长得一模一样，用户只能反复点。
        ///
        /// status == 0 表示钩子还没等到响应（请求可能还在飞，或压根不是 XHR）⇒ **不下结论**，
        /// 宁可不说，也不要凭空的判断把用户往错方向指。</summary>
        public static string DescribeCapturedStatus(int status)
        {
            if (status == 0) return "";
            if (status >= 200 && status < 300) return "网站自己发这个请求是 " + status + "（成功）";
            return "⚠ 网站自己发这个请求也返回 " + status + " —— 说明这个浏览器里的会话本身就没通过，"
                 + "换凭据、补请求头都不解决问题。先在这个窗口里把余额所在的页面**真正打开一次**"
                 + "（让地址栏走到那一层、页面自己把数据算出来），再点抓取。";
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
                    if (root.TryGetProperty("ua", out var ua) && ua.ValueKind == JsonValueKind.String) req.PageUserAgent = ua.GetString() ?? "";
                    if (root.TryGetProperty("href", out var hr) && hr.ValueKind == JsonValueKind.String) req.PageHref = hr.GetString() ?? "";
                    if (root.TryGetProperty("org", out var og) && og.ValueKind == JsonValueKind.String) req.PageOrigin = og.GetString() ?? "";
                    if (root.TryGetProperty("lang", out var lg) && lg.ValueKind == JsonValueKind.String) req.PageLanguage = lg.GetString() ?? "";
                    if (root.TryGetProperty("chua", out var cu) && cu.ValueKind == JsonValueKind.String) req.PageChUa = cu.GetString() ?? "";
                    if (root.TryGetProperty("chuam", out var cm) && cm.ValueKind == JsonValueKind.String) req.PageChUaMobile = cm.GetString() ?? "";
                    if (root.TryGetProperty("chuap", out var cp) && cp.ValueKind == JsonValueKind.String) req.PageChUaPlatform = cp.GetString() ?? "";
                    // status 只在钩子等到响应之后才回填。取不到就是 0 ＝「还不知道」，**不是**「失败了」——
                    // 两者必须分开，否则会把「响应还没回来」误报成「网站自己也被拒」。
                    if (root.TryGetProperty("status", out var st) && IntOf(st, out int status)) req.Status = status;
                    return req;
                }
            }
            catch { return null; }
        }

        /// <summary>宽容取整数（数字与数字字符串都收），纯函数。
        /// ⚠ 自己写一个而不是直接用 JsonElement.TryGetInt32：**后者遇到非 Number 元素是抛
        ///   InvalidOperationException，不是返回 false**（同 StatusProbe.IntEl 上记的那条教训）。
        ///   钩子写出来的是数字，但这段 JSON 要经浏览器往返一圈，宽容一点不吃亏。</summary>
        private static bool IntOf(JsonElement el, out int v)
        {
            v = 0;
            if (el.ValueKind == JsonValueKind.Number) return el.TryGetInt32(out v);
            if (el.ValueKind == JsonValueKind.String) return int.TryParse(el.GetString(), out v);
            return false;
        }

        /// <summary>读一份**已有的**凭据文本，拆出 URL / 方法 / 头 / body（纯函数）。
        /// 规则与 BalanceSources.ReadSecret 一致（同一份数据没有第二个解析口径），
        /// 差别只在吃字符串而不是吃路径 —— 这样才能离线喂合成文本。</summary>
        public static List<KeyValuePair<string, string>> ParseRawSecretText(string text, out string url, out string body, out string method)
        {
            url = null; body = null; method = null;
            var headers = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(text)) return headers;
            var bodyLines = new List<string>();
            bool inBody = false, inMethod = false;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                string t = line.Trim();
                if (t.StartsWith("---body", StringComparison.Ordinal)) { inBody = true; inMethod = false; continue; }
                if (t.StartsWith("---method", StringComparison.Ordinal)) { inMethod = true; inBody = false; continue; }
                if (t.StartsWith("---", StringComparison.Ordinal)) { inMethod = false; continue; }
                if (inBody) { bodyLines.Add(line); continue; }
                // ⚠ 读到值就要**立刻复位** inMethod。不复位的话，method 之后的每一行都会
                //   继续走这一支（被吞掉），而且 method 会被最后一个非空行反复覆盖 ——
                //   实测得到的是 "COOKIE: SESSION=A"，而所有请求头一起消失。
                if (inMethod) { if (t.Length > 0) { method = t.ToUpperInvariant(); inMethod = false; } continue; }
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

        /// <summary>把「旧凭据的头 + 抄到的头 + 页面上下文」合成最终要发的请求头（纯函数）。
        ///
        /// ⚠⚠ 2026-09-25 的事故就出在这一步：此前是**二选一** —— 抄到了就用抄到的那份整份替换旧头。
        ///   但页面 JS 能看见的只有它自己显式写的那几个头；**user-agent / accept / accept-language /
        ///   origin / referer / sec-fetch-\* 这一整套是浏览器自动加上去的，按规范脚本既设不了也读不到**
        ///   （forbidden header names）。于是「抄到」的那份**天然缺它们** —— 回测请求发出去连应用都没到，
        ///   被 nginx 直接顶回 401（响应体是 HTML 错误页而不是 API 的 JSON），而页面上余额明明显示得好好的。
        ///   修法不是二选一，是**叠加**：
        ///     ① 旧凭据的头打底（它有一份从真浏览器抓来的完整头 —— 这是最稀缺的东西）
        ///     ② 抄到的头覆盖上去（拿最新的 x-user-id / content-type 等）
        ///     ③ 仍然缺的用页面上下文补（navigator.userAgent / location 都是页面侧可读的真值，不是猜的）
        ///   第四层才是 DerivedTemplate（真·什么都没有时的骨架）。
        /// ⚠ 旧头的 origin / referer 只在**与目标同源**时沿用，否则等于把 A 站的来源发给 B 站。
        /// ⚠ 同名头（不区分大小写）只留一份、后写者胜：两份 content-type 会让服务器无所适从。</summary>
        public static List<KeyValuePair<string, string>> BuildFinalHeaders(
            IEnumerable<KeyValuePair<string, string>> oldHeaders,
            IEnumerable<KeyValuePair<string, string>> capturedHeaders,
            string targetUrl,
            string pageUserAgent,
            string pageHref,
            string pageLanguage,
            string pageChUa = "",
            string pageChUaMobile = "",
            string pageChUaPlatform = "")
        {
            var result = new List<KeyValuePair<string, string>>();
            var at = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string targetOrigin = OriginOf(targetUrl);

            Action<string, string> put = (name, value) =>
            {
                string n = (name ?? "").Trim();
                string v = (value ?? "").Trim();
                if (n.Length == 0 || v.Length == 0) return;
                if (IsDroppedHeader(n)) return;   // cookie 由 cookieHeader 重建；content-length / host 交 HttpClient
                int i;
                if (at.TryGetValue(n, out i)) { result[i] = new KeyValuePair<string, string>(result[i].Key, v); return; }
                at[n] = result.Count;
                result.Add(new KeyValuePair<string, string>(n, v));
            };

            // ① 旧凭据打底
            foreach (var h in oldHeaders ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                string n = (h.Key ?? "").Trim();
                if ((n.Equals("origin", StringComparison.OrdinalIgnoreCase) || n.Equals("referer", StringComparison.OrdinalIgnoreCase))
                    && targetOrigin.Length > 0
                    && !string.Equals(OriginOf(h.Value), targetOrigin, StringComparison.OrdinalIgnoreCase))
                    continue;   // 别把别的站点的来源发给目标站
                put(n, h.Value);
            }

            // ② 抄到的覆盖
            foreach (var h in capturedHeaders ?? Enumerable.Empty<KeyValuePair<string, string>>())
                put(h.Key, h.Value);

            // ③ 浏览器自动头：只在仍然缺的时候补（旧头里那份是真实浏览器抓的，优先）
            if (!at.ContainsKey("user-agent") && !string.IsNullOrWhiteSpace(pageUserAgent))
                put("user-agent", pageUserAgent);
            if (!at.ContainsKey("accept-language"))
                put("accept-language", AcceptLanguageOf(pageLanguage));
            // 客户端提示：有真值才补，**绝不用假值凑数**（编一个 sec-ch-ua 只会让指纹更不一致）
            if (!at.ContainsKey("sec-ch-ua") && !string.IsNullOrWhiteSpace(pageChUa))
                put("sec-ch-ua", pageChUa);
            if (!at.ContainsKey("sec-ch-ua-mobile") && !string.IsNullOrWhiteSpace(pageChUaMobile))
                put("sec-ch-ua-mobile", pageChUaMobile);
            if (!at.ContainsKey("sec-ch-ua-platform") && !string.IsNullOrWhiteSpace(pageChUaPlatform))
                put("sec-ch-ua-platform", pageChUaPlatform);
            if (targetOrigin.Length > 0)
            {
                put("origin", targetOrigin);   // 从目标 URL 算出来的是确定值
                put("referer", SameOrigin(pageHref, targetOrigin) ? pageHref : targetOrigin + "/");
            }

            // ④ 最后仍缺的骨架头（只在既没抄到、也没有旧凭据时才会走到这里）
            foreach (var d in DerivedTemplate(targetUrl))
                if (!at.ContainsKey(d.Key)) put(d.Key, d.Value);

            return result;
        }

        private static string OriginOf(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out u)) return "";
            return u.Scheme + "://" + u.Authority;
        }

        private static bool SameOrigin(string url, string origin)
        {
            string o = OriginOf(url);
            return o.Length > 0 && string.Equals(o, origin, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>把 navigator.language（如 zh-CN）写成 accept-language 常见的样式。</summary>
        private static string AcceptLanguageOf(string lang)
        {
            string s = (lang ?? "").Trim();
            if (s.Length == 0) return "";
            int dash = s.IndexOf('-');
            if (dash <= 0) return s;
            return s + "," + s.Substring(0, dash) + ";q=0.9";
        }

        /// <summary>拼出凭据文件的正文（纯函数）：第一行 URL，可选 ---method--- 段，随后「名字: 值」，可选 ---body--- 段。
        /// 行尾用 CRLF —— 与现有 workbuddy_secret.txt（CRLF=15）保持一致，用户在编辑器里看着正常。
        /// URL 为空一律返回空串：宁可不写，也不写一份连请求地址都没有的凭据进去。
        /// ⚠ ---method--- 与 ---body--- 都是「抄到的事实」，缺了它们就等于把请求的另一半扔掉：
        ///   2026-09-25 之前这两段**从来没被发出去过**（回测写死 POST + 空 body `{}`），而 GET 同一个
        ///   地址实测返回 404 —— 也就是说方法一旦抄错，回测连「地址不存在」和「没通过鉴权」都分不清。</summary>
        public static string ComposeSecret(string url, IEnumerable<KeyValuePair<string, string>> headers,
            string cookieHeader, string body, string method = null)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            var sb = new System.Text.StringBuilder();
            sb.Append(url.Trim()).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(method))
                sb.Append("---method---\r\n").Append(method.Trim().ToUpperInvariant()).Append("\r\n");
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
