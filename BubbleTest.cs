// --bubbletest：气泡「浮在宠物之外、不遮模型」的像素级验收。
//
// 为什么必须用像素（而不是读窗口矩形）：
//   桌宠窗口**绝大部分是透明的** —— 气泡盖住窗口 ≠ 盖住模型。
//   所以这里用做差法把「宠物真正占的像素」量出来（宠物可见 − 宠物隐藏），再与气泡矩形求交。
//   口径来自 window-render-forensics：做差、不靠单张判定；集合由几何存在性决定，不由颜色决定。
//
// ⚠⚠ 两条让差法活下来的前提（都是实测踩出来的，不是理论推的）：
//   ① **窗口内垫近黑背板**（r.Backdrop）—— 桌宠窗口是透明的，不垫的话窗口内露出的是用户的桌面。
//      实测第一次跑时屏幕上正在放 B 站视频，a0/a1 之差被视频画面顶到 105 万像素，判据当场失效。
//   ② **所有「宠物像素」只在宠物窗口矩形内统计** —— 窗口外是用户的桌面，不归我们管，也不能拿它当读数。
//   另：夜间调光会持续改窗口 Opacity ⇒ 全窗口像素都在动，测试里必须关掉。
//
// 判据（全是算术，每条都能红）：
//   ① invariant.static_repeat_diff == 0      同一状态连拍两次差恒为 0（差法的前提；不为 0 则整轮不可信）
//   ② bubble.visible == 1 且 bubble.size_px 合理
//                                            气泡确实处于可见状态、且尺寸正常
//                                            ⚠ 这是**运行时状态**判据，不是像素判据。理由（三条都实测过）写在 Judge() 里：
//                                              气泡矩形落在用户桌面上，本次实测那儿是 B 站视频，按亮度分布判断
//                                              「那块是不是气泡」要么会被动态内容骗过，要么根本没资格变红。
//                                              像素判据只用来回答**位置**问题（③④），存在性另有落盘图供人眼确认。
//   ③ bubble.overlap_pet_px == 0             「不遮模型」本身
//   ④ bubble.outside_pet_window == 0         气泡整体落在宠物窗口之外（本设计的目标形态）
//   ⑤ env.control_diff_px 只记录、不判定 —— 屏幕一角是否在动取决于用户桌面，不足以判定本轮读数无效
//      （真正保读数的：统计范围限制在窗口内 ＋ 不变量为 0）
//   ⑥ 负对照 --force-bubble-on-pet：把气泡强行压到宠物正中 ⇒ ③ 的同一判据**必须** > 500
//      —— 一个从没红过的判据，它的绿是没有信息量的（本项目判据纪律 7 的元级应用）。
using System;
using System.Collections.Generic;
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
    internal static class BubbleTest
    {
        private const int DiffThr = 8;      // 通道最大差 > 8 才算「这里有像素变化」（屏摄噪声余量）

        public static int Run(Cli o)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            string model = Cli.ResolveModel(o.ModelPath);
            if (model == null) { Console.WriteLine("[bubble] 找不到模型（用 --model 指定）"); return 2; }
            GlbModel gm = Glb.Load(model);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var cfg = PetConfig.Load();
            cfg.SizeIndex = o.SizeIndex;
            var r = new WpfPetRenderer(gm);
            // ⚠ 背板必须做在窗口**内部**（r.Backdrop 就是 _root.Background）——
            //   在桌宠另开一个窗口垫背景，实测盖不满，会漏出桌面。
            r.Backdrop = new SolidColorBrush(Color.FromRgb(16, 17, 20));
            var w = new PetWindow(r, cfg) { ShowInTaskbar = false };
            w.ForcedIdle = 0;
            w.SelfTestMode = true;
            PetWindow.ForceBubbleOnPet = o.ForceBubbleOnPet;
            w.Show();

            var rows = new List<string[]>();
            int fail = 0;
            Action<string, string, bool> Chk = (n, v, ok) =>
            {
                rows.Add(new[] { n, v, ok ? "PASS" : "FAIL" });
                if (!ok) fail++;
            };
            Action<string, string> Info = (n, v) => rows.Add(new[] { n, v, "INFO" });

            Shot a0 = null, a0b = null, a1 = null, a2 = null;
            int[] bubble = new int[4];
            int petPx = 0, overlap = 0, invMax = -1, envPx = -1, winOverlap = -1;
            double darkFrac = -1, chgFrac = -1;

            var tl = new DispatcherTimer(DispatcherPriority.Send);
            tl.Interval = TimeSpan.FromMilliseconds(40);
            int phase = 0;
            double due = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            tl.Tick += (s, e) =>
            {
                try
                {
                    double t = clock.Elapsed.TotalMilliseconds;
                    if (t < due) return;
                    switch (phase)
                    {
                        case 0:   // 就位：水平居中、上方留出放气泡的余地（否则「优先放上方」这条路径测不到）
                            if (o.At != null && o.At.Length == 2)
                            { w.Left = o.At[0] / w.DipScale; w.Top = o.At[1] / w.DipScale; }
                            else
                            {
                                Native.RECT wa0;
                                if (!Native.WindowWorkArea(w.Handle, out wa0))
                                { wa0 = new Native.RECT(); wa0.Right = 1920; wa0.Bottom = 1080; }
                                double wpx = w.ActualWidth * w.DipScale;
                                w.Left = ((wa0.Left + wa0.Right) / 2.0 - wpx / 2) / w.DipScale;
                                w.Top = (wa0.Top + 220 * w.DipScale) / w.DipScale;
                            }
                            w.Pose.Freeze = true;        // ⚠ 做差法的前提：几何必须逐帧一致
                            w.Cfg.NightDim = false;      // ⚠ 夜间调光持续改 Opacity ⇒ 全窗口像素都在动，差法失效
                            phase = 1; due = t + 900;    // 等它稳定渲染
                            break;

                        case 1: a0 = Cap(); phase = 2; due = t + 260; break;      // 宠物可见、气泡未显示
                        case 2: a0b = Cap(); phase = 3; due = t + 260; break;     // 不变量：同状态连拍第二张

                        case 3: w.Visibility = Visibility.Hidden; phase = 4; due = t + 320; break;
                        case 4: a1 = Cap(); phase = 5; due = t + 220; break;      // 只有背板 ⇒ 与 a0 之差 = 宠物像素
                        case 5: w.Visibility = Visibility.Visible; phase = 6; due = t + 420; break;

                        case 6: w.ShowBubble(); phase = 7; due = t + 3000; break; // 探测要联网，给足时间
                        case 7:
                            a2 = Cap();
                            bubble = w.BubbleRectPx();
                            phase = 8;
                            break;

                        case 8:
                            Judge();
                            tl.Stop();
                            app.Shutdown();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    rows.Add(new[] { "harness", ex.Message, "FAIL" });
                    fail++;
                    // 测试自己崩了必须喊出来 —— 否则「没打印表格」会被误读成「跑完了但没结论」
                    Console.WriteLine("[bubble] 测试自身异常：" + ex);
                    tl.Stop();
                    app.Shutdown();
                }
            };
            tl.Start();

            void Judge()
            {
                int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
                int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);

                bool visible = w.BubbleVisible;
                Chk("bubble.visible", visible ? "1" : "0", visible);

                // 气泡矩形 → **虚拟屏**坐标（截图的原点就是虚拟屏左上角）
                int bx = bubble[0] - vx, by = bubble[1] - vy, bw = bubble[2], bh = bubble[3];
                Info("bubble.rect_px", "[" + bx + "," + by + " " + bw + "x" + bh + "]");

                // 宠物窗口矩形（虚拟屏坐标）——「宠物像素」只在它内部统计（见文件头 ⚠⚠ ②）
                Native.RECT wr;
                Native.GetWindowRect(w.Handle, out wr);
                int wx0 = Math.Max(0, wr.Left - vx), wy0 = Math.Max(0, wr.Top - vy);
                int wx1 = Math.Min(a0.W, wr.Right - vx), wy1 = Math.Min(a0.H, wr.Bottom - vy);
                Info("pet.window_rect_px", "[" + wx0 + "," + wy0 + " - " + wx1 + "," + wy1 + "]");

                // ① 差法的前提：几何冻结是否真的成立
                invMax = MaxDiffIn(a0, a0b, wx0, wy0, wx1, wy1);
                Chk("invariant.static_repeat_diff", invMax.ToString(CultureInfo.InvariantCulture), invMax == 0);

                // ② 旁证统计（**只记录、不判定**，理由见下面那几行）。这里算两个与位置无关的量：
                //      a) 暗色块占比：气泡底色是 alpha=236/255 的深色 (22,24,34)，合成后 luma 落在 [18,46]；
                //      b) 变化占比：a0 → a2 之间有多少像素真的动了。
                int nAll = 0, nDark = 0, nChg = 0;
                int rx0 = Math.Max(0, bx), ry0 = Math.Max(0, by);
                int rx1 = Math.Min(a0.W, bx + bw), ry1 = Math.Min(a0.H, by + bh);
                for (int y = ry0; y < ry1; y++)
                    for (int x = rx0; x < rx1; x++)
                    {
                        nAll++;
                        double lu = Luma(a2, x, y);
                        if (lu >= 18 && lu <= 46) nDark++;
                        if (MaxCh(a0, a2, x, y) > DiffThr) nChg++;
                    }
                darkFrac = nAll > 0 ? nDark / (double)nAll : -1;
                chgFrac = nAll > 0 ? nChg / (double)nAll : -1;
                // ⚠ 这两条**只作旁证记录、不作判据**：气泡矩形落在用户桌面上，本次实测那儿是 B 站视频，
                //   任何「按亮度分布判断那块是不是气泡」的口径都会被动态内容骗过 / 或根本没资格变红：
                //     · 「矩形内变暗」→ 气泡底下是视频黑边，深色气泡反而略变亮（实测 -1.4）
                //     · 「暗色块占比」→ 黑边本来就落在同一亮度区间，气泡显示与否都 ≈ 0.9（混进两类对象）
                //     · 「白字像素增量」→ 视频自有亮像素 3100 个，气泡只多 208 个（信噪比不够）
                //   存在性因此交给运行时状态（下一行）＋落盘图人眼确认；像素判据只回答**位置**（③④）。
                Info("bubble.dark_frac(旁证)", darkFrac.ToString("0.000", CultureInfo.InvariantCulture));
                Info("bubble.changed_frac(旁证)", chgFrac.ToString("0.000", CultureInfo.InvariantCulture));
                Chk("bubble.size_px", bw + "x" + bh, bw > 200 && bh > 40);

                // ③ 宠物像素集合 + 与气泡矩形求交
                petPx = 0; overlap = 0;
                for (int y = wy0; y < wy1; y++)
                    for (int x = wx0; x < wx1; x++)
                        if (MaxCh(a0, a1, x, y) > DiffThr)
                        {
                            petPx++;
                            if (x >= bx && x < bx + bw && y >= by && y < by + bh) overlap++;
                        }
                Chk("pet.visible_px", petPx.ToString(CultureInfo.InvariantCulture), petPx > 500);

                // ④ 气泡与宠物窗口的几何交面积
                int ix = Math.Max(0, Math.Min(bx + bw, wx1) - Math.Max(bx, wx0));
                int iy = Math.Max(0, Math.Min(by + bh, wy1) - Math.Max(by, wy0));
                winOverlap = ix * iy;
                Info("bubble.petwindow_overlap_px", winOverlap.ToString(CultureInfo.InvariantCulture));

                if (o.ForceBubbleOnPet)
                    Chk("negative_control.overlap_pet_px",
                        overlap.ToString(CultureInfo.InvariantCulture), overlap > 500);
                else
                {
                    Chk("bubble.overlap_pet_px", overlap.ToString(CultureInfo.InvariantCulture), overlap == 0);
                    Chk("bubble.outside_pet_window", winOverlap.ToString(CultureInfo.InvariantCulture), winOverlap == 0);
                }

                // ⑤ 环境对照区（屏幕左上，远离宠物与气泡）—— **只记录，不判定**。
                //    它红过一次（264 px）：因为屏幕那一角恰好压着别的窗口在动。这条只反映「那儿有东西」，
                //    不反映「本轮读数无效」（换台机器、换个桌面布局，这个位置就是另一回事了）。
                //    真正保证读数有效的是：统计范围被限制在宠物窗口内（并垫了近黑背板）
                //    ＋ 不变量 static_repeat_diff == 0。留着当旁证，不当判据。
                envPx = 0;
                for (int y = 4; y < Math.Min(204, a0.H); y++)
                    for (int x = 4; x < Math.Min(204, a0.W); x++)
                        if (MaxCh(a0, a1, x, y) > DiffThr) envPx++;
                Info("env.control_diff_px(旁证)", envPx.ToString(CultureInfo.InvariantCulture));

                SaveShot(a0, Side(o.OutFile, "_a0"));
                SaveShot(a1, Side(o.OutFile, "_a1"));
                SaveShot(a2, Side(o.OutFile, "_a2"));

                Console.WriteLine("---- 气泡摆放验收（--bubbletest"
                    + (o.ForceBubbleOnPet ? " --force-bubble-on-pet" : "") + "）----");
                Console.WriteLine("气泡矩形 = [" + bx + "," + by + " " + bw + "x" + bh + "]"
                    + "   宠物窗口 = [" + wx0 + "," + wy0 + " - " + wx1 + "," + wy1 + "]");
                Console.WriteLine("宠物像素 = " + petPx + "   气泡区暗色块占比 = "
                    + darkFrac.ToString("0.000", CultureInfo.InvariantCulture)
                    + "   变化占比 = " + chgFrac.ToString("0.000", CultureInfo.InvariantCulture)
                    + "   与宠物重叠 = " + overlap + "   与窗口交面积 = " + winOverlap
                    + "   环境对照 = " + envPx);
                Console.WriteLine();
                foreach (string[] row in rows)
                    Console.WriteLine("  " + row[0].PadRight(38) + " " + row[1].PadLeft(10) + "  " + row[2]);
                Console.WriteLine();
                Console.WriteLine(fail == 0 ? "[bubble] 全部通过" : ("[bubble] 有 " + fail + " 项 FAIL"));

                if (!string.IsNullOrEmpty(o.OutFile))
                {
                    var sb = new StringBuilder();
                    sb.Append("{\n");
                    sb.Append("  \"forced\": ").Append(o.ForceBubbleOnPet ? "true" : "false").Append(",\n");
                    sb.Append("  \"bubble\": {\"visible\": ").Append(visible ? "true" : "false")
                      .Append(", \"rect_px\": [").Append(bx).Append(",").Append(by).Append(",")
                      .Append(bw).Append(",").Append(bh).Append("]")
                      .Append(", \"dark_frac\": ").Append(darkFrac.ToString("0.000", CultureInfo.InvariantCulture))
                      .Append(", \"changed_frac\": ").Append(chgFrac.ToString("0.000", CultureInfo.InvariantCulture))
                      .Append("},\n");
                    sb.Append("  \"pet\": {\"window_rect_px\": [").Append(wx0).Append(",").Append(wy0)
                      .Append(",").Append(wx1).Append(",").Append(wy1).Append("]")
                      .Append(", \"visible_px\": ").Append(petPx).Append("},\n");
                    sb.Append("  \"overlap_pet_px\": ").Append(overlap).Append(",\n");
                    sb.Append("  \"bubble_petwindow_overlap_px\": ").Append(winOverlap).Append(",\n");
                    sb.Append("  \"invariant_static_repeat_maxdiff\": ").Append(invMax).Append(",\n");
                    sb.Append("  \"env_control_diff_px\": ").Append(envPx).Append(",\n");
                    sb.Append("  \"checks\": [\n");
                    for (int i = 0; i < rows.Count; i++)
                        sb.Append("    {\"name\": \"").Append(rows[i][0]).Append("\", \"value\": \"")
                          .Append(rows[i][1]).Append("\", \"verdict\": \"").Append(rows[i][2]).Append("\"}")
                          .Append(i + 1 < rows.Count ? "," : "").Append("\n");
                    sb.Append("  ],\n");
                    sb.Append("  \"pass\": ").Append(rows.Count - fail).Append(", \"fail\": ").Append(fail).Append("\n");
                    sb.Append("}\n");
                    File.WriteAllText(o.OutFile, sb.ToString(), new UTF8Encoding(false));
                }
            }

            app.Run();
            return fail > 0 ? 1 : 0;
        }

        // ------------------------------------------------------------------ 截屏 / 差值
        private sealed class Shot
        {
            public int W, H, Stride;
            public byte[] P;
            public Shot(SD.Bitmap b)
            {
                W = b.Width; H = b.Height;
                SDI.BitmapData d = b.LockBits(new SD.Rectangle(0, 0, W, H), SDI.ImageLockMode.ReadOnly, SDI.PixelFormat.Format32bppArgb);
                Stride = d.Stride;
                P = new byte[Stride * H];
                System.Runtime.InteropServices.Marshal.Copy(d.Scan0, P, 0, P.Length);
                b.UnlockBits(d);
            }
        }

        private static Shot Cap()
        {
            int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            using (var full = new SD.Bitmap(vw, vh, SDI.PixelFormat.Format32bppArgb))
            {
                using (SD.Graphics g = SD.Graphics.FromImage(full))
                    g.CopyFromScreen(vx, vy, 0, 0, new SD.Size(vw, vh));
                return new Shot(full);
            }
        }

        private static int MaxCh(Shot a, Shot b, int x, int y)
        {
            if (x < 0 || y < 0 || x >= a.W || y >= a.H || x >= b.W || y >= b.H) return 0;
            int ia = y * a.Stride + x * 4, ib = y * b.Stride + x * 4;
            int m = 0;
            for (int c = 0; c < 3; c++)
            {
                int d = Math.Abs(a.P[ia + c] - b.P[ib + c]);
                if (d > m) m = d;
            }
            return m;
        }

        private static double Luma(Shot s, int x, int y)
        {
            if (x < 0 || y < 0 || x >= s.W || y >= s.H) return 0;
            int i = y * s.Stride + x * 4;
            return 0.114 * s.P[i] + 0.587 * s.P[i + 1] + 0.299 * s.P[i + 2];   // BGRA
        }

        private static int MaxDiffIn(Shot a, Shot b, int x0, int y0, int x1, int y1)
        {
            int m = 0;
            for (int y = Math.Max(0, y0); y < Math.Min(Math.Min(a.H, b.H), y1); y++)
                for (int x = Math.Max(0, x0); x < Math.Min(Math.Min(a.W, b.W), x1); x++)
                {
                    int d = MaxCh(a, b, x, y);
                    if (d > m) m = d;
                }
            return m;
        }

        /// <summary>把内存里的帧存成 PNG —— 判据出问题时至少能亲眼看一眼差落在哪。</summary>
        private static void SaveShot(Shot s, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                using (var b = new SD.Bitmap(s.W, s.H, SDI.PixelFormat.Format32bppArgb))
                {
                    SDI.BitmapData d = b.LockBits(new SD.Rectangle(0, 0, s.W, s.H),
                        SDI.ImageLockMode.WriteOnly, SDI.PixelFormat.Format32bppArgb);
                    System.Runtime.InteropServices.Marshal.Copy(s.P, 0, d.Scan0,
                        Math.Min(s.P.Length, d.Stride * s.H));
                    b.UnlockBits(d);
                    b.Save(path, SDI.ImageFormat.Png);
                }
            }
            catch { }
        }

        /// <summary>在 &lt;out&gt;.json 旁边生成 &lt;out&gt;_a0.png 这类旁证图。</summary>
        private static string Side(string outFile, string tag)
        {
            if (string.IsNullOrEmpty(outFile)) return null;
            return Path.Combine(Path.GetDirectoryName(outFile),
                Path.GetFileNameWithoutExtension(outFile) + tag + ".png");
        }
    }
}
