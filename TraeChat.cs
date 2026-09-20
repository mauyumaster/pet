// 桌宠「和桌宠说话」= 明文 llm_utils_chat（Trae 兼容通道，已验证可用，不扣积分）。
// 理由（2026-09-19 抓包证实）：Trae 真实对话主链路(create_agent_task / workflow/start / llm_utils_chat)
// 请求体全加密，明文只走服务端保留的兼容分支——能真实回复，但挂在独立通道不计入用户主计费桶。
// 因此本模块作桌宠闲聊后端；余额展示仍走 StatusProbe(明文可用) 的独立验证通道。
// 凭据与请求头全部复用 %LOCALAPPDATA%\AzhuPet\balance_secret.txt，避免再造第二份真值。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AzhuPet
{
    internal static class TraeChat
    {
        private const string Endpoint = "https://api5-normal.mchost.guru/api/agent/v3/llm_utils_chat";
        private const string DefaultModel = "DeepSeek-V4-Flash";

        /// <summary>
        /// 全局模型覆盖（命令行 <c>--model &lt;id&gt;</c>）。
        /// ⚠ 为什么用全局静态而不是给每个调用点加参数：四个调用点（台词／聊天窗／--llmtest／--eyetest）
        ///   本来都是裸调 ChatAsync，逐个传参**一定会漏**，而漏掉的那个会静默退回默认模型 ——
        ///   那正是本项目那条老毛病（同一份数据两个落点 ⇒ 迟早不一致）。
        /// 空 = 用 DefaultModel。
        /// </summary>
        public static string ModelOverride = "";

        /// <summary>模型 id 优先级：显式参数 &gt; 全局覆盖 &gt; 默认。</summary>
        public static string ResolveModel(string explicitModel)
        {
            if (!string.IsNullOrEmpty(explicitModel)) return explicitModel;
            return string.IsNullOrEmpty(ModelOverride) ? DefaultModel : ModelOverride;
        }
        private static readonly HttpClient _http = new HttpClient(new HttpClientHandler
        {
            UseProxy = false,   // 桌宠直连，避免被系统代理(如 Clash)截走
        });

        public static string SecretPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AzhuPet");
            return Path.Combine(dir, "balance_secret.txt");
        }

        // ---- OpenAI 兼容通道（2026-09-20，发布降门槛）----
        // 注入位：返回 (base, key, model)；null 或 base/key 任一为空 = 走 Trae（原有行为）。
        // ⚠ 做成可注入静态位而不是 ChatAsync 直读配置：与 OcrEye.Enabled 同一模式 ——
        //   判据能钉死输入（--speaktest 的通道判据），配置窗改完也即时生效。
        public static Func<Tuple<string, string, string>> OpenAiSource = null;

        /// <summary>**纯函数**：这通请求该不该走 OpenAI 兼容端点。</summary>
        public static bool ShouldUseOpenAi(Tuple<string, string, string> src)
        {
            return src != null
                && !string.IsNullOrWhiteSpace(src.Item1)
                && !string.IsNullOrWhiteSpace(src.Item2);
        }

        /// <summary>
        /// **纯函数**：内部 messages（content 为 [{type,text}] 数组）转成标准 OpenAI 兼容体。
        /// ⚠ content 必须拍平成**字符串**——不少兼容端点（DeepSeek／ollama）不接受数组形态。
        /// </summary>
        public static Dictionary<string, object> BuildOpenAiBody(string model, IReadOnlyList<object> messages)
        {
            var flat = new List<Dictionary<string, object>>();
            foreach (var m in messages)
            {
                var d = m as Dictionary<string, object>;
                if (d == null) continue;
                object content = d.ContainsKey("content") ? d["content"] : null;
                string text = FlattenContent(content);
                var role = d.ContainsKey("role") ? d["role"]?.ToString() : "user";
                flat.Add(new Dictionary<string, object> { ["role"] = role, ["content"] = text });
            }
            return new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = flat,
                ["stream"] = false,
                ["max_tokens"] = 600,
            };
        }

        /// <summary>content 三种形态（字符串／内存 List&lt;object&gt;[{type,text}]／JsonElement 数组）→ 拼成一段纯文本。</summary>
        public static string FlattenContent(object content)
        {
            if (content == null) return "";
            if (content is string s) return s;
            // 内存形态：调用方构造的 messages 里 content 是 List<object>，每项 Dictionary 带 "text"
            if (content is System.Collections.IEnumerable en && !(content is System.Text.Json.JsonElement))
            {
                var sb = new StringBuilder();
                foreach (var part in en)
                {
                    var d = part as Dictionary<string, object>;
                    if (d != null && d.TryGetValue("text", out var t))
                        sb.Append(t?.ToString() ?? "");
                    else if (part is string ps)
                        sb.Append(ps);
                }
                // ⚠ 枚举一无所获（未知元素形态）时回落 ToString，绝不静默输出空串 —— 空内容会伪装成「模型没说话」。
                return sb.Length > 0 ? sb.ToString() : content.ToString();
            }
            return content.ToString();
        }

        private static async Task<(bool Ok, string Text)> ChatOpenAiAsync(
            string baseUrl, string key, string model, IReadOnlyList<object> messages)
        {
            // ⚠ base 不带 /chat/completions（用户填 https://api.deepseek.com 即可），尾斜杠容忍。
            string url = baseUrl.TrimEnd('/') + "/chat/completions";
            string json = JsonSerializer.Serialize(BuildOpenAiBody(model, messages));
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    using (var resp = await _http.SendAsync(req))
                    {
                        string s = await resp.Content.ReadAsStringAsync();
                        if (!resp.IsSuccessStatusCode)
                            return (false, "HTTP " + (int)resp.StatusCode + "\n" + Clamp(s, 200));
                        using (var doc = JsonDocument.Parse(s))
                        {
                            if (doc.RootElement.TryGetProperty("choices", out var ch)
                                && ch.ValueKind == System.Text.Json.JsonValueKind.Array && ch.GetArrayLength() > 0
                                && ch[0].TryGetProperty("message", out var msg)
                                && msg.TryGetProperty("content", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                string text = c.GetString().Trim();
                                return text.Length > 0 ? (true, text) : (false, "模型没有返回内容。");
                            }
                            if (doc.RootElement.TryGetProperty("error", out var er))
                                return (false, Clamp(er.ToString(), 300));
                            return (false, "响应里没有 choices[0].message.content。\n" + Clamp(s, 200));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return (false, IsNetError(ex) ? "网络请求失败：" + ex.Message : "出错：" + ex.Message);
            }
        }

        /// <summary>把当前多轮 messages（OpenAI 风格 [{role, content:[{type,text}]}]）发给她，返回回复文本。</summary>
        public static async Task<(bool Ok, string Text)> ChatAsync(IReadOnlyList<object> messages, string model = null)
        {
            // 人格前缀在**发送边界**注入，而不是靠调用方记得 —— 调用方会忘。
            // 已有 system 则不重复插（这样将来若要临时改口吻，显式传一条即可）。
            messages = Persona.WithSystem(messages);

            // ---- 通道路由：配置了 OpenAI 兼容端点就走它，否则回落 Trae 中转 ----
            var src = OpenAiSource != null ? OpenAiSource() : null;
            if (ShouldUseOpenAi(src))
                return await ChatOpenAiAsync(src.Item1, src.Item2,
                    string.IsNullOrEmpty(src.Item3) ? ResolveModel(model) : src.Item3, messages);

            string file = SecretPath();
            if (!File.Exists(file)) return (false, "找不到凭据文件：%LOCALAPPDATA%\\AzhuPet\\balance_secret.txt\n（余额源配置里勾选至少一个 Trae 来源后会自动生成。）");

            // 从凭据文件解析请求头（第一行是 URL，其余是 headers —— 与 StatusProbe 同一格式）。
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in File.ReadAllLines(file))
            {
                string t = raw.Trim();
                if (t.Length == 0 || t.StartsWith("---", StringComparison.Ordinal)) continue;
                int c = raw.IndexOf(':');
                if (c > 0 && raw.Substring(0, c).Trim() != "Host")
                    headers[raw.Substring(0, c).Trim()] = raw.Substring(c + 1).Trim();
            }
            if (!headers.ContainsKey("authorization")) return (false, "凭据文件缺少 authorization(JWT)，请重新登录 Trae 后刷新凭据。");

            // 对话主链路要求的 app 上下文头，余额接口的凭据里没有，这里补齐（smoke 实测必需）。
            Ensure(headers, "x-app-id", "6eefa01c-1036-4c7e-9ca5-d891f63bfcd8");
            Ensure(headers, "x-ide-version", "3.3.67");
            Ensure(headers, "x-ide-version-code", "20260401");
            Ensure(headers, "x-machine-id", Guid.NewGuid().ToString().Replace("-", "").Substring(0, 32));
            Ensure(headers, "x-device-type", "windows");
            string uid = ExtractUid(headers["authorization"]);
            if (uid != null) Ensure(headers, "x-uid", uid);   // 缺 x-uid 时也用 JWT 的 user id 兜底

            var sid = Guid.NewGuid().ToString();
            var body = new Dictionary<string, object>
            {
                ["messages"] = messages,
                ["model"] = ResolveModel(model),
                ["function"] = "utils",
                ["stream"] = true,
                ["request_id"] = sid,
                ["session_id"] = sid,
                ["max_tokens"] = 600,
            };
            string json = JsonSerializer.Serialize(body);

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, Endpoint))
                {
                    foreach (var kv in headers)
                    {
                        if (string.Equals(kv.Key, "accept-encoding", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(kv.Key, "content-length", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(kv.Key, "content-type", StringComparison.OrdinalIgnoreCase))
                            continue;   // 压缩与长度交 HttpClient；content-type 由 StringContent 决定
                        req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                    }
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    using (var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                    {
                        string s = await resp.Content.ReadAsStringAsync();
                        if (!resp.IsSuccessStatusCode)
                            return (false, "HTTP " + (int)resp.StatusCode + "\n" + Clamp(s, 200));

                        // 逐行解析 SSE：data: {json}；取 response 字段拼接。遇 event:error 返回其 message。
                        var sb = new StringBuilder();
                        foreach (string line in s.Split('\n'))
                        {
                            if (!line.StartsWith("data:", StringComparison.Ordinal)) { if (line.StartsWith("event:error", StringComparison.Ordinal)) return (false, Clamp(s, 300)); continue; }
                            string payload = line.Substring(5).Trim();
                            if (payload.Length == 0) continue;
                            try
                            {
                                using (var doc = JsonDocument.Parse(payload))
                                {
                                    if (doc.RootElement.TryGetProperty("response", out var re) && re.ValueKind == JsonValueKind.String)
                                        sb.Append(re.GetString());
                                    if (doc.RootElement.TryGetProperty("message", out var me) && me.ValueKind == JsonValueKind.String && sb.Length == 0)
                                        return (false, me.GetString());
                                }
                            }
                            catch { /* 忽略无法解析的 data 分片 */ }
                        }
                        string text = sb.ToString().Trim();
                        return text.Length > 0 ? (true, text) : (false, "模型没有返回内容。");
                    }
                }
            }
            catch (Exception ex)
            {
                return (false, IsNetError(ex) ? "网络请求失败：" + ex.Message : "出错：" + ex.Message);
            }
        }

        private static bool IsNetError(Exception ex)
        {
            return ex is HttpRequestException || ex is System.Net.Sockets.SocketException || ex is TaskCanceledException;
        }

        private static void Ensure(Dictionary<string, string> h, string key, string val)
        {
            if (!h.ContainsKey(key)) h[key] = val;   // 已有值优先，避免覆盖凭据里的真值
        }

        /// <summary>从 "Cloud-IDE-JWT eyJ..." 中解开 payload，取 data.id 作为 x-uid。</summary>
        private static string ExtractUid(string authorization)
        {
            try
            {
                int sp = authorization.IndexOf(' ');
                if (sp < 0) return null;
                string token = authorization.Substring(sp + 1).Trim();
                var parts = token.Split('.');
                if (parts.Length < 2) return null;
                string payload = parts[1];
                payload = payload.Replace('-', '+').Replace('_', '/');
                int pad = payload.Length % 4;
                if (pad > 0) payload += new string('=', 4 - pad);
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                using (var doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.TryGetProperty("data", out var d)
                        && d.TryGetProperty("id", out var id)
                        && id.ValueKind == JsonValueKind.String)
                        return id.GetString();
                }
            }
            catch { }
            return null;
        }

        private static string Clamp(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > n ? s.Substring(0, n) : s;
        }
    }
}