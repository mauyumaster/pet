// L4 验证（--eyetest / --eye-send）。
//
// ⚠ 这里要回答的是三个**互相独立**的问题，缺一个都不算通过：
//   ① 判定逻辑对不对（IsDegenerate 纯函数，离线可逼红）；
//   ② 有没有偷偷落盘（截图**只许在内存里活一次**）；
//   ③ **通道到底收不收图** —— 这是最大的未知：Trae 通道从来没发过图，
//      格式是我试出来的，不是猜出来的。所以 --eye-send 真发一张，看她的回复里
//      有没有图里**只有看图才知道**的东西（窗口标题）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AzhuPet
{
    internal static class EyeTest
    {
        public static int Run(Cli o)
        {
            // 参数可调：分辨率与质量直接决定「她能不能看清字」，也决定延迟与体积。
            if (o.EyeMax > 0) ScreenEye.MaxWidth = o.EyeMax;
            if (o.EyeQ > 0) ScreenEye.JpegQuality = o.EyeQ;

            var checks = new List<object>();
            bool ok = true;
            int n = 0;

            Action<string, bool, string> Check = delegate (string name, bool good, string detail)
            {
                if (!good) ok = false;
                n++;
                checks.Add(new Dictionary<string, object> { ["name"] = name, ["ok"] = good, ["detail"] = detail });
                Console.WriteLine((good ? "  [OK]   " : "  [FAIL] ") + name + "  " + detail);
            };

            Console.WriteLine("== A. 退化图判定（纯函数，喂合成像素）==");

            // 全黑：PrintWindow 对 DirectComposition 窗口的经典失败形态
            Check("allBlackIsDegenerate", Degenerate(Solid(320, 200, 0, 0, 0), 320, 200),
                "全黑必须被判退化（否则黑图会被当成内容发出去）");
            // 全白
            Check("allWhiteIsDegenerate", Degenerate(Solid(320, 200, 255, 255, 255), 320, 200),
                "全白同样没有信息");
            // 单色
            Check("solidColorIsDegenerate", Degenerate(Solid(320, 200, 40, 40, 40), 320, 200),
                "纯色");
            // 有正常内容：极差应当很大
            Check("variedIsNotDegenerate", !Degenerate(Varied(320, 200), 320, 200),
                "有内容的图不能被误判为退化（否则永远走 BitBlt 回退）");
            // 只差一点点（模拟轻微噪点）：仍算退化
            Check("faintNoiseStillDegenerate", Degenerate(NearlySolid(320, 200), 320, 200),
                "极差 < 8 仍算单色（抗噪：压缩噪点不该让黑图蒙混过关）");
            // 退化参数
            string why;
            Check("nullIsDegenerate", ScreenEye.IsDegenerate(null, 0, 0, 0, out why), "空参数");
            Check("tinyIsDegenerate", ScreenEye.IsDegenerate(new byte[4 * 2 * 2], 8, 2, 2, out why),
                "采样点不足 16");

            Console.WriteLine();
            Console.WriteLine("== B. 截图不许落盘（对比她落点目录的文件清单）==");

            string dir = Memory.Dir();
            var before = Snapshot(dir);
            string shotInfo = "(未截图)";
            ScreenEye.Shot shot = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { shot = ScreenEye.Capture(null); } catch (Exception ex) { shotInfo = "截图抛错：" + ex.Message; }
            sw.Stop();
            var after = Snapshot(dir);

            var added = new List<string>();
            foreach (string f in after) if (!before.Contains(f)) added.Add(Path.GetFileName(f));
            Check("noFileWritten", added.Count == 0,
                added.Count == 0 ? "截图后落点目录无新增文件" : "多出文件：" + string.Join(", ", added.ToArray()));

            if (shot != null)
            {
                shotInfo = shot.Describe() + "  " + sw.ElapsedMilliseconds + " ms";
                Check("shotHasPixels", shot.Bytes > 1024, "截到了内容：" + shot.Bytes + " 字节");
                Check("shotScaledDown", shot.OutW <= ScreenEye.MaxWidth,
                    "宽度 " + shot.SrcW + " → " + shot.OutW + "（上限 " + ScreenEye.MaxWidth + "）");
            }
            else
            {
                // ⚠ 截不到**不等于**失败：可能是前台正好是她自己／尺寸退化／读不到。
                //   但必须留可读原因，不能静默变成「少一项」。
                Check("shotOrReason", false, "没截到图（前台可能是她自己，或读不到窗口）。" + shotInfo);
            }
            Console.WriteLine("  截图：" + shotInfo);

            if (o.EyeSend)
            {
                Console.WriteLine();
                Console.WriteLine("== C. 真发一张图给模型（通道收不收图）==");
                Console.WriteLine("本轮模型 id = " + TraeChat.ResolveModel(o.LlmModel)
                    + (string.IsNullOrEmpty(TraeChat.ModelOverride) ? "（默认）" : "（--model 覆盖）")
                    + "；图片格式 fmt=" + o.EyeFmt);
                if (shot == null)
                {
                    Check("sendNeedsShot", false, "没有截图可发（先让一个普通窗口到前台再跑）");
                }
                else
                {
                    // ⚠⚠ 第一版 prompt 写的是「如果没有图片就回答『没有图片』」——**那是诱导**：
                    //   它递给她一个「我没有图」的出口，于是她在收到图的情况下也照着答了，
                    //   我拿到一个**假阴性**，差点得出「通道不吃图」的结论。
                    //   第二版改成中性问「包含哪些内容」⇒ 她答「纯文字，和一张图片」⇒ 通道其实收图。
                    //   现在这版是**任务式**：要她念出图里的标题。
                    //   这不是诱导 —— 这个任务**只能在图上完成**，看不到图的唯一出路就是说「看不到」。
                    string prompt = "这条消息里有一张截图。请只回答一个问题："
                                  + "截图里那个窗口的标题栏上写着什么文字？照抄即可。";
                    bool hit = TrySend(shot, prompt, o.EyeFmt, Check);
                    if (!hit) Console.WriteLine("  （若返回「没有图片」⇒ 通道不吃这种图片格式，需换 fmt 再试）");
                }
            }

            if (o.EyeSynth)
            {
                Console.WriteLine();
                Console.WriteLine("== D. 对照：发一张**自己合成的大字图**（排除「屏幕太复杂／字太小」这个解释）==");
                const string secret = "紫色大象";
                var synth = Synth(secret);
                Check("synthShotMade", synth != null && synth.Bytes > 500,
                    synth == null ? "合成失败" : "白底黑字「" + secret + "」，" + synth.Bytes + " 字节");
                if (synth != null)
                {
                    var sw2 = System.Diagnostics.Stopwatch.StartNew();
                    string t2 = Send(synth, "这条消息里有一张图。请只回答：图里写的字是什么？照抄即可。", o.EyeFmt);
                    sw2.Stop();
                    Console.WriteLine("  耗时 " + sw2.ElapsedMilliseconds + " ms");
                    Console.WriteLine("  她的回复：" + t2);
                    bool got = t2.IndexOf(secret, StringComparison.Ordinal) >= 0;
                    Check("readSynthText", got,
                        got ? "读出了合成图里的字 ⇒ 通道真能传图，问题只出在屏幕截图那一侧"
                            : "读不出 110 号大字 ⇒ **通道不传图像内容**，与分辨率／质量无关");
                }
            }

            // ================= E. 纯色对照（决定性）=================
            // 「读不出文字」有两种完全不同的解释，必须分开：
            //   ㈠ 像素到了，但模型 OCR 能力弱  ⇒ 换更强的视觉模型**有用**；
            //   ㈡ 像素根本没到，她只是解析了 content 数组的结构 ⇒ 换模型**没用**（图在通道那层就没了）。
            // ⚠ 判据必须选一个「不看图就答不出」的任务：纯色块的**具体颜色**。
            //   而且用两张**互斥**的颜色（红／绿）—— 盲猜要连中两次且对应正确，概率上等于不可能。
            if (o.EyeSolid)
            {
                Console.WriteLine();
                Console.WriteLine("== E. 纯色对照：问「这张图整体是什么颜色」==");
                var probes = new[]
                {
                    Tuple.Create(Color.FromRgb(255, 0, 0), "红", new[] { "红", "red" }),
                    Tuple.Create(Color.FromRgb(0, 200, 0), "绿", new[] { "绿", "green" }),
                };
                int seen = 0;
                foreach (var p in probes)
                {
                    var sol = SynthSolid(p.Item1, 512, 512);
                    if (sol == null) { Check("solidShotMade", false, "合成纯色块失败"); break; }
                    var sw3 = System.Diagnostics.Stopwatch.StartNew();
                    string t3 = Send(sol, "这条消息里有一张图。请只回答一个问题：这张图整体是**什么颜色**？只答颜色名。", o.EyeFmt);
                    sw3.Stop();
                    bool okc = p.Item3.Any(w => t3.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (okc) seen++;
                    Console.WriteLine("  纯" + p.Item2 + "色块（" + sol.Bytes + " 字节）→ " + sw3.ElapsedMilliseconds + " ms → " + t3);
                    Check("solidColorSeen_" + p.Item2, okc, okc ? "认出了颜色" : "没认出纯" + p.Item2 + "色块");
                }
                Check("pixelsArrive", seen >= 2,
                    seen >= 2
                        ? "两张纯色块都答对 ⇒ **像素确实到了模型**；读不出文字只是识别能力问题 ⇒ 换更强的视觉模型可能有用"
                        : "纯色块也认不出（" + seen + "/2）⇒ **像素没到模型**，换模型在通道不变的前提下大概率无用");
            }

            string rf = o.EyeSolid ? "azhu_eyetest_solid.json"
                      : o.EyeSynth ? "azhu_eyetest_synth.json"
                      : o.EyeSend ? "azhu_eyetest_send.json"
                      : "azhu_eyetest.json";
            return Report(ok, checks, rf, n);
        }

        /// <summary>真发图。返回「回复里命中了标题片段」——那是**只有看图才知道**的东西。</summary>
        private static bool TrySend(ScreenEye.Shot shot, string prompt, int fmt, Action<string, bool, string> Check)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string text = Send(shot, prompt, fmt);
            sw.Stop();

            Console.WriteLine("  格式 fmt=" + fmt + "（1=Anthropic image/source，2=OpenAI image_url）");
            Console.WriteLine("  耗时 " + sw.ElapsedMilliseconds + " ms");
            Console.WriteLine("  她的回复：" + text);

            // ⚠ 只认**明确的否定**。第一版把「纯文字」「仅文字」也当成了拒绝词，
            //   于是她那句「纯文字，和一张图片」被判成"通道拒绝图片"——**判据自己有病**，
            //   和本项目历次「判据在现象没发生时也成立」同族。
            //   描述内容类型（"纯文字和一张图片"）是**收到**的表现，不是拒绝。
            bool refused = text.IndexOf("没有图片", StringComparison.Ordinal) >= 0
                        || text.IndexOf("没有图像", StringComparison.Ordinal) >= 0
                        || text.IndexOf("无图像", StringComparison.Ordinal) >= 0
                        || text.IndexOf("未收到图", StringComparison.Ordinal) >= 0
                        || text.IndexOf("没有收到图", StringComparison.Ordinal) >= 0
                        || text.IndexOf("看不到图", StringComparison.Ordinal) >= 0
                        || text.IndexOf("无法看到图", StringComparison.Ordinal) >= 0
                        || text.StartsWith("(失败)", StringComparison.Ordinal)
                        || text.StartsWith("(抛错)", StringComparison.Ordinal);
            Check("channelAcceptsImage", !refused, refused ? "通道拒绝了图片（或调用失败）" : "通道接受了图片");

            // 只有看图才知道的东西：窗口标题里的片段
            bool hit = false;
            string title = shot.Title ?? "";
            foreach (string frag in Frags(title))
            {
                if (text.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0) { hit = true; break; }
            }
            Check("sawSomethingOnlyVisible", hit,
                hit ? "回复里出现了标题片段（确实看见了图）"
                    : "回复里没有标题的任何片段（标题＝「" + Clamp(title, 40) + "」）—— 可能只是被当成普通文本回了个客气话");
            return hit;
        }

        /// <summary>真发一条带图消息。发送逻辑**只此一处**（屏幕截图与合成对照共用）。</summary>
        private static string Send(ScreenEye.Shot shot, string prompt, int fmt)
        {
            var content = new List<object>
            {
                new Dictionary<string, object> { ["type"] = "text", ["text"] = prompt },
                ImageBlock(ScreenEye.Base64(shot), fmt),
            };
            var msgs = new List<object>
            {
                new Dictionary<string, object> { ["role"] = "user", ["content"] = content.ToArray() },
            };
            try
            {
                var r = TraeChat.ChatAsync(msgs).GetAwaiter().GetResult();
                return r.Item1 ? r.Item2 : "(失败) " + r.Item2;
            }
            catch (Exception ex) { return "(抛错) " + ex.Message; }
        }

        /// <summary>
        /// **合成一张对照图**：白底 + 一个 110 号的黑字词。
        /// 用途：排除「屏幕截图太复杂／字太小所以看不清」这个解释。
        /// 如果连 110 号字都读不出来，那就**不是清晰度问题** —— 是通道不传图像内容。
        /// （这是本项目那条纪律的又一次应用：**负对照要打在「行为是否存在」，不是「参数是否为 0」**。）
        /// </summary>
        private static ScreenEye.Shot Synth(string text)
        {
            try
            {
                const int W = 900, H = 260;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));
                    var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), 110, Brushes.Black, 1.0);
                    dc.DrawText(ft, new Point(40, 60));
                }
                var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();

                var enc = new JpegBitmapEncoder();
                enc.QualityLevel = 92;
                enc.Frames.Add(BitmapFrame.Create(rtb));
                byte[] jpg;
                using (var ms = new MemoryStream()) { enc.Save(ms); jpg = ms.ToArray(); }

                return new ScreenEye.Shot
                {
                    Jpeg = jpg,
                    SrcW = W, SrcH = H, OutW = W, OutH = H,
                    Proc = "合成", Title = text, How = "RenderTargetBitmap",
                };
            }
            catch { return null; }
        }

        /// <summary>合成一张**纯色块**（512×512）。用途：把「读不出字」拆成「像素没到」还是「识别能力弱」。</summary>
        private static ScreenEye.Shot SynthSolid(Color c, int w, int h)
        {
            try
            {
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(0, 0, w, h));
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();

                var enc = new JpegBitmapEncoder();
                enc.QualityLevel = 92;
                enc.Frames.Add(BitmapFrame.Create(rtb));
                byte[] jpg;
                using (var ms = new MemoryStream()) { enc.Save(ms); jpg = ms.ToArray(); }

                return new ScreenEye.Shot
                {
                    Jpeg = jpg,
                    SrcW = w, SrcH = h, OutW = w, OutH = h,
                    Proc = "合成", Title = "纯色块", How = "RenderTargetBitmap",
                };
            }
            catch { return null; }
        }

        /// <summary>从标题里切出可用于核对的片段（≥3 字、去掉纯符号与太短的词）。</summary>
        private static IEnumerable<string> Frags(string title)
        {
            if (string.IsNullOrEmpty(title)) yield break;
            var parts = title.Split(new[] { ' ', '-', '—', '|', '·', '_', '（', '）', '(', ')', '：', ':' },
                                    StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length >= 3 && p.Length <= 24) yield return p;
            }
        }

        private static object ImageBlock(string b64, int fmt)
        {
            if (fmt == 3)
            {
                // image_url 直接是字符串（有些兼容层只认这一种）
                return new Dictionary<string, object>
                {
                    ["type"] = "image_url",
                    ["image_url"] = "data:image/jpeg;base64," + b64,
                };
            }
            if (fmt == 2)
            {
                return new Dictionary<string, object>
                {
                    ["type"] = "image_url",
                    ["image_url"] = new Dictionary<string, object> { ["url"] = "data:image/jpeg;base64," + b64 },
                };
            }
            return new Dictionary<string, object>
            {
                ["type"] = "image",
                ["source"] = new Dictionary<string, object>
                {
                    ["type"] = "base64",
                    ["media_type"] = "image/jpeg",
                    ["data"] = b64,
                },
            };
        }

        // ---------------------------------------------------------------- 合成像素

        private static byte[] Solid(int w, int h, byte b, byte g, byte r)
        {
            byte[] px = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++) { px[i * 4] = b; px[i * 4 + 1] = g; px[i * 4 + 2] = r; px[i * 4 + 3] = 255; }
            return px;
        }

        private static byte[] NearlySolid(int w, int h)
        {
            byte[] px = Solid(w, h, 30, 30, 30);
            for (int i = 0; i < 200; i++) { px[i * 4] = 33; }   // 极差 3
            return px;
        }

        private static byte[] Varied(int w, int h)
        {
            byte[] px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    px[o] = (byte)(x % 256);
                    px[o + 1] = (byte)(y % 256);
                    px[o + 2] = (byte)((x + y) % 256);
                    px[o + 3] = 255;
                }
            return px;
        }

        private static bool Degenerate(byte[] px, int w, int h)
        {
            string why;
            return ScreenEye.IsDegenerate(px, w * 4, w, h, out why);
        }

        private static List<string> Snapshot(string dir)
        {
            try { return Directory.Exists(dir) ? new List<string>(Directory.GetFiles(dir)) : new List<string>(); }
            catch { return new List<string>(); }
        }

        private static string Clamp(string s, int n)
        {
            if (s == null) return "";
            return s.Length > n ? s.Substring(0, n) + "…" : s;
        }

        private static int Report(bool ok, List<object> checks, string file, int n)
        {
            string path = Path.Combine(Path.GetTempPath(), file);
            try
            {
                var doc = new Dictionary<string, object>
                {
                    ["ok"] = ok,
                    ["total"] = n,
                    ["at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["checks"] = checks,
                };
                File.WriteAllText(path, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }),
                                  new UTF8Encoding(false));
            }
            catch { }

            Console.WriteLine();
            Console.WriteLine((ok ? "全部通过" : "有失败项") + "：" + (n - (ok ? 0 : 1)) + "/" + n + "  报告 → " + path);
            return ok ? 0 : 1;
        }
    }
}
