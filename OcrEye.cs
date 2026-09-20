// D 档（文字档）—— 「看屏幕」的另一种读法：不把画面送出去，只把屏幕上**写着的字**读下来。
//
// 为什么它值得单独立一档，而不是当成 C 档的低配版：
//   ① **能力上真能做**。C 档（把像素发给视觉模型）在本机通道上已被实测排除
//      （--eye-solid 纯色块对照：红块答「蓝色」、绿块答「看不到图」——
//       通道收得下 image_url，但模型拿不到像素）。OCR 走 Windows 自带的本地识别器：
//      不联网、不要模型、不出本机。
//   ② **性质与 C 档不同**：像素必须整张送出去才有用；文字可以在**本机读完再决定**。
//      ⇒ 它不是降级的替代，是换了一条不依赖外部模型的路。
//   ③ 但它读到的**就是内容本身**。所以「读」与「发」必须是两个开关：
//        OcrOn       —— 允许她在**本机**读屏幕上的字（本地能力）
//        OcrSendText —— 允许把读到的**原文**放进发给模型的提示（跨线的那一步，默认关）
//      关着的时候她照样用本地读到的字**做判断**（这个窗口值不值得说、是不是空白），
//      只是原文不出本机。⚠ 两个开关管两件事 —— 「同一份数据两个落点」在这条线上会
//      直接变成隐私事故，所以宁可多一个开关，也不让一个开关同时管两件事。
//
// ⚠ 三条纪律（与像素档同族）：
//   ① 截图与文本**只在内存里活一次**，永不写盘。
//   ② 不读她自己（否则她会读到「她能看见什么」的测试文字）。
//   ③ **敏感窗口一律不读**（密码框／聊天窗／网银）—— 误判成本不对称，一律取保守侧。
//
// ⚠ OCR 输出的一个坑（已被判据抓到过一次，别再踩）：中文是按**字块**切的，
//   识别结果里字与字之间会插空格（「桌 宠 能 看 见」）。直接拿原文做包含判断
//   会把「读出来了」判成「没读出来」，也会把「读出来的是不是这个」判错。
//   所以 Tidy / Compact 两个纯函数是**判据的一部分**，不是美化。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace AzhuPet
{
    internal static class OcrEye
    {
        /// <summary>她能不能在**本机**读屏幕上的字。默认开：它不出本机、不要模型、不要钱。</summary>
        public static bool Enabled = true;

        /// <summary>⚠⚠ 跨线的那一步：把 OCR 原文放进发给模型的提示。**默认关**。</summary>
        public static bool SendText = false;

        /// <summary>进提示的原文上限（字符）。整屏文字塞进提示既贵又没重点。</summary>
        public static int PromptCap = 240;

        /// <summary>仅供负对照（`--no-ocr-gate`）：跳过 `ShouldSendText` 的门，直接发。
        /// ⚠ 它存在的唯一理由：证明「关掉时必须不发」那条判据**测的是门**，而不是「碰巧没文字」。</summary>
        public static bool GateOff = false;

        /// <summary>
        /// **纯函数**：这一步该不该把屏幕文字交给模型。五个输入都能自己钉死 ⇒ 可离线验。
        ///
        /// ⚠⚠ 为什么要把「门」抽出来：这条线上泄漏一次是不可撤回的（屏幕上的字里可能有
        ///   聊天记录、订单号、别人发来的原文）。规则不该散落在调用点 —— 只在这里判一次。
        ///   门有三道：① 本机读开着 ② 允许外发 ③ 目标窗口不是她自己、不是敏感窗口。
        /// </summary>
        public static bool ShouldSendText(bool enabled, bool sendText, string selfName,
                                          string proc, string title)
        {
            if (GateOff) return true;                       // 负对照专用
            if (!enabled) return false;
            if (!sendText) return false;
            if (string.IsNullOrEmpty(proc)) return false;
            if (proc == selfName) return false;             // 她自己
            if (IsSensitive(proc, title)) return false;     // 聊天窗／密码框／网银
            return true;
        }

        /// <summary>
        /// **纯函数**：不会发出去时的可读原因。
        /// ⚠ 判的必须是 `ShouldSendText` 那**同一组条件、同一顺序** —— 否则会出现
        ///   「门关着但说不出为什么」，而那正是本仓的老毛病（静默跳过把「没找到」渲染成「少一项」）。
        /// </summary>
        public static string SendWhyText(bool enabled, bool sendText, string selfName,
                                         string proc, string title)
        {
            if (!enabled) return "本机读屏幕文字已关（OcrOn）";
            if (!sendText) return "「说话时带上屏幕上的字」没开";
            if (string.IsNullOrEmpty(proc)) return "还不知道读的是哪个窗口";
            if (proc == selfName) return "读的是她自己";
            if (IsSensitive(proc, title)) return "这个窗口属于敏感类（不读也不发）";
            return "";
        }

        /// <summary>
        /// **纯函数**：把「发不发」写成一句人话，给气泡用。
        /// ⚠ 它存在的理由是隐私侧的可见性：用户看到屏幕原文的那一刻，必须同时看到
        ///   「这段话会不会离开本机」。写成纯函数 ⇒ 判据能钉死两个档位各显示什么。
        /// </summary>
        public static string SendNote(bool willSend, string why)
        {
            if (willSend) return "（这一屏的字会一起发给她）";
            return string.IsNullOrEmpty(why)
                ? "（只在你的电脑上读，没发出去）"
                : "（只在你的电脑上读，没发出去 —— " + why + "）";
        }

        /// <summary>
        /// **纯函数**：把读到的原文收拾成「能塞进一行提示」的形态 —— 折成单行、截断到上限。
        /// ⚠ 截断要**明写省略号**：不写的话下游会把「半句话」当成全部（同「不许静默降级」）。
        /// </summary>
        public static string ForPrompt(string text)
        {
            string one = Tidy(text);
            if (one.Length == 0) return "";
            return one.Length > PromptCap ? one.Substring(0, PromptCap) + "…" : one;
        }

        /// <summary>
        /// 读字前把图缩到多宽。**0 = 不缩**（默认）。
        /// ⚠ 与像素档的 1280 不同是有理由的：缩图会**掉字**，而掉了几个字看不出来 ——
        ///   那是静默降级。宁可慢一点、大一点。
        /// </summary>
        public static int MaxWidth = 0;

        /// <summary>
        /// **负对照开关**：跳过 Tidy/Compact 归一化，直接用引擎原文。
        /// 它是给 --ocrtest 用的 —— 关掉归一化后「认出合成图里的字」这条判据**必须变红**，
        /// 否则说明那条判据根本不是靠归一化通过的（它的绿没有信息量）。运行时恒为 false。
        /// </summary>
        public static bool RawTextNoNormalize = false;

        /// <summary>识别结果。⚠ 说不读也得有为什么 —— 静默跳过是本仓的老毛病。</summary>
        internal sealed class Result
        {
            public bool Ok;
            public string Text = "";         // 已归一化的可读文本（未去词间空格以外的东西）
            public int Chars;                // 去掉所有空白后的字符数
            public int Lines;
            public int Ms;
            public string Lang;
            public string Why = "";          // 不 Ok 时的原因；Ok 时写「读到了什么」

            // ---- 「读的是谁的」——**必须由这一层带出来** ----
            // ⚠⚠ 原来这两个数由调用方（PetWindow）自己去 Probe 一次。那就是**同一份数据两个落点**：
            //   标题栏说的是 A、图里可能是 B，而两处各问一次前台，谁也发现不了。
            //   现在只有一个来源：`Watcher.ReadTarget()` 交给这里的那个窗口。
            public string Proc, Title;
            /// <summary>这个读数是**回退**来的（当前前台是她自己／桌面），它是 N 秒前的那个窗口。</summary>
            public bool FromLast;
            public double AgeSec;
            /// <summary>这张图是屏幕拷贝来的 ⇒ 可能被别的窗口挡住。要显示给用户，不许静默。</summary>
            public string Caveat = "";

            /// <summary>
            /// ⚠⚠ 这一屏的原文**会不会**被放进发给模型的提示（跨线的那一步）。
            /// 由 `ReadForeground` 在**同一个读数**上判完带出来 —— 不许由显示层自己去问一次，
            /// 那就是「同一份数据两个落点」：气泡上说「不会发」，实际发送路径却因为另一次
            /// 判定（或另一次前台变化）真的发了，而两处谁也发现不了。
            /// </summary>
            public bool WillSend;
            /// <summary>不会发时的可读原因。静默的「不发」与「功能没接上」长得一样。</summary>
            public string SendWhy = "";

            public bool Any { get { return Chars > 0; } }

            public string Describe()
            {
                return Ok ? ("读到 " + Chars + " 字 / " + Lines + " 行，用时 " + Ms + " ms")
                          : ("没读到：" + Why);
            }
        }

        // ================================================================ 引擎

        private static OcrEngine _eng;
        private static string _engWhy;          // 引擎拿不到时的可读原因（含「本机没装语言包」）
        private static readonly object _lock = new object();

        public static string LangTag { get { return _eng == null ? null : _eng.RecognizerLanguage.LanguageTag; } }
        public static string Why { get { return _engWhy; } }

        /// <summary>本机有没有可用的中文识别器。**拿不到也要留下原因**，不许只是一个 false。</summary>
        public static bool Available()
        {
            EnsureEngine();
            return _eng != null;
        }

        private static void EnsureEngine()
        {
            if (_eng != null || _engWhy != null) return;
            try
            {
                var tags = new List<string>();
                try
                {
                    foreach (var l in OcrEngine.AvailableRecognizerLanguages) tags.Add(l.LanguageTag);
                }
                catch { }

                OcrEngine e = null;
                try { e = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("zh-Hans")); }
                catch { }
                if (e == null) { try { e = OcrEngine.TryCreateFromUserProfileLanguages(); } catch { } }

                if (e == null)
                {
                    _engWhy = "本机没有可用的 OCR 识别器（已装语言包：" +
                              (tags.Count == 0 ? "无" : string.Join(",", tags)) + "）";
                    return;
                }
                _eng = e;
            }
            catch (Exception ex) { _engWhy = "创建 OCR 引擎失败：" + ex.GetType().Name + " " + ex.Message; }
        }

        // ================================================================ 归一化（纯函数）

        private static bool IsCjk(char c)
        {
            return (c >= 0x4E00 && c <= 0x9FFF)      // 基本汉字
                || (c >= 0x3400 && c <= 0x4DBF)      // 扩展 A
                || (c >= 0x3000 && c <= 0x303F)      // 中文标点
                || (c >= 0xFF00 && c <= 0xFFEF);     // 全角
        }

        /// <summary>
        /// **纯函数**：把引擎原文收拾成可读的文本。
        /// 规则：① 所有空白压成一个空格 ② 去掉行首尾空格 ③ **去掉两个汉字之间的空格**
        /// （引擎按字块切，中文之间本来没有空格；保留它会让台词读起来是「桌 宠 能 看 见」）。
        /// 拉丁词之间的空格必须留下 —— 去掉会把 "hello world" 粘成一个词。
        /// </summary>
        public static string Tidy(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";

            // ① 空白归一（顺带把各种 Unicode 空白也收掉）
            var sb = new System.Text.StringBuilder(raw.Length);
            bool pendingSpace = false;
            foreach (char c in raw)
            {
                if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
                if (pendingSpace) sb.Append(' ');
                pendingSpace = false;
                sb.Append(c);
            }

            // ② 去掉「汉字 空格 汉字」里的那个空格
            for (int i = sb.Length - 2; i >= 1; i--)
            {
                if (sb[i] != ' ') continue;
                if (i + 1 < sb.Length && IsCjk(sb[i - 1]) && IsCjk(sb[i + 1])) sb.Remove(i, 1);
            }
            return sb.ToString();
        }

        /// <summary>
        /// **纯函数**：这段文本里还有没有「汉字 空格 汉字」。
        /// ⚠ 这是 `Tidy` 的**可验证的不变量**：有它，判据才能被逼红
        ///   （否则 `--no-ocr-normalize` 关掉归一化、判据却照样绿 —— 那条判据没资格失败）。
        /// </summary>
        public static bool HasSpaceBetweenCjk(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 1; i + 1 < s.Length; i++)
                if (s[i] == ' ' && IsCjk(s[i - 1]) && IsCjk(s[i + 1])) return true;
            return false;
        }

        /// <summary>
        /// **纯函数**：判据用的形态 —— 去掉**所有**空白。
        /// ⚠ 判据比对的必须是它，不是 Tidy 的结果：中文 OCR 的字间空格会让
        ///   `IndexOf` 把「读出来了」判成「没读出来」（本仓踩过一次，见 --ocrtest 头注释）。
        /// </summary>
        public static string Compact(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }

        // ================================================================ 敏感窗口（纯函数）

        /// <summary>
        /// **纯函数**：这个窗口能不能读。
        /// ⚠ 这里是**误判成本不对称**的地方：漏读一个普通窗口 = 她少一句话；
        ///   读到一次密码框／聊天记录 = 不可撤回。所以表可以宽，判法一律取保守侧。
        /// </summary>
        public static bool IsSensitive(string proc, string title)
        {
            return Hit(proc, SensitiveProcs) || Hit(title, SensitiveTitleWords);
        }

        private static readonly string[] SensitiveProcs =
        {
            "wechat", "weixin", "qq", "tim", "telegram", "discord", "signal", "whatsapp",
            "teams", "dingtalk", "dingtalkl", "slack", "feishu", "lark", "wework",
            "keepass", "1password", "bitwarden", "lastpass", "keeper", "enpass", "dashlane",
            "mstsc", "teamviewer", "anydesk", "sunloginclient", "todesk",
        };

        private static readonly string[] SensitiveTitleWords =
        {
            "密码", "口令", "验证码", "登录", "登入", "网银", "转账", "支付",
            "password", "passwd", "otp", "2fa", "sign in", "log in", "wallet",
        };

        private static bool Hit(string s, string[] table)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string low = s.ToLowerInvariant();
            foreach (string w in table) if (low.IndexOf(w, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // ================================================================ 识别

        /// <summary>
        /// 读一张已截好的屏幕图。⚠ **同步** API：内部丢到线程池上执行
        ///   （WinRT 的异步回调会回到发起它的单元；在 UI 线程上同步等它会死锁）。
        ///   耗时量级：一屏 200–400 ms，够小到可以放在一次手动操作里，**不适合每拍都跑**。
        /// </summary>
        public static Result Read(ScreenEye.Shot shot)
        {
            if (shot == null) return Fail("没有截图");
            if (shot.Pixels == null || shot.PixelW <= 0 || shot.PixelH <= 0) return Fail("截图的原始像素没带过来");
            EnsureEngine();
            if (_eng == null) return Fail(_engWhy);
            if (!Enabled) return Fail("读屏幕文字已关（OcrOn=false）");

            try
            {
                // ⚠ 丢到线程池：WinRT 的 IAsyncOperation 完成回调会 post 回发起它的单元，
                //   在 UI 线程上 .Result 就等于自锁（症状是桌宠界面僵住，而不是报错）。
                return Task.Run(() => Core(shot)).GetAwaiter().GetResult();
            }
            catch (Exception ex) { return Fail("识别失败：" + ex.GetType().Name + " " + ex.Message); }
        }

        private static Result Core(ScreenEye.Shot shot)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int w = shot.PixelW, h = shot.PixelH;
            // ⚠ MaxImageDimension 是 uint（WinRT 侧），别直接往 int 里塞 —— 首跑就是 CS0266。
            long max = 0;
            try { max = OcrEngine.MaxImageDimension; } catch { max = 0; }

            string why = null;
            if (max > 0 && (w > max || h > max))
            {
                // ⚠ 不许静默缩小：超限就明确不读（缩小会掉字，而「掉了几个字」看不出来）。
                return Fail("图 " + w + "×" + h + " 超过识别器上限 " + max + "，不读（缩小会静默掉字）");
            }

            SoftwareBitmap bmp = null;
            OcrResult res;
            try
            {
                bmp = SoftwareBitmap.CreateCopyFromBuffer(shot.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8,
                                                          w, h, BitmapAlphaMode.Premultiplied);
                lock (_lock) { res = _eng.RecognizeAsync(bmp).AsTask().GetAwaiter().GetResult(); }
            }
            catch (Exception ex) { return Fail("识别异常：" + ex.GetType().Name + " " + ex.Message); }
            finally { try { if (bmp != null) bmp.Dispose(); } catch { } }

            sw.Stop();

            if (res == null) return Fail("识别器返回了空结果");
            string raw = res.Text ?? "";
            string text = RawTextNoNormalize ? raw : Tidy(raw);
            int chars = Compact(text).Length;

            var r = new Result
            {
                Ok = true, Text = text, Chars = chars, Ms = (int)sw.ElapsedMilliseconds,
                Lang = LangTag, Why = why,
            };
            try { r.Lines = res.Lines == null ? 0 : res.Lines.Count; } catch { }
            // ⚠ 读不到字**不算失败**：看图、空白文档、游戏画面本来就一个字都没有。
            //   但必须与「引擎坏了」区分开 —— 所以 Ok=true 而 Chars=0，由调用方看 Any。
            r.Why = chars > 0 ? "读到内容" : "这一屏没有文字（不等于出错了）";
            return r;
        }

        private static Result Fail(string why)
        {
            return new Result { Ok = false, Why = why };
        }

        // ================================================================ 给说话人用

        /// <summary>
        /// 说话人取屏幕文字的唯一入口（`LlmSpeaker.ScreenSource` 默认指向它）。
        /// **未开启时返回空串，且连一次截屏都不做** —— 读一次就是一次截屏＋一次识别，
        /// 「先读了再丢掉」既浪费又让图上内存白白过一遍（同 ReadForeground 把敏感判定放在截图前）。
        /// ⚠ 可注入 ⇒ 判据能离线验「文字真的到了说话人手里」这段接线。
        /// </summary>
        public static string TextForSpeaking()
        {
            var t = Watcher.ReadTarget();
            string proc = t == null ? null : t.Proc;
            string title = t == null ? null : t.Title;
            return TextForSpeakingFrom(() => ReadForeground(null), proc, title);
        }

        /// <summary>
        /// `TextForSpeaking` 的**可注入**形态：读的来源与「读的是谁」都由外面钉死。
        ///
        /// ⚠⚠ 为什么非要有它：这一条链是**跨线**的（屏幕内容离开本机），而它的两个关键断言
        ///   ——「门关着时一个字都不发」与「门开着时发出去的正是读到的那段原文，未改写」——
        ///   若靠真屏幕去验，绿/红就取决于屏幕上此刻写着什么（本仓明令：判据的输入不许沿用
        ///   外部可变状态）。注入之后，两个档位、四种读的结果都能离线跑出来。
        ///
        /// ⚠ 顺序：**先过门、再去读**。反过来会在「本来就不该发」的窗口上白白截一次屏 ——
        ///   图在内存里出现过一次就已经发生了，而这正是 `ReadForeground` 把敏感判定放在
        ///   截图之前的那条纪律。
        /// </summary>
        public static string TextForSpeakingFrom(Func<Result> read, string proc, string title)
        {
            if (!ShouldSendText(Enabled, SendText, Watcher.SelfName(), proc, title)) return "";
            Result r;
            try { r = read == null ? null : read(); }
            catch { r = null; }             // 读的过程炸了也不该让她说不出话
            if (r == null || !r.Ok || !r.Any) return "";
            return ForPrompt(r.Text);
        }

        // ================================================================ 一步到位

        /// <summary>
        /// 读「她该看的那个窗口」并读出上面的字。
        ///
        /// ⚠⚠ 口径（2026-09-20 实拍修正）：目标由 `Watcher.ReadTarget()` 决定 ——
        ///   **当前前台是她自己时，用她上一条真实读数**。原因：托盘菜单属于 pet.exe，
        ///   你一点托盘前台就变成她自己的窗口；继续按「当前前台」读，结果必然是
        ///   「看见了：pet · 没读」—— 她只看见了自己。
        ///
        /// ⚠ `expectProc` 只在**自动路径**（她刚观察到 A、随后去截）上传入，用来拦「前台已经换走了」。
        ///   手动路径传 null：你手动问她「看这个」时不存在「她刚说的是 A」这个前提。
        /// </summary>
        public static Result ReadForeground(string expectProc)
        {
            if (!Enabled) return Fail("读屏幕文字已关（OcrOn=false）");

            var t = Watcher.ReadTarget();          // ⚠ 这里只问一次前台，票据就是这个窗口
            bool fromLast = t != null && Watcher.LastForeign != null
                            && ReferenceEquals(t, Watcher.LastForeign);

            if (t == null) return Fail("读不到前台窗口（正在切换／锁屏？），也没有更早的记录");
            if (t.Proc == Watcher.SelfName())
                return Fail("我现在只看得见自己（前台 = " + t.Proc + "）—— 回到你要我看的那个窗口再点一次");
            if (t.Shell)
                return Fail("前台是桌面／任务栏（" + t.Cls + "），这里没有可读的内容");

            // 敏感判定放在截图**之前**：连截图都不截（截了再丢，图仍在内存里出现过一次，没必要）。
            if (IsSensitive(t.Proc, t.Title))
                return Fail("这个窗口不读（敏感：" + t.Proc + "）");

            if (!string.IsNullOrEmpty(expectProc)
                && !string.Equals(t.Proc, expectProc, StringComparison.OrdinalIgnoreCase))
                return Fail("要看的是 " + expectProc + "，但现在拿到的是 " + t.Proc + "（前台换走了）");

            // ⚠⚠ 必须走带像素的那个重载。首跑 --ocrprobe 就是因为这里走了 1 参重载
            //   （wantPixels=false，只编 JPEG 不留像素），于是真屏幕上永远读不出东西，
            //   而报出来的原因是「截图的原始像素没带过来」—— 合成图那一组判据对此**完全无感**
            //   （它自己造带像素的 Shot）。这正是「逻辑全绿 ≠ 接上了」。
            var shot = ScreenEye.CaptureWindow(t.Hwnd, t.Proc, true, MaxWidth);
            if (shot == null)
                return Fail("没截到「" + t.Proc + "」（窗口已关闭／最小化／不可见／尺寸退化）");

            var r = Read(shot);
            r.Proc = shot.Proc;
            r.Title = shot.Title;
            r.FromLast = fromLast;
            r.AgeSec = t.AgeSec;
            if (shot.Occluded)
                r.Caveat = "这扇窗当时不在最前面，读到的可能被挡住";
            // ⚠ 在**同一个读数**上把「会不会发出去」判完 —— 与 TextForSpeaking 用的是同一个纯函数，
            //   所以气泡上写的那句与真实发送路径不可能各说一套。
            r.WillSend = ShouldSendText(Enabled, SendText, Watcher.SelfName(), t.Proc, t.Title);
            if (!r.WillSend) r.SendWhy = SendWhyText(Enabled, SendText, Watcher.SelfName(), t.Proc, t.Title);
            // ⚠ 就算识别本身失败了，也要把「读的是谁」带出来 —— 否则气泡只能显示一句
            //   「没读到」，用户无从判断是哪个窗口出的问题。
            return r;
        }
    }
}
