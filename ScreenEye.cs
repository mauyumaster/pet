// L4（像素级）—— 把「屏幕上具体的内容」变成她能看到的东西。
//
// ⚠⚠ **这一层是不可逆的口径变化**，所以默认关：
//   A 档（进程名）与 B 档（元素树）都是**不出本机**的；像素级要**把屏幕发出去**。
//   不可逆的性质不能靠口头承诺 —— 由 `eyeOn`（托盘「她能看屏幕」）显式打开。
//
// ⚠ 两条纪律：
//   ① 截图**只在内存里活一次**：编码后立刻发走，**永不写盘**（同「可以看、绝不能留档」）。
//      --eyetest 会扫临时目录确认没有落下图片文件。
//   ② 不拍她自己 —— 否则她在图里看见自己，重演「……换到 pet 了。」
//
// ⚠ 为什么 Capture 要吃 `expectProc`，而不是「谁在前台就拍谁」：
//   观察（Tick 里的 Sample）与截图之间隔着一次模型调用，前台可能已经变了。
//   那会变成「她说的是 A，图里是 B」—— **同一份数据两个落点**的变体。
//   所以进程名对不上就**不拍**：宁可少一张图，也不给一张对不上的。
using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AzhuPet
{
    internal static class ScreenEye
    {
        /// <summary>⚠ 默认关。开了 ＝「屏幕内容可以出本机」。</summary>
        public static bool Enabled;

        /// <summary>缩到多宽。原图直发又慢又贵，视觉模型对大图还会自己切块。</summary>
        public static int MaxWidth = 1280;
        public static int JpegQuality = 70;

        /// <summary>一张截图。⚠ 只在内存里活着 —— 调用方用完即弃，不许写盘。</summary>
        internal sealed class Shot
        {
            public byte[] Jpeg;
            public int SrcW, SrcH, OutW, OutH;
            public string Proc, Title, How;
            /// <summary>这张图是**屏幕拷贝**来的（不是窗口自己画的）⇒ 可能含遮挡物。
            /// ⚠ 截「上一个真实窗口」时这条会常态为真（那个窗口通常不在前台上），必须让用户看得见。</summary>
            public bool Occluded;
            public int Bytes { get { return Jpeg == null ? 0 : Jpeg.Length; } }

            // ---- 给 D 档（本地读字）留的出口 ----
            // ⚠ 同一张图两条路：**发出去**走 Jpeg，**留在本机识别**走 Pixels。
            //   不许把「有没有像素」和「有没有 JPEG」当成同一件事 —— 它们是两个消费者。
            public byte[] Pixels;            // BGRA8（预乘），stride = PixelW*4。null = 这次只要 JPEG
            public int PixelW, PixelH;

            public string Describe()
            {
                return Proc + "  " + SrcW + "×" + SrcH + " → " + OutW + "×" + OutH
                     + "  JPEG " + (Bytes / 1024) + " KB  [" + How + "]";
            }
        }

        // ==================================================================== 截图

        /// <summary>
        /// 截「前台窗口」。返回 null ＝ 不拍（读不到 / 前台是她自己 / **前台已经换走了**）。
        /// </summary>
        public static Shot Capture(string expectProc)
        {
            return Capture(expectProc, false, MaxWidth);
        }

        /// <summary>
        /// ⚠ `wantPixels` 决定「这张图是给谁看的」：给模型看 ⇒ 编 JPEG；给**本机 OCR** 看 ⇒ 留原始像素。
        ///   `maxWidth &lt;= 0` ＝ 不缩小。读字这条路**默认不缩小** —— 缩小会掉字，而掉了几个字
        ///   看不出来（那是静默失败，不是降级）。
        /// </summary>
        public static Shot Capture(string expectProc, bool wantPixels, int maxWidth)
        {
            return CaptureWindow(Native.GetForegroundWindow(), expectProc, wantPixels, maxWidth);
        }

        /// <summary>
        /// 截**指定窗口**。
        ///
        /// ⚠⚠ 为什么需要它（2026-09-20 用户实拍「她只看见了自己」）：
        ///   托盘菜单属于 pet.exe，菜单一关，前台就落回她自己的窗口。此时若还按
        ///   「当前前台」截，截到的一定是她自己。⇒ 调用方应当先把**那个窗口**定下来
        ///   （`Watcher.ReadTarget()`），再按句柄来截，而不是在这里重新问一次「谁在前台」。
        ///
        /// ⚠ 传进来的窗口多半**不在前台上**，所以三个门必须先过：句柄还有效、没最小化、可见。
        ///   最小化的窗口 `PrintWindow` 会**成功返回但是空白**（同「返回 true 却画出黑图」那族），
        ///   不提前拦住就会把空白当成「这一屏没有字」报出去。
        /// ⚠ `expectProc` 留空 ＝ 不校验（手动路径）；给了就严格校验（自动路径：她说的 A 与截到的必须是同一个）。
        /// </summary>
        public static Shot CaptureWindow(IntPtr hwnd, string expectProc, bool wantPixels, int maxWidth)
        {
            IntPtr hdcScreen = IntPtr.Zero, hdcMem = IntPtr.Zero, hBmp = IntPtr.Zero, hOld = IntPtr.Zero;
            try
            {
                if (hwnd == IntPtr.Zero) return null;
                if (!Native.IsWindow(hwnd)) return null;         // 那个窗口已经关掉了
                if (Native.IsIconic(hwnd)) return null;          // 最小化 ⇒ PrintWindow 出空白
                if (!Native.IsWindowVisible(hwnd)) return null;  // 不可见 ⇒ 同理

                int pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                string proc = null;
                try { using (var p = System.Diagnostics.Process.GetProcessById(pid)) proc = p.ProcessName; }
                catch { proc = null; }
                if (string.IsNullOrEmpty(proc)) return null;
                if (proc == Watcher.SelfName()) return null;                       // 她自己的窗口：不拍
                if (!string.IsNullOrEmpty(expectProc)
                    && !string.Equals(proc, expectProc, StringComparison.OrdinalIgnoreCase))
                    return null;                                                   // 不是她说的那个窗口 ⇒ 不拍

                Native.RECT r;
                if (!Native.GetWindowRect(hwnd, out r)) return null;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w < 64 || h < 64 || w > 16000 || h > 16000) return null;       // 退化尺寸：不拍

                var sb = new System.Text.StringBuilder(512);
                Native.GetWindowText(hwnd, sb, sb.Capacity);
                string title = sb.ToString();

                hdcScreen = Native.GetDC(IntPtr.Zero);
                if (hdcScreen == IntPtr.Zero) return null;
                hdcMem = Native.CreateCompatibleDC(hdcScreen);
                hBmp = Native.CreateCompatibleBitmap(hdcScreen, w, h);
                if (hdcMem == IntPtr.Zero || hBmp == IntPtr.Zero) return null;
                hOld = Native.SelectObject(hdcMem, hBmp);

                string how;
                BitmapSource src = Render(hBmp, w, h, hwnd, hdcMem, hdcScreen, r, out how);
                if (src == null) return null;

                BitmapSource outSrc = src;
                if (maxWidth > 0 && src.PixelWidth > maxWidth)
                {
                    double k = (double)maxWidth / src.PixelWidth;
                    var tb = new TransformedBitmap(src, new ScaleTransform(k, k));
                    tb.Freeze();
                    outSrc = tb;
                }

                var shot = new Shot
                {
                    SrcW = src.PixelWidth, SrcH = src.PixelHeight,
                    OutW = outSrc.PixelWidth, OutH = outSrc.PixelHeight,
                    Proc = proc, Title = title, How = how,
                    Occluded = how != null && how.IndexOf("含遮挡", StringComparison.Ordinal) >= 0,
                };

                if (wantPixels)
                {
                    // ⚠ 不编 JPEG 是有意的：JPEG 有损，而**字的边缘**正是最容易掉的地方。
                    // ⚠⚠ 必须先显式转成 Pbgra32 再取像素：CreateBitmapSourceFromHBitmap 交出来的是
                    //   Bgr32（3 字节/像素）还是 Pbgra32 取决于窗口本身，而 OCR 那边按 4 字节/像素 解。
                    //   格式对不上不是报错，是**整幅错位** —— 识别结果会变成乱码。
                    var pbgra = new FormatConvertedBitmap(outSrc, PixelFormats.Pbgra32, null, 0);
                    pbgra.Freeze();
                    int pw = pbgra.PixelWidth, ph = pbgra.PixelHeight, stride = pw * 4;
                    var px = new byte[(long)stride * ph];
                    pbgra.CopyPixels(px, stride, 0);
                    shot.PixelW = pw; shot.PixelH = ph; shot.Pixels = px;
                }
                else
                {
                    var enc = new JpegBitmapEncoder();
                    enc.QualityLevel = JpegQuality;
                    enc.Frames.Add(BitmapFrame.Create(outSrc));
                    using (var ms = new MemoryStream())
                    {
                        enc.Save(ms);
                        shot.Jpeg = ms.ToArray();
                    }
                }
                return shot;
            }
            catch { return null; }
            finally
            {
                // ⚠ 顺序不能反：先把原对象选回去，再删位图，否则位图仍被 DC 占用 ⇒ 泄漏。
                try { if (hOld != IntPtr.Zero && hdcMem != IntPtr.Zero) Native.SelectObject(hdcMem, hOld); } catch { }
                try { if (hBmp != IntPtr.Zero) Native.DeleteObject(hBmp); } catch { }
                try { if (hdcMem != IntPtr.Zero) Native.DeleteDC(hdcMem); } catch { }
                try { if (hdcScreen != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, hdcScreen); } catch { }
            }
        }

        /// <summary>把窗口画进位图。失败原因**写进 how**，不许静默降级。</summary>
        private static BitmapSource Render(IntPtr hBmp, int w, int h, IntPtr hwnd,
                                           IntPtr hdcMem, IntPtr hdcScreen, Native.RECT r, out string how)
        {
            // ① 先试 PrintWindow：**只画这个窗口** ⇒ 天然不含遮挡物、不含她自己。
            string prefix = "";
            bool ok = false;
            try { ok = Native.PrintWindow(hwnd, hdcMem, Native.PW_RENDERFULLCONTENT); } catch { ok = false; }
            if (ok)
            {
                var s = ToSource(hBmp, w, h);
                // ⚠ why 必须先初始化：s == null 时短路求值不会调 IsDegenerateOf，
                //   而 C# 认为 out 参数在此路径上未赋值（首跑就报 CS0165）。
                string why = "空图";
                if (s != null && !IsDegenerateOf(s, out why)) { how = "PrintWindow"; return s; }
                prefix = "PrintWindow 画出来的不可用（" + why + "）→ ";
            }
            else prefix = "PrintWindow 返回 false → ";

            // ② 回退：直接从屏幕拷贝。**会含遮挡物**（她自己、别的置顶窗口），记进 how。
            // ⚠⚠ 现在这条回退多了一层风险：截的窗口**多半不在前台**（我们改成了截「上一个真实窗口」），
            //   那么屏幕拷贝拿到的可能是**压在上面的别的窗口** —— 内容对不上，而且看不出来。
            //   所以这一条要把「不在前台」写进 how，由气泡转告用户（宁可说「可能被挡住」，也不假装读准了）。
            if (!Native.BitBlt(hdcMem, 0, 0, w, h, hdcScreen, r.Left, r.Top, Native.SRCCOPY)) { how = prefix + "BitBlt 失败"; return null; }
            bool isFg = Native.GetForegroundWindow() == hwnd;
            how = prefix + (isFg ? "BitBlt(含遮挡)" : "BitBlt(含遮挡·且不在前台)");
            return ToSource(hBmp, w, h);
        }

        private static BitmapSource ToSource(IntPtr hBmp, int w, int h)
        {
            try
            {
                var s = Imaging.CreateBitmapSourceFromHBitmap(hBmp, IntPtr.Zero, Int32Rect.Empty,
                                                              BitmapSizeOptions.FromEmptyOptions());
                s.Freeze();   // Freeze 后才可跨线程用（模型调用在后台线程上）
                return s;
            }
            catch { return null; }
        }

        private static bool IsDegenerateOf(BitmapSource s, out string why)
        {
            why = null;
            try
            {
                int w = s.PixelWidth, h = s.PixelHeight;
                int stride = w * 4;
                long need = (long)stride * h;
                if (need <= 0 || need > 64L * 1024 * 1024) { why = "图太大，跳过采样"; return true; }
                byte[] px = new byte[need];
                s.CopyPixels(px, stride, 0);
                return IsDegenerate(px, stride, w, h, out why);
            }
            catch (Exception ex) { why = "读像素失败：" + ex.Message; return true; }
        }

        /// <summary>
        /// **纯函数**：这幅图是不是退化的（全黑／全白／几乎单色）。
        /// 为什么要它：`PrintWindow` 对 DirectComposition 渲染的窗口**会返回 true 但画出全黑**，
        /// 只看返回值就会把黑图当成内容发出去 —— 而黑图没有任何信息，只会让她瞎说。
        /// 抽成纯函数是为了能**离线喂合成像素逼红**（真截图没法在无人时复现「Chrome 返回黑图」）。
        /// </summary>
        public static bool IsDegenerate(byte[] px, int stride, int w, int h, out string why)
        {
            why = null;
            if (px == null || w <= 0 || h <= 0 || stride < w * 4) { why = "参数不成立"; return true; }

            long total = (long)w * h;
            int step = (int)Math.Max(1L, total / 4000);      // 采样约 4000 点
            int min = 255, max = 0, n = 0;
            for (long i = 0; i < total; i += step)
            {
                int x = (int)(i % w), y = (int)(i / w);
                int o = y * stride + x * 4;                  // BGRA
                if (o + 2 >= px.Length) break;
                int lum = (px[o] * 29 + px[o + 1] * 150 + px[o + 2] * 77) >> 8;
                if (lum < min) min = lum;
                if (lum > max) max = lum;
                n++;
            }
            if (n < 16) { why = "采样点太少（" + n + "）"; return true; }

            int range = max - min;
            if (range < 8) { why = "整幅几乎是单色（亮度极差 " + range + "，min=" + min + " max=" + max + "）"; return true; }
            return false;
        }

        // ==================================================================== 发送

        /// <summary>base64 载荷。⚠ 不带 data: 前缀 —— 由调用方按通道格式拼。</summary>
        public static string Base64(Shot s)
        {
            return s == null || s.Jpeg == null ? null : Convert.ToBase64String(s.Jpeg);
        }
    }
}
