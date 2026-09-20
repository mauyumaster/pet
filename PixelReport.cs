// 像素量测 —— 「看不到图时，把画面变成算术」的那台仪器。
//
// ⚠ 为什么不用「绿幕 + 单张截图」判定：
//   上一版就是这么做的，结果窗口在屏上占 360x450，而绿幕只贡献了 18 px ——
//   「已绘制像素」里 99.99% 其实是桌面壁纸，于是量出来「17% 纯黑」，
//   被读成「贴图碎了 / 渲染过暗」，一路排查到贴图解码。全是假的。
//   一次读数里混进两类对象，结论会整个反过来。
//
// 正解 = 做差：同一次运行、**冻结姿态**（否则呼吸会把两张图错开，差值里多一圈幽灵边缘），
// 用分层开关分别拍「全隐 / 只有接地影 / 全画」，两两相减得到两个互不重叠的集合：
//     模型 = |全画 − 只有影|        接地影 = |只有影 − 全隐|
// 于是在**同一群像素**上算均值、中位数、暗像素占比，才是一个可比较的数。
//
// 再进一步：一趟里把 4 种材质都扫一遍，就能算出**光照增益**：
//     white 模式（纯白漫反射）在轮廓上的均值 ÷ 255 = 该通道的平均光照增益
//     tex 模式 ≈ emissive 模式 × 该增益
// 增益不是 1，就说明角色被渲染成了「比贴图更暗/偏色」；把它除进灯色即一次校准到中性
//（WPF 照明对灯色是线性的，所以这一步是可解的，不用瞎调）。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

namespace AzhuPet
{
    internal static class PixelReport
    {
        private static readonly int[] Modes = { 0, 1, 2, 3 };
        private static readonly string[] ModeNames = { "tex", "white", "emissive", "uv" };
        private const int WaitMs = 260;          // 每个状态等它渲染几帧（30fps 下约 8 帧）

        public static int Run(Cli o)
        {
            string model = Cli.ResolveModel(o.ModelPath);
            if (model == null) { Console.WriteLine("[pix] 找不到 model/chibi_maid_pet.glb"); return 2; }
            if (string.IsNullOrEmpty(o.PixDir)) { Console.WriteLine("[pix] 需要 --pix <输出目录>"); return 2; }
            Directory.CreateDirectory(o.PixDir);

            GlbModel gm = Glb.Load(model);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            var cfg = PetConfig.Load();
            cfg.SizeIndex = o.SizeIndex;
            var r = new WpfPetRenderer(gm);
            if (o.Amb != null) r.AmbOverride = o.Amb;
            if (o.Key != null) r.KeyOverride = o.Key;
            var w = new PetWindow(r, cfg) { ShowInTaskbar = false };
            w.ForcedIdle = 0;                    // 别在量测中途打盹
            w.Show();

            var rows = new List<string[]>();
            var json = new StringBuilder("{");
            var shots = new Dictionary<string, SD.Bitmap>();
            var bufs = new Dictionary<string, Shot>();
            int rc = 0;

            var tl = new DispatcherTimer(DispatcherPriority.Send);
            tl.Interval = TimeSpan.FromMilliseconds(WaitMs);
            int step = 0, mi = 0, li = 0, retry = 0;
            double t0 = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double[] gain = null;

            var rects = new List<string>();
            Action<string> Keep = key =>
            {
                SD.Bitmap b = Capture(w);
                if (shots.ContainsKey(key)) shots[key].Dispose();
                shots[key] = b;
                bufs[key] = new Shot(b);
                rects.Add(Rect(w));
                Directory.CreateDirectory(Path.Combine(o.PixDir, "raw"));
                b.Save(Path.Combine(o.PixDir, "raw", key + ".png"), SDI.ImageFormat.Png);
            };

            // 每个模式要拍的三个层次
            var layers = new[] { "off", "shadow", "all" };

            tl.Tick += (s, e) =>
            {
                try
                {
                    if (step == 0)
                    {
                        // ---- 就位：显式坐标优先，否则回到右下角
                        if (o.At != null && o.At.Length == 2)
                        { w.Left = o.At[0] / w.DipScale; w.Top = o.At[1] / w.DipScale; }
                        else w.GoHome();
                        w.Pose.Freeze = true;         // ⚠ 做差法的前提：几何必须逐帧一致
                        w.Pose.IdleOn = false;
                        t0 = clock.Elapsed.TotalSeconds;
                        step = 1;
                        return;
                    }
                    if (step == 1)
                    {
                        if (clock.Elapsed.TotalSeconds - t0 < 0.7) return;   // 等窗口稳定落位
                        rows.Add(Row("place", "rect=" + Rect(w) + " dpi=" + w.DipScale.ToString("0.###", CultureInfo.InvariantCulture)));
                        // 深色背板：做进窗口内部（不是另开窗口垫在下面）。⚠⚠ 这一步不是装饰，
                        //   是掩码能不能成立的前提。本机壁纸均值 186，而白模渲染出来只有 205 ——
                        //   用「模型色 ≠ 背景色」当掩码时轮廓有一半漏掉（实测只圈到 18% 而不是 36%），
                        //   而暗色的接地影反而被圈进来，掩码整个变成了影子。
                        //   换成接近黑的背板后，白模与背景差 ~170、暗影与背景差只有 ~14，两者才分得开。
                        var bgColor = o.Backdrop != null && o.Backdrop.Length >= 3
                            ? System.Windows.Media.Color.FromRgb(C8(o.Backdrop[0]), C8(o.Backdrop[1]), C8(o.Backdrop[2]))
                            : System.Windows.Media.Color.FromRgb(28, 30, 36);
                        r.Backdrop = new System.Windows.Media.SolidColorBrush(bgColor);
                        step = 2;
                        return;
                    }

                    // ---- 逐模式逐层次连拍
                    if (step == 2)
                    {
                        // ⚠⚠ 改完状态**当拍不能拍**：WPF 还没重绘，截到的是上一状态的画面。
                        //   上一版就是这样——4 个模式的「全隐」张全都拍到了模型，
                        //   于是每条「静止性」不变量都是 254（= 整幅都在变），
                        //   而差异区形状恰好是角色轮廓，这才定位到「拍早了」。
                        //   所以：改状态一拍，等一拍，再拍。
                        r.Mode = Modes[mi];
                        r.Layer = li == 0 ? WpfPetRenderer.LayerKind.Nothing
                                : li == 1 ? WpfPetRenderer.LayerKind.NoModel
                                          : WpfPetRenderer.LayerKind.All;
                        w.InvalidateVisual();
                        step = 3;
                        return;
                    }
                    if (step == 3)
                    {
                        Keep(ModeNames[mi] + "_" + layers[li] + "_a");
                        step = 4;
                        return;
                    }
                    if (step == 4)
                    {
                        // 同一状态隔一拍再拍一张：验「背景静止」。背景一动，做差出的轮廓就是假的。
                        Keep(ModeNames[mi] + "_" + layers[li] + "_b");
                        string k = ModeNames[mi] + "_" + layers[li];
                        if (MaxDiff(bufs[k + "_a"], bufs[k + "_b"]) != 0 && retry < 4)
                        {
                            // ⚠ 偶发会拍到「上一帧」：WPF 的重绘不保证在我们等的那一拍里完成
                            //   （实测同一份代码，一次跑 12 条静止性全 0，下一次有两条是 253）。
                            //   对策不是把等待调长（那是碰运气），而是**不一致就两张都重拍**。
                            retry++; step = 3; return;
                        }
                        retry = 0;
                        li++;
                        if (li > 2) { li = 0; mi++; }
                        step = mi >= Modes.Length ? 5 : 2;
                        return;
                    }

                    // ---- 分析
                    if (step == 5)
                    {
                        // ① 不变量：窗口没动（窗口一动，裁框里的壁纸也跟着位移，做差出来的轮廓就是假的）
                        var rs = new HashSet<string>(rects);
                        rows.Add(Row("invariant.rect_stable", string.Join(" | ", new List<string>(rs).ToArray()),
                            rs.Count == 1 ? "PASS" : "FAIL"));

                        // 背板是否真的垫上了。⚠ 这条是上一版缺的那条不变量：
                        //   绿幕没合成上去也没人验，于是「已绘制像素」里 99.99% 是桌面壁纸，
                        //   量出来的「17% 纯黑」被当成「贴图碎了/渲染过暗」，白排查一轮。
                        Shot off0 = bufs["tex_off_a"];
                        int nDark = 0;
                        for (int y = 0; y < off0.H; y++)
                            for (int x = 0; x < off0.W; x++)
                            {
                                int i = y * off0.Stride + x * 4;
                                if (off0.P[i + 0] < 60 && off0.P[i + 1] < 60 && off0.P[i + 2] < 60) nDark++;
                            }
                        double darkFrac = nDark / (double)(off0.W * off0.H);
                        rows.Add(Row("backdrop.dark_frac_of_off", F4(darkFrac), darkFrac > 0.95 ? "PASS" : "FAIL"));

                        // ② 不变量：每层两次拍摄的差应恒为 0。注意「全隐」层里桌面是露出来的，
                        //    所以这条同时也验了「背景静止」——桌面一动，整份报告作废。
                        foreach (string nm in ModeNames)
                            foreach (string ly in layers)
                            {
                                int dv = MaxDiff(bufs[nm + "_" + ly + "_a"], bufs[nm + "_" + ly + "_b"]);
                                rows.Add(Row("invariant." + nm + "_" + ly + "_static", dv.ToString(),
                                    dv == 0 ? "PASS" : (dv <= 2 ? "INFO" : "FAIL")));
                            }

                        // ③ 掩码只推一次 —— 用 white 模式（模型全白、与壁纸和暗影的对比度最高）。
                        //    ⚠ 早先让每个模式各自推掩码，结果 tex 与 white 的「模型像素数」差了 5%：
                        //    深色贴图与暗影对比小、白模对比大，阈值卡住的位置不同 ⇒
                        //    各模式的均值算在**不同的像素集合**上，光照增益跟着失真。
                        int W = bufs["white_off_a"].W, H = bufs["white_off_a"].H;
                        Shot wOff = bufs["white_off_a"], wSh = bufs["white_shadow_a"], wAll = bufs["white_all_a"];
                        bool[] mModel = new bool[W * H];
                        bool[] mShadow = new bool[W * H];
                        int nModel = 0, nShadow = 0, nEdge = 0;
                        int bMinX = int.MaxValue, bMaxX = -1, bMinY = int.MaxValue, bMaxY = -1;
                            for (int y = 0; y < H; y++)
                            for (int x = 0; x < W; x++)
                            {
                                int i = y * W + x;
                                // 判据用「背景 → 全画」而不是「影 → 全画」：前者不依赖影子被谁压住，
                                // 且背板接近黑时，模型的差 ≥150、接地影的差只有 ~14，一刀 60 分得很干净。
                                int dOn = MaxCh(wOff, wAll, x, y);
                                int dShadow = MaxCh(wOff, wSh, x, y);
                                bool on = dOn > 60;
                                mModel[i] = on;
                                mShadow[i] = !on && dShadow > 6;
                                if (on)
                                {
                                    nModel++;
                                    if (dOn <= 100) nEdge++;           // 只差一点点 → 抗锯齿／半透明边
                                    if (x < bMinX) bMinX = x; if (x > bMaxX) bMaxX = x;
                                    if (y < bMinY) bMinY = y; if (y > bMaxY) bMaxY = y;
                                }
                                else if (mShadow[i]) nShadow++;
                            }
                        double area = W * (double)H;
                        rows.Add(Row("mask.model_px", nModel + " (" + F4(nModel / area) + " of rect)"));
                        rows.Add(Row("mask.shadow_px", nShadow + " (" + F4(nShadow / area) + ")"));
                        rows.Add(Row("mask.edge_px", nEdge + " (" + F4(nEdge / Math.Max(1, nModel)) + " of model)"));
                        rows.Add(Row("mask.bbox_top_bottom_frac",
                            F3(bMinY / (double)H) + "/" + F3((bMaxY + 1) / (double)H)));
                        rows.Add(Row("mask.bbox_left_right_frac",
                            F3(bMinX / (double)W) + "/" + F3((bMaxX + 1) / (double)W)));
                        WriteMasks(mModel, mShadow, W, H, Path.Combine(o.PixDir, "mask.png"));

                        // ④ 逐模式统计，全部落在**同一个** mModel 上
                        for (int m = 0; m < Modes.Length; m++)
                        {
                            string nm = ModeNames[m];
                            Shot all = bufs[nm + "_all_a"];
                            var acc = new long[] { 0, 0, 0 };
                            long lumSum = 0;
                            int nDark20 = 0, nDark64 = 0, nDark128 = 0, nPureBlack = 0;
                            int nClipAny = 0, nClipAll = 0;
                            for (int y = 0; y < H; y++)
                                for (int x = 0; x < W; x++)
                                {
                                    if (!mModel[y * W + x]) continue;
                                    int i = y * all.Stride + x * 4;
                                    int b = all.P[i + 0], g = all.P[i + 1], rr = all.P[i + 2];
                                    acc[0] += rr; acc[1] += g; acc[2] += b;
                                    int L = (rr * 299 + g * 587 + b * 114) / 1000;
                                    lumSum += L;
                                    if (L < 20) nDark20++;
                                    if (L < 64) nDark64++;
                                    if (L < 128) nDark128++;
                                    if (rr < 20 && g < 20 && b < 20) nPureBlack++;
                                    // 削波：光源总量 > 1 时，受光面会被顶到 255，白白的围裙会丢失层次。
                                    // 「够不够亮」和「有没有削波」是两件事，必须分开量。
                                    if (rr >= 250 || g >= 250 || b >= 250) nClipAny++;
                                    if (rr >= 250 && g >= 250 && b >= 250) nClipAll++;
                                }
                            double nn = Math.Max(1, nModel);
                            double mr = acc[0] / nn, mg = acc[1] / nn, mb = acc[2] / nn;
                            rows.Add(Row("mode." + nm + ".mean_rgb", F(mr) + "," + F(mg) + "," + F(mb)));
                            rows.Add(Row("mode." + nm + ".mean_lum", F(lumSum / nn)));
                            rows.Add(Row("mode." + nm + ".dark<20_frac", F4(nDark20 / nn)));
                            rows.Add(Row("mode." + nm + ".dark<64_frac", F4(nDark64 / nn)));
                            rows.Add(Row("mode." + nm + ".dark<128_frac", F4(nDark128 / nn)));
                            rows.Add(Row("mode." + nm + ".pure_black_frac", F4(nPureBlack / nn)));
                            rows.Add(Row("mode." + nm + ".clip_any_frac", F4(nClipAny / nn)));
                            rows.Add(Row("mode." + nm + ".clip_all_frac", F4(nClipAll / nn)));
                            if (m == 1) gain = new[] { mr / 255.0, mg / 255.0, mb / 255.0 };
                            WriteModelOnly(all, mModel, W, H, Path.Combine(o.PixDir, "model_" + nm + ".png"));
                        }

                        // ⑤ 光照增益：由 white 模式（纯白漫反射）反解。轮廓上的平均光照增益
                        if (gain != null)
                        {
                            rows.Add(Row("gain.per_channel", F3(gain[0]) + "," + F3(gain[1]) + "," + F3(gain[2])));
                            rows.Add(Row("gain.mean", F3((gain[0] + gain[1] + gain[2]) / 3)));
                            rows.Add(Row("gain.blue_over_red", F3(gain[2] / Math.Max(1e-6, gain[0]))));
                            // 建议灯色 = 现值 / 增益（线性照明下即中性白）
                            Color a = ParseCol(r.Stats, "amb"), k = ParseCol(r.Stats, "key");
                            rows.Add(Row("suggest.amb", Suggest(a, gain)));
                            rows.Add(Row("suggest.key", Suggest(k, gain)));
                        }
                        rows.Add(Row("render.stats", r.Stats));
                        rows.Add(Row("elapsed_s", F(clock.Elapsed.TotalSeconds)));

                        Console.WriteLine();
                        foreach (string[] row in rows)
                            Console.WriteLine(string.Format("  {0,-40} {1,-46} {2}", row[0], row[1], row[2]));

                        json.Append(" \"rows\": [");
                        for (int i = 0; i < rows.Count; i++)
                            json.Append(i == 0 ? "" : ", ").Append("{\"name\": ").Append(Js(rows[i][0]))
                                .Append(", \"value\": ").Append(Js(rows[i][1])).Append(", \"result\": ").Append(Js(rows[i][2])).Append("}");
                        json.Append("]}");
                        // ⚠ json 的构造是 `new StringBuilder("{")` … `Append("]}")` —— 起止括号
                        //   在构造里就已经配好了，落盘时**一个字都不能补**。之前这段在外面又包了
                        //   一对 `{` `}`，产出 `{{ "rows": … ]}}` 的非法 JSON（读的时候才炸）。
                        File.WriteAllText(Path.Combine(o.PixDir, "pix.json"),
                            json.ToString(), new UTF8Encoding(false));

                        bool bad = false;
                        foreach (string[] row in rows) if (row[2] == "FAIL") bad = true;
                        Console.WriteLine(bad ? "\n[pix] 有 FAIL 项" : "\n[pix] 全部通过");
                        rc = bad ? 1 : 0;
                        tl.Stop();
                        foreach (var kv in shots) kv.Value.Dispose();
                        app.Shutdown();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[pix] 异常：" + ex);
                    rc = 3;
                    tl.Stop();
                    app.Shutdown();
                }
            };
            tl.Start();
            app.Run();
            return rc;
        }

        // ------------------------------------------------------------------ 背板
        private static byte C8(int v) { return (byte)(v < 0 ? 0 : v > 255 ? 255 : v); }

        // ------------------------------------------------------------------ 截图
        private static SD.Bitmap Capture(PetWindow w)
        {
            int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            var full = new SD.Bitmap(vw, vh, SDI.PixelFormat.Format32bppArgb);
            using (SD.Graphics g = SD.Graphics.FromImage(full))
                g.CopyFromScreen(vx, vy, 0, 0, new SD.Size(vw, vh));
            Native.RECT wr;
            Native.GetWindowRect(w.Handle, out wr);
            var rect = new SD.Rectangle(wr.Left - vx, wr.Top - vy, wr.Right - wr.Left, wr.Bottom - wr.Top);
            if (rect.X < 0) { rect.Width += rect.X; rect.X = 0; }
            if (rect.Y < 0) { rect.Height += rect.Y; rect.Y = 0; }
            if (rect.Right > full.Width) rect.Width = full.Width - rect.X;
            if (rect.Bottom > full.Height) rect.Height = full.Height - rect.Y;
            if (rect.Width <= 0 || rect.Height <= 0) { full.Dispose(); return new SD.Bitmap(1, 1); }
            SD.Bitmap crop = full.Clone(rect, SDI.PixelFormat.Format32bppArgb);
            full.Dispose();
            return crop;
        }

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

        private static int MaxCh(Shot a, Shot b, int x, int y)
        {
            if (x >= a.W || y >= a.H) return 0;
            int ia = y * a.Stride + x * 4, ib = y * b.Stride + x * 4;
            int m = 0;
            for (int c = 0; c < 3; c++)
            {
                int d = Math.Abs(a.P[ia + c] - b.P[ib + c]);
                if (d > m) m = d;
            }
            return m;
        }

        private static int MaxDiff(Shot a, Shot b)
        {
            int m = 0, W = Math.Min(a.W, b.W), H = Math.Min(a.H, b.H);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int d = MaxCh(a, b, x, y);
                    if (d > m) m = d;
                }
            return m;
        }

        private static void WriteModelOnly(Shot all, bool[] mask, int W, int H, string path)
        {
            using (var outp = new SD.Bitmap(W, H, SDI.PixelFormat.Format32bppArgb))
            {
                SDI.BitmapData d = outp.LockBits(new SD.Rectangle(0, 0, W, H), SDI.ImageLockMode.WriteOnly, SDI.PixelFormat.Format32bppArgb);
                var buf = new byte[d.Stride * H];
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        int o = y * d.Stride + x * 4, i = y * all.Stride + x * 4;
                        if (mask[y * W + x])
                        {
                            buf[o + 0] = all.P[i + 0]; buf[o + 1] = all.P[i + 1];
                            buf[o + 2] = all.P[i + 2]; buf[o + 3] = 255;
                        }
                        else { buf[o + 0] = 240; buf[o + 1] = 240; buf[o + 2] = 238; buf[o + 3] = 255; }
                    }
                System.Runtime.InteropServices.Marshal.Copy(buf, 0, d.Scan0, buf.Length);
                outp.UnlockBits(d);
                outp.Save(path, SDI.ImageFormat.Png);
            }
        }

        private static void WriteMasks(bool[] model, bool[] shadow, int W, int H, string path)
        {
            using (var outp = new SD.Bitmap(W, H, SDI.PixelFormat.Format32bppArgb))
            {
                SDI.BitmapData d = outp.LockBits(new SD.Rectangle(0, 0, W, H), SDI.ImageLockMode.WriteOnly, SDI.PixelFormat.Format32bppArgb);
                var buf = new byte[d.Stride * H];
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        int o = y * d.Stride + x * 4, i = y * W + x;
                        byte r = 30, g = 30, b = 34;
                        if (shadow[i]) { r = 90; g = 120; b = 160; }
                        if (model[i]) { r = 240; g = 200; b = 60; }
                        buf[o + 0] = b; buf[o + 1] = g; buf[o + 2] = r; buf[o + 3] = 255;
                    }
                System.Runtime.InteropServices.Marshal.Copy(buf, 0, d.Scan0, buf.Length);
                outp.UnlockBits(d);
                outp.Save(path, SDI.ImageFormat.Png);
            }
        }

        // ------------------------------------------------------------------ 小工具
        private static string Rect(PetWindow w)
        {
            Native.RECT r; Native.GetWindowRect(w.Handle, out r);
            return r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top);
        }

        private static Color ParseCol(string stats, string key)
        {
            int i = stats.IndexOf(key + "=", StringComparison.Ordinal);
            if (i < 0) return Color.FromArgb(0, 0, 0);
            string s = stats.Substring(i + key.Length + 1);
            int sp = s.IndexOf(' ');
            if (sp > 0) s = s.Substring(0, sp);
            string[] p = s.Split(',');
            if (p.Length < 3) return Color.FromArgb(0, 0, 0);
            return Color.FromArgb(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]));
        }

        private static string Suggest(Color c, double[] gain)
        {
            return Cl(c.R / gain[0]) + "," + Cl(c.G / gain[1]) + "," + Cl(c.B / gain[2]);
        }
        private static int Cl(double v) { return (int)Math.Round(Math.Min(255, Math.Max(0, v))); }

        private static string[] Row(string n, string v) { return Row(n, v, "INFO"); }
        private static string[] Row(string n, string v, string r) { return new[] { n, v, r }; }
        private static string F(double v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }
        private static string F3(double v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }
        private static string F4(double v) { return v.ToString("0.0000", CultureInfo.InvariantCulture); }

        private static string Js(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
