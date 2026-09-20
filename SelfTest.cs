// 自检 —— 桌宠这种东西「看着对」最不可靠：透明窗里发黑、影子脱节、幅度小到看不出来、
// 点了半天其实是点在透明区（于是挡住了干活）。所以把每一条都变成算术：
//   ① 几何：顶点/三角面/包围盒/贴图来源
//   ② 姿态：呼吸幅度换算成屏幕像素（不上算术等于没测）
//   ③ 命中：6 个采样点的判定 ＋ 单次耗时
//   ④ 全屏隐退：真的开一个铺满屏幕的窗口，看它有没有退让、有没有回来
//   ⑤ 像素：裁下来数——透明处是不是真的透（绿底反查）、有没有黑块、轮廓占多高
// 退出码：0 = 全过，1 = 有 FAIL。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

namespace AzhuPet
{
    internal static class SelfTest
    {
        private static string[] Row(string name, string val, bool ok)
        {
            return new[] { name, val, ok ? "PASS" : "FAIL" };
        }
        private static string[] Row(string name, string val)
        {
            return new[] { name, val, "PASS" };        // 陈述事实的行，不参与判定
        }

        public static int Run(Cli o)
        {
            var rows = new List<string[]>();
            var notes = new List<string>();
            var json = new StringBuilder();   // ⚠ 不能预置 "{" 又让各项自带前导逗号 —— 那会写出 {, "a":1} 这种非法 JSON

            // ---------------------------------------------------------- ① 几何
            string modelPath = Cli.ResolveModel(o.ModelPath);
            if (modelPath == null) { Console.Error.WriteLine("找不到模型"); return 1; }
            var m = Glb.Load(modelPath);
            rows.Add(Row("model.vertices", m.Vertices.ToString(), m.Vertices >= 50000));
            rows.Add(Row("model.triangles", m.Triangles.ToString(), m.Triangles == 120000));
            rows.Add(Row("model.bbox.w", m.Width.ToString("0.000"), Math.Abs(m.Width - 0.821) < 0.02));
            rows.Add(Row("model.bbox.h", m.Height.ToString("0.000"), Math.Abs(m.Height - 1.139) < 0.02));
            rows.Add(Row("model.bbox.d", m.Depth.ToString("0.000"), Math.Abs(m.Depth - 0.651) < 0.02));
            rows.Add(Row("model.grounded", "minY=" + m.Min.Y.ToString("0.####"), Math.Abs(m.Min.Y) < 1e-4));

            // 形变不许削顶：纵向拉伸从**脚底**往上长，取景顶部余量 = (MarginY-1)/2 · 身高。
            // ⚠ 这条必须能失败 —— 只要有人调大 JumpStretch / JumpLift 而不重算留白，这里就变红。
            {
                double headroom = (WpfPetRenderer.MarginY - 1) / 2 * m.Height;
                var probePose = new PoseEngine { IdleOn = false };
                probePose.TriggerJump();
                double top = 0, apexLift = 0, minSy = double.MaxValue;
                for (int i = 0; i < 420; i++)   // 0.44 s 起落 + 0.48 s，把落地挤压那 0.30 s 也扫进来
                {
                    var p = probePose.Step(probePose.JumpT / 200.0);
                    double t = m.Height * p.ScaleY + p.Lift;
                    if (t > top) { top = t; apexLift = p.Lift; }
                    if (p.ScaleY < minSy) minSy = p.ScaleY;
                }
                // 侧倾（Roll = swayAmp·sin）会让**头顶**再抬高 TopHalfWidth·sin(Roll)，必须算进去：
                // 忽略它就把余量当成了白得的。⚠ 用真实头顶半宽，不是包围盒半宽（后者按最宽处算，会高估几倍）。
                double tilt = m.TopHalfWidth * Math.Sin(probePose.SwayAmp);
                double limit = m.Height + headroom, margin = limit - (top + tilt);
                rows.Add(Row("pose.jumpTopInFrame",
                      "top=" + top.ToString("0.000") + "+tilt" + tilt.ToString("0.000")
                      + " limit=" + limit.ToString("0.000") + " margin=" + margin.ToString("0.000") + "m",
                      margin > 0));
                // 点击跳跃必须**首尾都有形变**：起跳抻长（Sy>1）之后，落地要压一下（Sy<1）。
                // ⚠ 阈值取 0.98 是**判「有没有那一下」而不是判强弱**：
                //   删掉 TriggerBounce 调用 ⇒ 全程 Sy 恒 1.000 → FAIL；
                //   只要调用在，TriggerBounce 内部会把强度夹到 ≥0.25 ⇒ 至少压 5.2% ⇒ minSy ≤ 0.948 → PASS。
                //   （第一版取 0.95，结果「把强度设成 0」被夹到 0.25 后仍 PASS —— 假通过，已改。）
                rows.Add(Row("pose.jumpLandSquash",
                      "minScaleY=" + minSy.ToString("0.###") + " (" + ((minSy - 1) * 100).ToString("0.#") + "%)"
                      + " land=" + (probePose.JumpLandBounce * 100).ToString("0.#") + "%",
                      minSy < 0.98));
                rows.Add(Row("pose.jumpApex",
                      "scaleY=" + (top - apexLift).ToString("0.000") + "m lift=" + apexLift.ToString("0.000")
                      + " stretch=+" + (probePose.JumpStretch * 100).ToString("0.#") + "%"
                      + " squash=-" + (probePose.LandSquash * 100).ToString("0.#") + "%"
                      + " period=" + probePose.JumpT.ToString("0.00") + "s"
                      + " headW=" + m.TopHalfWidth.ToString("0.000") + "m"));
            }
            rows.Add(Row("model.basecolor", m.BaseColorName + " " + m.BaseColorW + "x" + m.BaseColorH,
                  m.BaseColor != null && m.BaseColorName.Contains("whale") && m.BaseColorW == 2048));
            foreach (string n in m.Notes) notes.Add(n);

            // ---------------------------------------------------------- 建窗
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var cfg = new PetConfig { SizeIndex = o.SizeIndex, NightDim = false, ShowTray = false };
            var r = new WpfPetRenderer(m);
            var w = new PetWindow(r, cfg) { SelfTestMode = true, ForcedIdle = o.ForcedIdle };
            w.Pose.DozeAfter = o.DozeAfter;
            w.Pose.SleepAfter = o.SleepAfter;
            w.Show();

            Window fs = null;
            bool fsFg = false;
            double tMeasureEnd = -1, tHidden = -1, tRestored = -1, tTotal = o.Seconds;
            double tMeasureStart = -1;
            int framesAtMeasure = 0;
            int fsTries = 0;
            string waTrace = "";
            var clock = Stopwatch.StartNew();
            int step = 0;
            var probes = new List<object[]>();

            var tl = new DispatcherTimer(DispatcherPriority.Normal);
            tl.Interval = TimeSpan.FromMilliseconds(50);
            tl.Tick += (s, e) =>
            {
                double t = clock.Elapsed.TotalSeconds;
                try
                {
                    switch (step)
                    {
                        case 0:
                            if (o.At != null && o.At.Length == 2) { w.Left = o.At[0] / w.DipScale; w.Top = o.At[1] / w.DipScale; }
                            else w.GoHome();
                            step = 1;
                            break;

                        case 1:
                            if (t > 1.2)
                            {
                                w.Pose.ResetExtremes();
                                framesAtMeasure = w.RenderedFrames;
                                tMeasureStart = t;
                                waTrace = "wa=" + WorkAreaOf(w);
                                step = 2;
                            }
                            break;

                        case 2:
                            if (t > 1.2 + Math.Min(3.0, tTotal * 0.45)) { tMeasureEnd = t; step = 3; }
                            break;

                        case 3:
                            fs = MakeFullscreen(w);
                            TryActivate(fs, w);
                            fsTries = 1;
                            step = 4;
                            break;

                        case 4:
                            if (!fsFg && Native.ForegroundIsFullscreen(w.Handle)) fsFg = true;
                            // ⚠ 别只激活一次。Windows 的前台锁定会让第一次 Activate 静默失败
                            //   （实测：单次尝试 → foreground_seen=no，产品代码的隐退路径压根没被走到，
                            //   报出来是「未隐退」，看着像功能坏了，其实是夹具没到位）。这里反复试。
                            if (!fsFg && fs != null && ++fsTries % 6 == 0) TryActivate(fs, w);
                            if (w.HideK > 0.99) { tHidden = t; step = 5; }
                            else if (t > tMeasureEnd + 5.0) { step = 5; }
                            break;

                        case 5:
                            rows.Add(Row("fullscreen.foreground_seen", fsFg ? "yes" : "no", fsFg));
                            if (fs != null) { fs.Close(); fs = null; }
                            step = 6;
                            break;

                        case 6:
                            if (w.HideK < 0.02) { tRestored = t; step = 7; }
                            else if (t > tMeasureEnd + 8.0) { step = 7; }
                            break;

                        case 7:
                            tl.Stop();
                            RunHitProbes(r, w, probes);
                            ShotAndAnalyse(o, w, json);
                            Finish(app, rows, notes, json, m, r, w, o, tHidden, tRestored, tMeasureEnd, tMeasureStart, framesAtMeasure, waTrace, probes);
                            step = 8;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    rows.Add(Row("selftest.exception", ex.Message, false));
                    notes.Add(ex.ToString());
                    tl.Stop();
                    try { if (fs != null) fs.Close(); } catch { }
                    app.Shutdown();
                    step = 8;
                }
            };
            tl.Start();

            app.Run();

            var sb = new StringBuilder();
            int fails = 0;
            foreach (string[] row in rows)
            {
                if (row[2] == "FAIL") fails++;
                sb.AppendLine((row[2] == "PASS" ? "[PASS] " : "[FAIL] ") + row[0] + " = " + row[1]);
            }
            sb.AppendLine("TOTAL pass=" + (rows.Count - fails) + " fail=" + fails);
            Console.Out.Write(sb.ToString());
            Console.Out.Flush();

            if (o.OutFile != null)
            {
                try
                {
                    var full = new StringBuilder();
                    full.Append("{\n");
                    full.Append("  \"checks\": [\n");
                    for (int i = 0; i < rows.Count; i++)
                    {
                        full.Append("    {\"name\": ").Append(Js(rows[i][0])).Append(", \"value\": ").Append(Js(rows[i][1]))
                            .Append(", \"result\": ").Append(Js(rows[i][2])).Append("}").Append(i == rows.Count - 1 ? "\n" : ",\n");
                    }
                    full.Append("  ],\n  \"notes\": [");
                    for (int i = 0; i < notes.Count; i++) full.Append(i == 0 ? "" : ", ").Append(Js(notes[i]));
                    full.Append("],\n  \"detail\": {").Append(json).Append("}}\n");
                    File.WriteAllText(o.OutFile, full.ToString(), new UTF8Encoding(false));
                }
                catch { }
            }
            return fails == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------ 命中采样
        private static void RunHitProbes(WpfPetRenderer r, PetWindow w, List<object[]> probes)
        {
            double W = w.ActualWidth, H = w.ActualHeight;
            var pts = new object[] {
                new object[] { "head",   W * 0.50, H * 0.35, true  },
                new object[] { "torso",  W * 0.50, H * 0.55, true  },
                new object[] { "corner_tl",  8.0,       8.0, false },
                new object[] { "above_head", W * 0.50, H * 0.06, false },
                new object[] { "below_feet", W * 0.50, H * 0.97, false },
                new object[] { "corner_br", W - 8, H - 8, false },
            };
            foreach (object[] p in pts)
            {
                bool hit = r.HitTest(new Point(Convert.ToDouble(p[1]), Convert.ToDouble(p[2])));
                probes.Add(new object[] { p[0], hit, p[3], r.LastHitType, Math.Round(r.LastHitMs, 3) });
            }
            // 冷查询（每次挪 4~8 px，绕过 1.5 px 的结果缓存）vs 热查询（同一格重复问）
            var swp = Stopwatch.StartNew();
            for (int i = 0; i < 50; i++) r.HitTest(new Point(W * 0.5 + (i % 5) * 4, H * 0.55 + (i % 7) * 3));
            swp.Stop();
            probes.Add(new object[] { "hit_ms_cold50", Math.Round(swp.Elapsed.TotalMilliseconds / 50.0, 3), null, "-", null });

            var swp2 = Stopwatch.StartNew();
            for (int i = 0; i < 50; i++) r.HitTest(new Point(W * 0.5, H * 0.55));
            swp2.Stop();
            probes.Add(new object[] { "hit_ms_cached50", Math.Round(swp2.Elapsed.TotalMilliseconds / 50.0, 3), null, "-", null });
        }

        // ------------------------------------------------------------------ 截图
        // ⚠⚠ 像素真值不在这里。曾经这一段是「绿幕 + 单张截图」，然后判「已绘制像素里 17% 是纯黑」⇒
        //   被读成「贴图碎了 / 渲染过暗」，一路排查到 PNG 解码。真相是：那个窗口上绿幕只占了 18 px，
        //   「已绘制像素」里 99.99% 是桌面壁纸 —— 量的是壁纸，不是模型。
        //   现在像素只由 `--pix` 出（分层连拍 + 做差 + 深色背板 + 不变量自检）。
        //   两套判据必然漂移，所以这里只留一张给人看的截图，不再产出任何数字。
        private static void ShotAndAnalyse(Cli o, PetWindow w, StringBuilder json)
        {
            int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            using (var bmp = new SD.Bitmap(vw, vh))
            {
                using (SD.Graphics g = SD.Graphics.FromImage(bmp))
                    g.CopyFromScreen(vx, vy, 0, 0, new SD.Size(vw, vh));
                Native.RECT wr;
                Native.GetWindowRect(w.Handle, out wr);
                var rect = new SD.Rectangle(wr.Left - vx, wr.Top - vy, wr.Right - wr.Left, wr.Bottom - wr.Top);
                if (rect.X < 0) { rect.Width += rect.X; rect.X = 0; }
                if (rect.Y < 0) { rect.Height += rect.Y; rect.Y = 0; }
                if (rect.Right > bmp.Width) rect.Width = bmp.Width - rect.X;
                if (rect.Bottom > bmp.Height) rect.Height = bmp.Height - rect.Y;
                if (o.ShotCrop != null && rect.Width > 0 && rect.Height > 0)
                    using (SD.Bitmap c = bmp.Clone(rect, bmp.PixelFormat)) c.Save(o.ShotCrop, SDI.ImageFormat.Png);
                if (o.ShotFile != null) bmp.Save(o.ShotFile, SDI.ImageFormat.Png);
            }
            json.Append("\"window_rect_px\": \"").Append(Rect(w)).Append("\"");   // 首项，不带前导逗号
        }

        private static string Rect(PetWindow w)
        {
            Native.RECT r;
            Native.GetWindowRect(w.Handle, out r);
            return r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top);
        }

        // ------------------------------------------------------------------ 收尾
        private static void Finish(Application app, List<string[]> rows, List<string> notes, StringBuilder json,
            GlbModel m, WpfPetRenderer r, PetWindow w, Cli o, double tHidden, double tRestored, double tMeasureEnd,
            double tMeasureStart, int framesAtMeasure, string waTrace, List<object[]> probes)
        {
            double pxPerMeter = r.PxPerMeter * w.DipScale;
            double liftPx = (w.Pose.LiftMax - w.Pose.LiftMin) * pxPerMeter;
            double bodyPx = m.Height * pxPerMeter;
            double avgMs = w.SumFrameMs / Math.Max(1, w.RenderedFrames);
            double span = Math.Max(0.001, tMeasureEnd - tMeasureStart);
            double fps = (w.RenderedFrames - framesAtMeasure) / span;

            rows.Add(Row("window.workarea", waTrace + " dpi=" + w.DipScale.ToString("0.###")));
            rows.Add(Row("render.fps_in_window", fps.ToString("0.0") + " fps（" + (w.RenderedFrames - framesAtMeasure) + " 帧 / " + span.ToString("0.00") + " s）", fps > 22));

            rows.Add(Row("pose.frames", w.Pose.Frames.ToString(), w.Pose.Frames > 30));
            rows.Add(Row("pose.lift_px", liftPx.ToString("0.00") + " px（" + (w.Pose.LiftMax - w.Pose.LiftMin).ToString("0.0000") + " m 换算）", liftPx > 6));
            rows.Add(Row("pose.roll_deg", ((w.Pose.RollMax - w.Pose.RollMin) * 180 / Math.PI).ToString("0.00"), (w.Pose.RollMax - w.Pose.RollMin) > 0.02));
            rows.Add(Row("pose.yaw_deg", ((w.Pose.YawMax - w.Pose.YawMin) * 180 / Math.PI).ToString("0.00")));
            rows.Add(Row("pose.dozeK", w.Pose.DozeK.ToString("0.000") + " (" + w.Pose.State + ")"));
            rows.Add(Row("pose.pitch_deg_max", (w.Pose.PitchMax * 180 / Math.PI).ToString("0.000")));
            rows.Add(Row("window.px_per_meter", pxPerMeter.ToString("0.0")));
            rows.Add(Row("window.body_px_expected", bodyPx.ToString("0.0")));
            rows.Add(Row("window.feet_dip", r.FeetYDip.ToString("0.0") + " / " + w.ActualHeight.ToString("0.0"), r.FeetYDip > 0 && r.FeetYDip < w.ActualHeight));
            rows.Add(Row("window.dpi_scale", w.DipScale.ToString("0.###")));
            rows.Add(Row("render.frames", w.RenderedFrames.ToString(), w.RenderedFrames > 10));
            rows.Add(Row("render.avg_ms", avgMs.ToString("0.00"), avgMs < 33));
            rows.Add(Row("render.max_ms", w.MaxFrameMs.ToString("0.00")));
            rows.Add(Row("render.wpf_tier", (RenderCapability.Tier >> 16).ToString()));
            rows.Add(Row("render.process_render_mode", RenderOptions.ProcessRenderMode.ToString()));
            rows.Add(Row("nchittest.checks", w.HitThroughChecks + " (pass " + w.HitThroughPass + ")"));
            rows.Add(Row("fullscreen.hidden_at", tHidden > 0 ? (tHidden - tMeasureEnd).ToString("0.00") + " s" : "未隐退", tHidden > 0));
            rows.Add(Row("fullscreen.restored_at", tRestored > 0 ? (tRestored - tMeasureEnd).ToString("0.00") + " s" : "未恢复", tRestored > 0));

            foreach (object[] p in probes)
            {
                string name = "hit." + p[0];
                if (p[1] is bool)
                {
                    bool hit = (bool)p[1];
                    bool want = (bool)p[2];
                    rows.Add(Row(name, "hit=" + hit + " type=" + p[3] + " " + p[4] + "ms", hit == want));
                }
                else rows.Add(Row(name, p[1] + " ms", (double)p[1] < 8.0));
            }

            json.Append(", \"stats\": ").Append(Js(r.Stats));
            json.Append(", \"pose\": {");
            json.Append("\"liftMin\": ").Append(N(w.Pose.LiftMin, "0.######")).Append(", \"liftMax\": ").Append(N(w.Pose.LiftMax, "0.######"));
            json.Append(", \"rollMin\": ").Append(N(w.Pose.RollMin, "0.######")).Append(", \"rollMax\": ").Append(N(w.Pose.RollMax, "0.######"));
            json.Append(", \"yawMin\": ").Append(N(w.Pose.YawMin, "0.######")).Append(", \"yawMax\": ").Append(N(w.Pose.YawMax, "0.######"));
            json.Append(", \"syMin\": ").Append(N(w.Pose.SyMin, "0.######")).Append(", \"syMax\": ").Append(N(w.Pose.SyMax, "0.######"));
            json.Append(", \"dozeK\": ").Append(N(w.Pose.DozeK, "0.######")).Append(", \"state\": ").Append(Js(w.Pose.State.ToString()));
            json.Append(", \"lift_px\": ").Append(N(liftPx, "0.###")).Append(", \"body_px\": ").Append(N(bodyPx, "0.###"));
            json.Append("}");
            json.Append(", \"fps_avg\": ").Append(N(fps, "0.##"));
            json.Append(", \"timeline\": {\"measure_start\": ").Append(N(tMeasureStart, "0.##"))
                .Append(", \"measure_end\": ").Append(N(tMeasureEnd, "0.##"))
                .Append(", \"hidden\": ").Append(N(tHidden, "0.##"))
                .Append(", \"restored\": ").Append(N(tRestored, "0.##")).Append("}");
            json.Append(", \"hit_probes\": [");
            for (int i = 0; i < probes.Count; i++)
            {
                object[] p = probes[i];
                json.Append(i == 0 ? "" : ", ");
                json.Append("{\"name\": ").Append(Js((string)p[0]))
                    .Append(", \"result\": ").Append(p[1] is bool ? ((bool)p[1] ? "true" : "false") : "null")
                    .Append(", \"want\": ").Append(p[2] is bool ? ((bool)p[2] ? "true" : "false") : "null")
                    .Append(", \"type\": ").Append(Js((string)p[3]))
                    .Append(", \"ms\": ").Append(p[4] == null ? "null" : N(Convert.ToDouble(p[4]), "0.###"))
                    .Append("}");
            }
            json.Append("]");
            json.Append(", \"rate\": [");
            for (int i = 0; i < w.RateLog.Count; i++) json.Append(i == 0 ? "" : ", ").Append(Js(w.RateLog[i]));
            json.Append("]");
            json.Append(", \"gate\": {\"calls\": ").Append(w.GateCalls).Append(", \"pass\": ").Append(w.GatePass).Append("}");
            json.Append(", \"trace\": [");
            for (int i = 0; i < w.TraceN; i++) json.Append(i == 0 ? "" : ", ").Append(Js(w.TraceLog[i]));
            json.Append("]");

            app.Shutdown();
        }

        private static string N(double v, string fmt)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return v.ToString(fmt, CultureInfo.InvariantCulture);
        }

        private static string Js(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        private static string WorkAreaOf(PetWindow w)
        {
            Native.RECT wa;
            if (!Native.WindowWorkArea(w.Handle, out wa)) return "(取不到)";
            return wa.Left + "," + wa.Top + " → " + wa.Right + "," + wa.Bottom;
        }

        /// <summary>把全屏测试窗推到前台。Activate() 受 Windows 前台锁定，常静默失败；
        /// 补一次 SetForegroundWindow ＋「在桌宠窗外的空白处点一下」——点在普通窗口上是最可靠的夺前台路径。</summary>
        private static void TryActivate(Window fs, PetWindow w)
        {
            try
            {
                IntPtr h = new System.Windows.Interop.WindowInteropHelper(fs).Handle;
                fs.Activate();
                Native.SetForegroundWindow(h);
                Native.RECT pr;
                Native.GetWindowRect(w.Handle, out pr);
                int tx = pr.Left > 60 ? pr.Left - 40 : pr.Right + 40;
                Native.SetCursorPos(tx, pr.Top + 20);
                System.Threading.Thread.Sleep(60);
                Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            }
            catch { }
        }

        private static Window MakeFullscreen(PetWindow w)
        {
            IntPtr mon = Native.MonitorFromWindow(w.Handle, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFO();
            mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFO));
            Native.GetMonitorInfo(mon, ref mi);
            var f = new Window();
            f.WindowStyle = WindowStyle.None;
            f.ResizeMode = ResizeMode.NoResize;
            f.ShowInTaskbar = false;
            f.Topmost = false;
            f.Background = new SolidColorBrush(Color.FromRgb(24, 24, 28));
            f.Left = mi.rcMonitor.Left / w.DipScale;
            f.Top = mi.rcMonitor.Top / w.DipScale;
            f.Width = (mi.rcMonitor.Right - mi.rcMonitor.Left) / w.DipScale;
            f.Height = (mi.rcMonitor.Bottom - mi.rcMonitor.Top) / w.DipScale;
            f.Show();
            return f;
        }
    }
}
