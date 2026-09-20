// 拖拽的**端到端**验证 —— 「能拖」这件事必须用真鼠标事件量出来，不能靠读代码下结论。
//
// 背景：上一轮只写了 `--clicktest`（点击穿透），**从没测过拖拽**，于是「拖拽抛物已实现」
// 实际只是「代码里写了」。这次补上：真的按下、真的分步移动、真的松开，量窗口位移。
//
// 两个实验 —— 缺了 B 就没有判据：
//   A 按住模型躯干拖 → 期望窗口跟着走（位移 ≈ 光标位移）
//   B 按住窗口透明角落拖 → 期望窗口**纹丝不动**（那一下该穿到下层去）
//   B 是对照组。没有它，A 通过可能只是因为「整个窗口都能拖」这个更宽的条件成立，
//   那样「逐像素命中」就等于没做。
//
// 失败要能定位到环：NCHITTEST 没给 HTCLIENT ⇒ 卡在命中判定；
// 给了但 DownCount=0 ⇒ 卡在输入送达（WS_EX_NOACTIVATE / 激活）；Down>0 而没位移 ⇒ 卡在 OnMove。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace AzhuPet
{
    internal static class DragTest
    {
        const int Steps = 8;        // 分步移动的步数
        const int StepPx = 32;      // 每步位移（物理像素）

        public static int Run(Cli o)
        {
            var rows = new List<string[]>();
            string modelPath = Cli.ResolveModel(o.ModelPath);
            var m = Glb.Load(modelPath);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var cfg = new PetConfig { SizeIndex = o.SizeIndex, NightDim = false, ShowTray = false };
            var r = new WpfPetRenderer(m);
            var w = new PetWindow(r, cfg) { SelfTestMode = true, ForcedIdle = -1 };
            w.Show();

            var clock = Stopwatch.StartNew();
            var tl = new DispatcherTimer(DispatcherPriority.Normal);
            tl.Interval = TimeSpan.FromMilliseconds(50);

            int step = 0, sub = 0;
            int atX = (o.At != null && o.At.Length == 2) ? o.At[0] : 900;   // 只取 X；Y 一律贴地

            Native.RECT a0 = new Native.RECT(), a1 = new Native.RECT(), a2 = new Native.RECT();
            Native.RECT b0 = new Native.RECT(), b1 = new Native.RECT();
            Point hitPt = new Point(), missPt = new Point();
            bool hitFound = false, missFound = false;
            int aDowns0 = 0, aHitKindAt = 0;
            string hitKindA = "", hitKindB = "";
            Native.RECT prevRect = new Native.RECT();
            int stable = 0;
            Native.RECT bAtSet = new Native.RECT();
            Point bCursorSet = new Point();
            string bMissDiag = "";
            int bDowns0 = 0;

            tl.Tick += (s, e) =>
            {
                double t = clock.Elapsed.TotalSeconds;
                try
                {
                    switch (step)
                    {
                        case 0:
                            // ⚠ 两条口径要求，缺一个数就会偏：
                            //   ① 摆到屏幕中部 —— 靠右下角起拖会撞到屏幕边界，位移被截断；
                            //   ② **贴地** —— 摆在半空的话它会先自由落体一段，那段里水平速度
                            //      只受微弱空气阻力，滑行距离会混进自由落体带来的漂移
                            //      （实测：摆在半空量到 901px，其中约 350px 是"掉下来的"）。
                            if (t < 0.7) break;
                            w.Left = atX / w.DipScale;
                            w.Top = SystemParameters.WorkArea.Bottom - r.FeetYDip;
                            step = 1;
                            break;

                        case 1:
                            if (t < 1.5) break;
                            Native.GetWindowRect(w.Handle, out a0);
                            // 采样点交给渲染器自己判：各找一个「打得到」和「打不到」的点
                            for (double fy = 0.45; fy < 0.80 && !hitFound; fy += 0.02)
                            {
                                var p = new Point(w.ActualWidth * 0.5, w.ActualHeight * fy);
                                if (r.HitTest(p)) { hitPt = p; hitFound = true; }
                            }
                            for (double fy = 0.03; fy < 0.30 && !missFound; fy += 0.02)
                                for (double fx = 0.03; fx < 0.30 && !missFound; fx += 0.02)
                                {
                                    var p = new Point(w.ActualWidth * fx, w.ActualHeight * fy);
                                    if (!r.HitTest(p)) { missPt = p; missFound = true; }
                                }
                            MoveCursorTo(w, hitPt);

                            // 坐标口径自检：WPF 的 PointToScreen 返回的到底是 **DIP** 还是 **物理像素**？
                            //   WM_NCHITTEST 给的是物理像素；若上者返回 DIP，则必须除以 DipScale 才能喂给
                            //   PointFromScreen，否则每点一次都算到窗口外面去（= 恒判透明 = 拖不动）。
                            Point scr = w.PointToScreen(hitPt);
                            Point back = w.PointFromScreen(scr);
                            rows.Add(new[] { "drag.coord.roundtrip",
                                "dip(" + (int)hitPt.X + "," + (int)hitPt.Y + ") -> screen("
                                + (int)scr.X + "," + (int)scr.Y + ") -> dip(" + (int)back.X + "," + (int)back.Y + ")",
                                P(Math.Abs(back.X - hitPt.X) < 1.5 && Math.Abs(back.Y - hitPt.Y) < 1.5) });
                            rows.Add(new[] { "drag.coord.scale",
                                "DipScale=" + w.DipScale.ToString("0.###")
                                + " wpfLeft=" + w.Left.ToString("0.#") + " wpfTop=" + w.Top.ToString("0.#")
                                + " | GetWindowRect=" + a0.Left + "," + a0.Top + " "
                                + (a0.Right - a0.Left) + "x" + (a0.Bottom - a0.Top)
                                + " | Actual=" + w.ActualWidth.ToString("0.#") + "x" + w.ActualHeight.ToString("0.#"),
                                "PASS" });
                            step = 2;
                            break;

                        case 2:
                            if (t < 2.0) break;
                            hitKindA = w.LastHitKind;      // 光标停在模型上时，NCHITTEST 给的是什么
                            aDowns0 = w.DownCount;
                            Down();
                            sub = 0;
                            step = 3;
                            break;

                        case 3:
                            // 分步移动：一步一个 tick，避免一次性跳过去时 WPF 只收到一个 MouseMove
                            if (sub >= Steps) { step = 4; break; }
                            Nudge(StepPx, 0);
                            sub++;
                            break;

                        case 4:
                            if (t < 2.0 + Steps * 0.05 + 0.30) break;
                            Up();
                            step = 5;
                            break;

                        case 5:
                            if (t < 2.6) break;
                            // 等抛物真的停（Airborne 由物理自己清），别用固定时长猜
                            if ((w.Airborne || w.Dragging) && t < 25) break;
                            Native.GetWindowRect(w.Handle, out a1);
                            a2 = a1;
                            prevRect = a1; stable = 0;
                            step = 6;
                            break;

                        case 6:
                            // ---- 对照实验 B：拖透明角落
                            // ⚠ 必须等窗口**完全停稳**再定位。否则 B 的光标是按滑动中的位置算的，
                            //   窗口一滑走，那个"透明角落"就落到模型身上了 —— 实测正是如此
                            //   （B 的 blank 点被量成 client），会把对照实验整个变成假失败。
                            if (w.Airborne || w.Dragging) break;
                            Native.RECT q; Native.GetWindowRect(w.Handle, out q);
                            if (Dpx(prevRect, q) > 1) { prevRect = q; stable = 0; break; }
                            prevRect = q;
                            if (++stable < 3) break;
                            bCursorSet = MoveCursorTo(w, missPt);
                            Native.GetWindowRect(w.Handle, out bAtSet);
                            bMissDiag = "窗口=" + q.Left + "," + q.Top + " missDip=" + missPt.X + "," + missPt.Y
                                      + " dpi=" + w.DipScale.ToString("0.##") + " 设光标=" + bCursorSet.X + "," + bCursorSet.Y;
                            step = 7;
                            break;

                        case 7:
                            if (t < 0) break;
                            step = 8;
                            break;

                        case 8:
                            hitKindB = w.LastHitKind;
                            Native.POINT cp; Native.GetCursorPos(out cp);
                            Native.GetWindowRect(w.Handle, out b0);
                            bMissDiag += " ‖ 读时 光标=" + cp.X + "," + cp.Y
                                       + " 窗口=" + b0.Left + "," + b0.Top + " NCHITTEST(参考)=" + hitKindB;
                            bDowns0 = w.DownCount;
                            Down();
                            sub = 0;
                            step = 9;
                            break;

                        case 9:
                            if (sub >= Steps) { step = 10; break; }
                            Nudge(StepPx, 0);
                            sub++;
                            break;

                        case 10:
                            Up();
                            step = 11;
                            break;

                        case 11:
                            Native.GetWindowRect(w.Handle, out b1);
                            tl.Stop();

                            int aMove = Dpx(a0, a1), bMove = Dpx(b0, b1);
                            int expect = Steps * StepPx;

                            rows.Add(new[] { "drag.hit_point_found", hitPt.ToString(), P(hitFound) });
                            rows.Add(new[] { "drag.miss_point_found", missPt.ToString(), P(missFound) });
                            rows.Add(new[] { "drag.A.nchittest_on_model", hitKindA,
                                P(hitKindA.StartsWith("client")) });
                            rows.Add(new[] { "drag.A.down_count", w.DownCount + "（拖 A 前后 " + aDowns0
                                + "→" + w.DownCount + "）", P(w.DownCount > aDowns0) });
                            rows.Add(new[] { "drag.A.move_count", w.MoveCount.ToString(), P(w.MoveCount > 0) });
                            rows.Add(new[] { "drag.A.moved_px", aMove + "（期望 ≈" + expect + "，含抛物滑行）",
                                P(aMove >= 180) });
                            rows.Add(new[] { "drag.A.rejected_hit", w.DownRejectedHit + " / lock "
                                + w.DownRejectedLock, P(w.DownRejectedHit == 0 && w.DownRejectedLock == 0) });
                            // ⚠ B 的判据不能用 LastHitKind —— Windows 会**缓存** hit test：窗口静止时
                            //   光标在同一窗口内部移动，WM_NCHITTEST 可能根本不被重复调用（实测：B 期间
                            //   一次都没调，读到的是实验 A 拖拽途中的旧值，于是把透明角落误判成 client）。
                            //   直接判「这一按有没有送到桌宠」更本质：穿透的定义就是这个。
                            rows.Add(new[] { "drag.B.down_count_delta", (w.DownCount - bDowns0)
                                + "（期望 0：透明处按住不该到桌宠）", P(w.DownCount == bDowns0) });
                            rows.Add(new[] { "drag.B.nchittest_note", hitKindB, "PASS" });
                            rows.Add(new[] { "drag.B.diag", bMissDiag, "PASS" });
                            rows.Add(new[] { "drag.B.moved_px", bMove + "（期望 ≈0：透明处应穿透，不该被拖）",
                                P(bMove <= 10) });
                            rows.Add(new[] { "drag.rects", "A " + Dpx(a0, a2) + "px  B " + bMove + "px", "PASS" });

                            ClickTest.Report(o, rows);
                            app.Shutdown();
                            step = 12;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    rows.Add(new[] { "drag.exception", ex.GetType().Name + ": " + ex.Message, "FAIL" });
                    ClickTest.Report(o, rows);
                    app.Shutdown();
                    step = 12;
                }
            };
            tl.Start();
            app.Run();
            return rows.Exists(x => x[2] == "FAIL") ? 1 : 0;
        }

        private static string P(bool ok) { return ok ? "PASS" : "FAIL"; }

        /// <summary>窗口左上角的位移（物理像素，曼哈顿距离）。</summary>
        private static int Dpx(Native.RECT a, Native.RECT b)
        {
            return Math.Abs(b.Left - a.Left) + Math.Abs(b.Top - a.Top);
        }

        private static Point MoveCursorTo(PetWindow w, Point pDip)
        {
            Native.RECT r;
            Native.GetWindowRect(w.Handle, out r);
            int px = r.Left + (int)Math.Round(pDip.X * w.DipScale);
            int py = r.Top + (int)Math.Round(pDip.Y * w.DipScale);
            Native.SetCursorPos(px, py);
            return new Point(px, py);
        }

        private static void Nudge(int dx, int dy)
        {
            Native.POINT c;
            if (Native.GetCursorPos(out c)) Native.SetCursorPos(c.X + dx, c.Y + dy);
        }

        private static void Down()
        {
            Native.mouse_event(Native.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        }

        private static void Up()
        {
            Native.mouse_event(Native.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }
    }
}
