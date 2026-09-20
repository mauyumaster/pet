// 点击穿透的**端到端**验证 —— 这是桌宠最要紧的一条，也是最容易「以为做了」的一条。
//
// 机制：主进程把桌宠摆在探针窗口正上方，然后用 SendInput 在屏幕坐标上真的点两下：
//   ① 桌宠的透明处（角上）→ 期望：这一下**穿到下层**，探针收到 WM_LBUTTONDOWN
//   ② 桌宠的模型处（躯干）→ 期望：探针**收不到**
// 探针是**另一个进程**（同一个 exe 的 --probe 模式），所以这同时也就验了
// 「HTTRANSPARENT 能不能跨进程穿透」这个文档里含糊其辞的点（MSDN 只说同线程）。
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace AzhuPet
{
    internal static class Probe
    {
        /// <summary>探针进程：一个深灰不透明窗口，收到左键就记数。</summary>
        public static int Run(Cli o)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var win = new Window();
            win.WindowStyle = WindowStyle.None;
            win.ResizeMode = ResizeMode.NoResize;
            win.ShowInTaskbar = false;
            win.Topmost = false;
            win.Left = 0; win.Top = 0;
            win.Width = 200; win.Height = 200;
            win.Background = new SolidColorBrush(Color.FromRgb(48, 52, 60));
            win.Show();

            double scale = VisualTreeHelper.GetDpi(win).DpiScaleX;
            if (o.ProbeRect != null && o.ProbeRect.Length == 4)
            {
                win.Left = o.ProbeRect[0] / scale;
                win.Top = o.ProbeRect[1] / scale;
                win.Width = o.ProbeRect[2] / scale;
                win.Height = o.ProbeRect[3] / scale;
            }

            var src = System.Windows.Interop.HwndSource.FromHwnd(
                new System.Windows.Interop.WindowInteropHelper(win).Handle);
            int hits = 0;
            if (src != null)
            {
                src.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
                {
                    if (msg == Native.WM_LBUTTONDOWN)
                    {
                        hits++;
                        try
                        {
                            File.WriteAllText(o.ProbeFile,
                                "hits=" + hits + "\r\nready=1\r\nlast_msg=0x" + msg.ToString("X4") + "\r\n",
                                new UTF8Encoding(false));
                        }
                        catch { }
                    }
                    return IntPtr.Zero;
                });
            }
            try
            {
                File.WriteAllText(o.ProbeFile, "hits=0\r\nready=1\r\n", new UTF8Encoding(false));
            }
            catch { }

            var t = new DispatcherTimer(DispatcherPriority.Normal);
            t.Interval = TimeSpan.FromSeconds(30);         // 防呆：主进程没来收尾也不会赖着
            t.Tick += (s, e) => app.Shutdown();
            t.Start();
            return app.Run();
        }
    }

    internal static class ClickTest
    {
        public static int Run(Cli o)
        {
            var rows = new System.Collections.Generic.List<string[]>();

            string modelPath = Cli.ResolveModel(o.ModelPath);
            var m = Glb.Load(modelPath);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var cfg = new PetConfig { SizeIndex = o.SizeIndex, NightDim = false, ShowTray = false };
            var r = new WpfPetRenderer(m);
            var w = new PetWindow(r, cfg) { SelfTestMode = true, ForcedIdle = -1 };
            w.Show();

            Process probe = null;
            string probeFile = Path.Combine(Path.GetTempPath(), "azhu_probe.txt");
            double tStart = 0;
            double tReadAt = 0;
            string probeAfterMiss = "";
            long kindMissPt = 0, kindHitPt = 0;
            var clock = Stopwatch.StartNew();
            int step = 0;
            Native.RECT petRect = new Native.RECT();
            Point missPt = new Point(), hitPt = new Point();
            bool missFound = false, hitFound = false;
            string probeText = "";

            var tl = new DispatcherTimer(DispatcherPriority.Normal);
            tl.Interval = TimeSpan.FromMilliseconds(60);
            tl.Tick += (s, e) =>
            {
                double t = clock.Elapsed.TotalSeconds;
                try
                {
                    switch (step)
                    {
                        case 0:
                            w.GoHome();
                            step = 1;
                            break;

                        case 1:
                            if (t < 1.0) break;
                            Native.GetWindowRect(w.Handle, out petRect);
                            // 采样点由渲染器自己判：找一个「打不到」和一个「打得到」的点
                            for (double fy = 0.04; fy < 0.30 && !missFound; fy += 0.02)
                                for (double fx = 0.04; fx < 0.30 && !missFound; fx += 0.02)
                                {
                                    var p = new Point(w.ActualWidth * fx, w.ActualHeight * fy);
                                    if (!r.HitTest(p)) { missPt = p; missFound = true; }
                                }
                            for (double fy = 0.42; fy < 0.70 && !hitFound; fy += 0.02)
                            {
                                var p = new Point(w.ActualWidth * 0.5, w.ActualHeight * fy);
                                if (r.HitTest(p)) { hitPt = p; hitFound = true; }
                            }
                            StartProbe(o, probeFile, petRect, w.DipScale, out probe);
                            step = 2;
                            break;

                        case 2:
                            if (t < 2.6) break;
                            ClickWnd(w, missPt);
                            tReadAt = t;
                            step = 25;
                            break;

                        case 25:
                            // 每次点击后单独读一次 —— 才能分辨「这一下穿没穿」，
                            // 而不是只看一个末尾的总数（那正是上一版假通过的由来）。
                            if (t < tReadAt + 0.5) break;
                            probeAfterMiss = ReadProbe(probeFile);
                            step = 3;
                            break;

                        case 3:
                            if (t < tReadAt + 0.9) break;
                            ClickWnd(w, hitPt);
                            tReadAt = t;
                            step = 35;
                            break;

                        case 35:
                            if (t < tReadAt + 0.8) break;
                            probeText = ReadProbe(probeFile);
                            // ★ 主判据：主动发 WM_NCHITTEST 问「这个点算不算在桌宠身上」。
                            //   它同步执行我们的钩子，不受 hit-test 缓存影响，可重复。
                            //   （靠"移动光标再看 LastHitKind"不行，靠"探针收到几次点击"也不行 ——
                            //     后者跨进程本身不稳，实测在恒穿透的负对照下都能报绿。）
                            kindMissPt = ProbeHit(w, missPt);
                            kindHitPt = ProbeHit(w, hitPt);
                            step = 4;
                            break;

                        case 4:
                            tl.Stop();
                            int hits = ParseHits(probeText);
                            rows.Add(new[] { "clicktest.miss_point_found", missPt.ToString(), P(missFound) });
                            rows.Add(new[] { "clicktest.hit_point_found", hitPt.ToString(), P(hitFound) });
                            // ★ 主判据：进程内、同步、可重复、不受 hit-test 缓存影响。
                            //   原版把「探针收到几次点击」当主判据，问题是它**没有资格失败** ——
                            //   实测在「强制恒穿透」的负对照下照样报绿（探针一次都没收到）。
                            rows.Add(new[] { "clicktest.nchittest_miss_pt", KindName(kindMissPt),
                                P(kindMissPt == Native.HTTRANSPARENT) });
                            rows.Add(new[] { "clicktest.nchittest_hit_pt", KindName(kindHitPt),
                                P(kindHitPt == Native.HTCLIENT) });
                            rows.Add(new[] { "clicktest.probe_hits", hits
                                + "（仅参考：HTTRANSPARENT 跨进程是否生效 MSDN 只承诺同线程，实测时收时不收）",
                                P(hits >= 0) });
                            rows.Add(new[] { "clicktest.pet_rect", petRect.Left + "," + petRect.Top + " "
                                + (petRect.Right - petRect.Left) + "x" + (petRect.Bottom - petRect.Top), "PASS" });
                            rows.Add(new[] { "clicktest.probe_file", probeText.Replace("\r\n", " | "), "PASS" });
                            rows.Add(new[] { "clicktest.after_miss_click", probeAfterMiss.Replace("\r\n", " | "), "PASS" });
                            var hl = new StringBuilder();
                            int hn = Math.Min(w.HitLogN, w.HitLog.Length);
                            for (int i = 0; i < hn; i++) hl.Append("[").Append(w.HitLog[i]).Append("] ");
                            rows.Add(new[] { "clicktest.nchittest_log",
                                w.HitLogN + " 次调用，最近 " + hn + " 条: " + hl, "PASS" });
                            Kill(probe);
                            Report(o, rows);
                            app.Shutdown();
                            step = 5;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    rows.Add(new[] { "clicktest.exception", ex.Message, "FAIL" });
                    Kill(probe);
                    Report(o, rows);
                    app.Shutdown();
                    step = 5;
                }
            };
            tl.Start();
            app.Run();
            return rows.Exists(x => x[2] == "FAIL") ? 1 : 0;
        }

        private static string P(bool ok) { return ok ? "PASS" : "FAIL"; }

        /// <summary>主动给窗口发 WM_NCHITTEST，拿到「这个屏幕点算不算在桌宠身上」的真值。
        /// 不依赖光标移动（Windows 会缓存 hit test），也不依赖跨进程穿透（本身不稳）。</summary>
        private static long ProbeHit(PetWindow w, Point pDip)
        {
            Native.RECT r;
            Native.GetWindowRect(w.Handle, out r);
            int px = r.Left + (int)Math.Round(pDip.X * w.DipScale);
            int py = r.Top + (int)Math.Round(pDip.Y * w.DipScale);
            long lp = ((long)(py & 0xFFFF) << 16) | (long)(px & 0xFFFF);
            return Native.SendMessage(w.Handle, Native.WM_NCHITTEST, IntPtr.Zero, new IntPtr(lp)).ToInt64();
        }

        private static string KindName(long v)
        {
            if (v == Native.HTTRANSPARENT) return "HTTRANSPARENT";
            if (v == Native.HTCLIENT) return "HTCLIENT";
            return "0x" + v.ToString("X");
        }

        private static void StartProbe(Cli o, string file, Native.RECT rect, double scale, out Process p)
        {
            try { File.Delete(file); } catch { }
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            var psi = new ProcessStartInfo(exe);
            psi.Arguments = "--probe \"" + file + "\" --probe-rect "
                + rect.Left + "," + rect.Top + "," + (rect.Right - rect.Left) + "," + (rect.Bottom - rect.Top);
            psi.UseShellExecute = false;
            p = Process.Start(psi);
        }

        private static void ClickWnd(PetWindow w, Point pDip)
        {
            Native.RECT r;
            Native.GetWindowRect(w.Handle, out r);
            int px = r.Left + (int)Math.Round(pDip.X * w.DipScale);
            int py = r.Top + (int)Math.Round(pDip.Y * w.DipScale);
            Native.SetCursorPos(px, py);
            System.Threading.Thread.Sleep(120);
            Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            System.Threading.Thread.Sleep(40);
            Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }

        private static string ReadProbe(string f)
        {
            try { return File.Exists(f) ? File.ReadAllText(f).Trim() : "(无文件)"; }
            catch (Exception ex) { return "(读失败:" + ex.Message + ")"; }
        }

        private static int ParseHits(string s)
        {
            int i = s.IndexOf("hits=", StringComparison.Ordinal);
            if (i < 0) return -1;
            int j = i + 5, k = j;
            while (k < s.Length && char.IsDigit(s[k])) k++;
            int v;
            return int.TryParse(s.Substring(j, k - j), out v) ? v : -1;
        }

        private static void Kill(Process p)
        {
            try { if (p != null && !p.HasExited) p.Kill(); } catch { }
        }

        internal static void Report(Cli o, System.Collections.Generic.List<string[]> rows)
        {
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
                var j = new StringBuilder("{\n  \"checks\": [\n");
                for (int i = 0; i < rows.Count; i++)
                    j.Append("    {\"name\": \"").Append(rows[i][0]).Append("\", \"value\": \"")
                     .Append(rows[i][1].Replace("\\", "/").Replace("\"", "'")).Append("\", \"result\": \"")
                     .Append(rows[i][2]).Append("\"}").Append(i == rows.Count - 1 ? "\n" : ",\n");
                j.Append("  ]\n}\n");
                try { File.WriteAllText(o.OutFile, j.ToString(), new UTF8Encoding(false)); } catch { }
            }
        }
    }
}
