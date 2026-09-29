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
using System.Threading;
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

        /// <summary>是否在兼容端点的请求体里带 <c>"thinking":{"type":"disabled"}</c>（默认带）。
        /// ⚠⚠ 为什么默认必须带：现代混合推理模型（智谱 GLM-4.7 及以上、DeepSeek V4 系列）**默认开思考**，
        ///   而思考 token **计入 max_tokens** —— 本程序写死 600，思考跑完留给正文的预算就见底，
        ///   现象是「模型没有返回内容」，这句报错**指向不了真因**（2026-09-29 实测智谱 glm-4.7-flash）。
        /// 负对照 <c>--old-openai-body</c> 会把它置 false（回到 2026-09-29 之前的请求体）。</summary>
        public static bool SendThinkingDisabled = true;

        /// <summary>最近一次请求有没有因为「模型不接受关思考」而退避重试（供 <c>--llmtest</c> 观察）。空 = 没发生。
        /// ⚠ 为什么要留这个位：退避是**静默**发生的 —— 成功时不打印任何东西，用户就不知道
        ///   「这个模型其实拒绝过 disabled」。没有这条线索，同一个模型在两台机器上表现不一致时会无从归因。</summary>
        public static string LastThinkingNote = "";

        // ---- 限流（HTTP 429）处置（2026-09-29：用户实测 glm-4.7-flash 返回 429 / code 1305）----
        // 官方错误码表（docs.bigmodel.cn/cn/faq/api-code）把 429 家族分得很细，处置**完全不同**：
        //   · 1302「您的账户已达到速率限制，请您控制请求频率」← 我们自己发太快（RPM）
        //   · 1305「该模型当前访问量过大，请您稍后再试」    ← **平台侧算力过载**，与我们的频率无关
        //   这两条「等一下就好」；而 1113 欠费 / 1308 配额 / 1309 套餐到期 / 1310 周月上限 /
        //   1311 无权限 / 1313 公平策略 / 1314·1315 企业套餐 / 1316-1321 各类上限「等多久都没用」。
        // ⇒ 「429 就一律重试」是**错的**：对后一组重试只是白花配额，还会把「该充值了」拖成
        //   一个看不出原因的慢失败。必须按业务码分家。

        /// <summary>限流重试的**额外**次数（首次失败之后再发几次）。0 ＝ 关掉重试。</summary>
        public static int RateLimitRetries = 2;

        /// <summary>首次退避毫秒；之后每次 ×2（1.2s → 2.4s）。
        /// ⚠ 可写是给判据用的：脚本化发送器跑三轮不该真睡 3.6 秒。</summary>
        public static int RateLimitDelayMs = 1200;

        /// <summary>最近一次请求有没有因为限流而重试／回落备用通道（供 <c>--llmtest</c> 观察）。空 = 没发生。
        /// ⚠ 与 <see cref="LastThinkingNote"/> 同一个理由：重试是**静默**的，不记下来就没人知道发生过 ——
        ///   而这正是本项目反复吃亏的形态（「点了没反应」「静默回落到 Trae」）。</summary>
        public static string LastRetryNote = "";

        /// <summary>429 家族里「等多久都没用」的业务码（智谱官方错误码表，2026-09-29 核）。
        /// ⚠ 用**业务码文本**而不是「模型名/服务名」当判据：名单一定会过期，而错误码是服务端契约。</summary>
        private static readonly string[] NonRetriable429Codes =
        {
            "1113",                                                           // 账户欠费，请充值后重试
            "1308", "1310", "1316", "1317", "1318", "1319", "1320", "1321",   // 各类配额／使用上限已满
            "1309", "1311", "1313", "1314", "1315",                           // 套餐到期／无权限／公平策略／企业套餐／key 类型不符
        };

        /// <summary>**判据专用注入位**：把「发一次 HTTP」换掉（null ＝ 用真的 <see cref="OpenAiOnce"/>）。
        /// ⚠ 与 <see cref="OpenAiSource"/> 同一模式。重试与回落是**行为**，只能靠脚本化的发送器才验得了 ——
        ///   真去打网络的话，判据就变成「此刻这个模型挤不挤」，那是外部状态，不是我们的契约。</summary>
        public static Func<string, string, Dictionary<string, object>,
            Task<(bool Ok, int Status, string Body, string Text)>> OpenAiSender = null;

        /// <summary>备用通道（主通道被限流时改走它）。null 或 base/key 任一为空 ＝ 不用。
        /// 与 <see cref="OpenAiSource"/> 同型，接线同样只在 Program.WireOpenAi 一处。</summary>
        public static Func<Tuple<string, string, string>> FallbackSource = null;

        /// <summary>同一时刻只允许一通「和模型说话」在飞（理由见 <see cref="ChatOpenAiAsync"/>）。</summary>
        private static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        /// <summary>**纯函数**：这通失败值不值得「等一下再发一次」。
        /// ⚠ 刻意不是「429 ⇒ 重试」：同一个 HTTP 状态码下，服务端把「稍等会好」（1302/1305）
        ///   与「等到明天也不会好」（1113 欠费、1308/1310/1316-1321 配额、1309 套餐、1311 权限、
        ///   1313 公平策略、1314/1315 企业套餐）分成了不同业务码。见 NonRetriable429Codes。
        /// ⚠ 只认 429/503：400（参数）／401（key）／404（地址）重试毫无意义，
        ///   而且会把真因盖成「服务端挤」—— 与 LooksLikeThinkingRejected 同一条纪律。</summary>
        public static bool LooksLikeTransientOverload(int status, string body)
        {
            if (status == 503) return true;   // 服务不可用：直连场景下只可能来自服务端，标准语义就是稍后再试
            if (status != 429) return false;
            if (string.IsNullOrEmpty(body)) return true;   // 光有 429 没有正文 ⇒ 按标准语义处理
            foreach (string code in NonRetriable429Codes)
                if (body.IndexOf("\"" + code + "\"", StringComparison.Ordinal) >= 0) return false;
            return true;
        }

        /// <summary>**纯函数**：这具请求体有没有明确要求「关思考」。
        /// ⚠ 判据刻意不读写入端用的那个常量：两边共用一个常量的话，改错了会一起错，等于没判。</summary>
        public static bool AsksThinkingOff(Dictionary<string, object> body)
        {
            if (body == null || !body.TryGetValue("thinking", out var t)) return false;
            var d = t as Dictionary<string, object>;
            if (d == null || !d.TryGetValue("type", out var ty)) return false;
            return string.Equals(ty?.ToString(), "disabled", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>**纯函数**：这通失败的响应是不是在说「这个模型不接受关思考」。
        /// ⚠ 判据刻意**宽松**（只看状态码 + 文本里有没有 thinking／reasoning／思考）：宁可在别的 400 上
        ///   白重试一次（一次请求，可忽略），也不要因措辞不同而漏判 —— 漏判的后果是那个模型直接不可用。
        /// ⚠ 只认 400/422：401（key）／404（地址）／429（限流）重试无意义，且会把真因盖掉。</summary>
        public static bool LooksLikeThinkingRejected(int status, string body)
        {
            if (status != 400 && status != 422) return false;
            if (string.IsNullOrEmpty(body)) return false;
            string t = body.ToLowerInvariant();
            return t.Contains("thinking") || t.Contains("reasoning") || t.Contains("思考");
        }

        /// <summary>**纯函数**：这句报错是不是「模型没吐正文」（＝很可能思考吃光了预算）。</summary>
        public static bool LooksLikeEmptyContent(string msg)
        {
            return !string.IsNullOrEmpty(msg) && msg.IndexOf("没有返回内容", StringComparison.Ordinal) >= 0;
        }

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
        /// <paramref name="disableThinking"/> ＝ false 是**退避路径**要用的（模型拒绝关思考时改发这一具）。
        /// </summary>
        public static Dictionary<string, object> BuildOpenAiBody(
            string model, IReadOnlyList<object> messages, bool disableThinking = true)
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
            var body = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = flat,
                ["stream"] = false,
                ["max_tokens"] = 600,
            };
            // 「关思考」的写法取自智谱官方文档（docs.bigmodel.cn/cn/guide/capabilities/thinking）：
            //   `"thinking":{"type":"disabled"}`；非流式响应把思考放在 message.reasoning_content，
            //   正文仍在 message.content ⇒ **正文本身是干净的**，问题只是预算被思考吃光。
            // ⚠ 已知道的反例：GLM-5.3 / 5.3-FLASH **不再支持关闭思考**，传 disabled 直接报错
            //   ⇒ 由 ChatOpenAiAsync 的自适应退避兜住。所以这里**不许硬编码模型名单**（名单一定过期）。
            if (disableThinking && SendThinkingDisabled)
                body["thinking"] = new Dictionary<string, object> { ["type"] = "disabled" };
            return body;
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

        /// <summary>兼容端点**单次**请求。连状态码与响应体原文一起返回 —— 退避判据要看响应体里
        /// 有没有提 thinking，所以不能只回「成不成」（那点信息在这里是判据的输入）。</summary>
        private static async Task<(bool Ok, int Status, string Body, string Text)> OpenAiOnce(
            string url, string key, Dictionary<string, object> body)
        {
            string json = JsonSerializer.Serialize(body);
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
                            return (false, (int)resp.StatusCode, s,
                                "HTTP " + (int)resp.StatusCode + "\n" + Clamp(s, 200));
                        using (var doc = JsonDocument.Parse(s))
                        {
                            if (doc.RootElement.TryGetProperty("choices", out var ch)
                                && ch.ValueKind == System.Text.Json.JsonValueKind.Array && ch.GetArrayLength() > 0
                                && ch[0].TryGetProperty("message", out var msg)
                                && msg.TryGetProperty("content", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                string text = c.GetString().Trim();
                                return text.Length > 0
                                    ? (true, 200, s, text)
                                    : (false, 0, s, "模型没有返回内容。");
                            }
                            if (doc.RootElement.TryGetProperty("error", out var er))
                                return (false, 0, s, Clamp(er.ToString(), 300));
                            return (false, 0, s, "响应里没有 choices[0].message.content。\n" + Clamp(s, 200));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return (false, -1, "",
                    IsNetError(ex) ? "网络请求失败：" + ex.Message : "出错：" + ex.Message);
            }
        }

        /// <summary>发一次请求 —— 判据可以把它换掉（见 <see cref="OpenAiSender"/>）。</summary>
        private static Task<(bool Ok, int Status, string Body, string Text)> Send(
            string url, string key, Dictionary<string, object> body)
        {
            var sender = OpenAiSender;
            return sender != null ? sender(url, key, body) : OpenAiOnce(url, key, body);
        }

        /// <summary>在**同一条通道**上把请求发出去，处理两条互相正交的重试轴：
        ///   轴 1「模型不接受关思考」＝ 换一具请求体（最多一次，理由见 <see cref="SendThinkingDisabled"/>）；
        ///   轴 2「限流」            ＝ 同一具请求体稍后再发（最多 <see cref="RateLimitRetries"/> 次）。
        /// 两条轴分开计数是刻意的：关思考的退避**不是**限流重试，混用会让「两次都败」时报错失去意义。
        /// 返回 RateLimited ＝ 最终失败的原因是不是限流 —— 调用方据此决定要不要回落备用通道。</summary>
        private static async Task<(bool Ok, string Text, bool RateLimited)> TryChannel(
            string baseUrl, string key, string model, IReadOnlyList<object> messages)
        {
            // ⚠ base 不带 /chat/completions（用户填 https://api.deepseek.com 即可），尾斜杠容忍。
            string url = baseUrl.TrimEnd('/') + "/chat/completions";
            bool askThinkingOff = true;
            int tries = 0;
            while (true)
            {
                var one = await Send(url, key, BuildOpenAiBody(model, messages, askThinkingOff));
                if (one.Ok) return (true, one.Text, false);

                // ---- 轴 1：少数模型**不接受关闭思考**（智谱 GLM-5.3 / 5.3-FLASH 传 disabled 直接报错）----
                // 换一具**不带该参数**的请求体再发一次。不计入限流次数 —— 它与限流是两回事。
                if (askThinkingOff && LooksLikeThinkingRejected(one.Status, one.Body))
                {
                    LastThinkingNote = "该模型拒绝了 thinking=disabled（HTTP " + one.Status + "），已去掉该参数重试一次。";
                    askThinkingOff = false;
                    continue;
                }

                // ---- 轴 2：限流 ⇒ 等一下再发（官方处置原文就是「稍后再试」）----
                // ⚠ 只对 LooksLikeTransientOverload 为真的失败重试：欠费/配额/无权限那几种 429
                //   重试只是白花配额，还把真因拖成一个看不出原因的慢失败。
                if (tries < RateLimitRetries && LooksLikeTransientOverload(one.Status, one.Body))
                {
                    tries++;
                    LastRetryNote = "限流（HTTP " + one.Status + "），已重试 " + tries + "/" + RateLimitRetries + " 次。";
                    int wait = RateLimitDelayMs * (1 << Math.Min(tries - 1, 4));   // 上限 ×16，防大次数时位移溢出
                    if (wait > 0) await Task.Delay(wait);
                    continue;
                }

                string text = one.Text;
                if (LooksLikeEmptyContent(text))
                    text += "\n（本次请求带了「关闭思考」参数 thinking=disabled。若该模型强制思考，"
                          + "思考 token 会先占满 max_tokens=600，正文因此为空 —— 换一个能关思考的模型，"
                          + "或按它家文档改写法。报错本身不会告诉你这一点。）";
                bool limited = LooksLikeTransientOverload(one.Status, one.Body);
                if (limited)
                    // ⚠ 这一条同样是「让报错自己说出线索」：1305 看起来像我们配错了，其实是平台算力过载 ——
                    //   不写出来，用户只会去反复检查 key 和地址。
                    text += "\n（已自动重试 " + tries + " 次仍被挡。这条是**服务端过载/限流**，与你的 key、地址无关 ——"
                          + "过一会儿再发一次通常就好了。想让她在这种情况下也答得上话，"
                          + "可在「模型通道」里再填一条备用通道。）";
                return (false, text, limited);
            }
        }

        /// <summary>兼容端点取一次回复：**先主通道，被限流才改走备用通道**。
        /// ⚠ 串行化（<see cref="_gate"/>）：桌宠自己有**三路**在调这里（打字聊天／台词／每小时小结），
        ///   而免费档的 RPM 很紧 —— 自己把自己顶到 1302 是自伤，代价只是让后到的那路等一下。
        /// ⚠ 只在**限流**时回落备用通道：key 错／地址错这类回落毫无意义，
        ///   只会把一个真因换成另一个真因（更难查）。</summary>
        internal static async Task<(bool Ok, string Text)> ChatOpenAiAsync(
            string baseUrl, string key, string model, IReadOnlyList<object> messages)
        {
            LastThinkingNote = "";
            LastRetryNote = "";
            await _gate.WaitAsync();
            try
            {
                var main = await TryChannel(baseUrl, key, model, messages);
                if (main.Ok) return (true, main.Text);

                var fb = FallbackSource != null ? FallbackSource() : null;
                if (main.RateLimited && ShouldUseOpenAi(fb))
                {
                    LastRetryNote += (LastRetryNote.Length > 0 ? " " : "") + "主通道限流，已改走备用通道。";
                    var alt = await TryChannel(fb.Item1, fb.Item2,
                        string.IsNullOrEmpty(fb.Item3) ? model : fb.Item3, messages);
                    if (alt.Ok) return (true, alt.Text);
                    // 两条都不通：报**主通道**的错（那才是用户配的那条，回落只是附加动作），
                    // 但把备用通道的失败原因附在后面 —— 否则「两条都填了还不行」时无从下手。
                    return (false, main.Text + "\n（备用通道也没通：" + Clamp(alt.Text, 160) + "）");
                }
                return (false, main.Text);
            }
            finally { _gate.Release(); }
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

            // 从凭据文件解析请求头。
            // ⚠⚠ **同一份数据不允许有第二个解析口径** —— 走 CredentialCapture.HeadersFromSecretText，
            //   它内部就是 ParseRawSecretText（与 StatusProbe.TraeBalanceAsync / 自定义余额源同一份实现），
            //   只是顺手剔掉 Host（HTTP/1.1 的 Host 由 HttpClient 按 URL 自己生成，手写一个会和它打架）。
            //   此前这里自己手写了一遍循环：所有 `---` 段标记被跳过 ⇒ 不认 ---body---，
            //   于是 body 段的 JSON 被当成「一行请求头」混进 dictionary（键名非法，最终被
            //   TryAddWithoutValidation 静默丢掉 —— 不报错，只是悄悄脏了一层）。
            //   聊天这一发不用文件里的 URL / method / body（地址取 Endpoint 常量、body 自己造）。
            var headers = CredentialCapture.HeadersFromSecretText(File.ReadAllText(file));
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