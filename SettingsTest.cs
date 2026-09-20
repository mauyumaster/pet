// --settingstest：离线验**设置面板的版式**（不联网、不真开窗、不写库）。
//
// ⚠⚠ 为什么这条自检非有不可（第二版实测踩到）：
//   第一版面板拿肉眼一看就发现「卡片全被裁掉、右半片空白、到处是红叉」——
//   但**没有任何一条判据能发现它**。所有自检都在验逻辑（配置读写、通道路由），
//   没有一条验「控件排完了没有、有没有互相压住、宽度有没有跟上窗口」。
//   于是「版式坏了」只能靠人眼看，而人眼不看的时候它就一直是坏的。
//
// 这个文件把「版式对不对」变成可以量出来的四件事：
//   ① 每张卡片的高 ≥ 它内部所有行的下沿 + 下内边距   —— 防「内容被裁」
//   ② 同一张卡片里的行**不重叠**                    —— 防「控件互相压住」
//   ③ 卡片宽度跟随内容区宽度                        —— 防「窗口放大后右半片空白」
//   ④ 页面高度 ≥ 卡片流真实高度                     —— 防「滚动条算错，内容够不着」
//
// ⚠ 它必须能变红：`--settingstest --no-layout` 会**故意跳过重排**，
//   此时 ③ 与 ① 必须失败。跑不出红 ⇒ 这套判据在测空气。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace AzhuPet
{
    internal static class SettingsTest
    {
        /// <summary>给版式自检用的**假寄主**：只回答「配置是什么」和「生效被调了几次」，
        /// 不加载模型、不起渲染器、不碰真窗口。
        /// ⚠ 它是**只读**的：Save() 会写进它的 Cfg，但那份 Cfg 是我们的临时副本 ——
        ///   真配置一个字节都不动（否则跑一次自检就改掉用户的设置）。</summary>
        private sealed class FakeHost : ISettingsHost
        {
            private readonly PetConfig _cfg = new PetConfig();
            public int Applied, Sizes, Reloads;
            public PetConfig Cfg { get { return _cfg; } }
            public void ApplyConfig() { Applied++; }
            public void SetSize(int idx) { Sizes++; }
            public void ReloadBalanceSources() { Reloads++; }
        }

        /// <summary>量一张卡片：返回 (声明高度, 内容下沿)。</summary>
        private static Tuple<int, int> MeasureCard(Card card)
        {
            int bottom = 0;
            foreach (Control c in card.Controls)
            {
                int b = c.Top + c.Height;
                if (b > bottom) bottom = b;
            }
            return Tuple.Create(card.Height, bottom);
        }

        public static int Run(Cli o)
        {
            bool negative = o.NoLayout;
            var checks = new List<object>();
            bool ok = true;

            void Check(string name, bool pass, string detail)
            {
                if (!pass) ok = false;
                checks.Add(new Dictionary<string, object> { ["name"] = name, ["ok"] = pass, ["detail"] = detail ?? "" });
            }

            Form form = null;
            var host = new FakeHost();
            try
            {
                form = new SettingsWindow(host);

                // 给窗口一个真实尺寸（构造期 ClientSize 可能还没跟上 DPI 缩放），再排一次
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-4000, -4000);      // 排到屏幕外：只为量尺寸，不闪你眼睛
                form.Show();
                form.ClientSize = new Size(880, 620);
                form.PerformLayout();
                form.Update();

                if (!negative) InvokeRelayout(form);

                // ⓪ 窗体**真的是**我们要的尺寸
                //    ⚠⚠ 这条是从一次真实事故里加出来的（第一版）：WinForms 的 AutoScaleMode
                //      默认 Font，设完 Font 再设 ClientSize 会被按字体比例偷偷缩放 ——
                //      请求 880×620 实得 601×451，整条 Dock 链错位、满屏红叉。
                //      版式数学全对（另外 5 条全绿），但窗口根本没那么大。
                //      ⇒ 「控件排得对」不等于「窗口是对的大小」，两件事都要量。
                Check("windowSizeHonored",
                    form.ClientSize.Width >= 800 && form.ClientSize.Height >= 560,
                    "客户区 " + form.ClientSize.Width + "×" + form.ClientSize.Height
                    + "（请求 ≥800×560；明显偏小 ⇒ AutoScaleMode 把尺寸缩掉了）");

                // ⓪' 内容区必须真的有宽度（Dock 链断了的话它是 0 或极小）
                var cw = FindContentPanel(form);
                Check("contentAreaHasSize", cw != null && cw.ClientSize.Width > 400 && cw.ClientSize.Height > 200,
                    cw == null ? "找不到内容区容器" :
                    "内容区客户区 " + cw.ClientSize.Width + "×" + cw.ClientSize.Height + "（必须 >400×200）");

                var cards = FindCards(form);
                var flows = FindFlows(form);

                Check("panelsFound", cards.Count > 0,
                    "找到 " + cards.Count + " 张卡片、 " + flows.Count + " 个卡片流 —— 一条都没找到就没资格通过");

                // ⓪'' 左栏与内容区**真的各占各的地盘**（不许互相盖住）
                //    ⚠⚠ 这条是从一次真截图里加出来的，而且 `--settingstest` 当时**全绿**：
                //      Dock 的 z-order 搞错时，`Dock=Fill` 的 content 会把整个客户区吃光，
                //      左栏 `_nav` 明明有 196 宽、却被压在 content 底下**完全看不见**。
                //      所有版式判据照样过 —— 因为卡片宽度、行高、开关位置都不依赖左栏。
                //      ⇒ 必须单独量「两个区域互不重叠、且导航栏宽度没被吃成 0」。
                {
                    // ⚠ 必须取**直接挂在窗体上**的那个 Dock=Left 容器，和**它的兄弟** Fill 容器。
                    //   第一版用 FindPages 回调去猜，抓到的是嵌套在里面的「页面」——
                    //   那个的 Left 就是 0，判据于是误报重叠。
                    Panel nav = FindNav(form);
                    Panel content = null;
                    foreach (Control c in form.Controls)
                        if (c is Panel p && p.Dock == DockStyle.Fill) content = p;
                    int navW = nav != null ? nav.Width : 0;
                    bool disjoint = nav != null && content != null && nav.Right <= content.Left + 1;
                    Check("navVisibleAndDisjoint", nav != null && navW >= 150 && disjoint,
                        nav == null ? "找不到左侧导航栏容器"
                        : (navW < 150 ? "左栏宽只有 " + navW + "（被 Dock=Fill 吃掉了？必须 ≥150）"
                           : (!disjoint ? "左栏与内容区重叠：nav.Right=" + nav.Right + " > content.Left=" + content.Left
                              : "左栏 " + navW + " 宽，与内容区不重叠")));
                }

                // ① 内容不被裁：卡片高 ≥ 内容下沿 + 下内边距
                //    ⚠ 额外要求：卡片**必须真的被排过**。一个从没排过的卡片高度是默认值，
                //      「高 ≥ 内容下沿」在它身上会**因为所有行都在 0 位置而恒真** ——
                //      这正是负对照里 cardFitsContent 没红的原因（它当时在测空气）。
                int clipped = 0;
                string clippedWhich = "";
                foreach (var c in cards)
                {
                    var m = MeasureCard(c);
                    bool neverLaidOut = m.Item2 == 0 && c.Height <= 64;   // 行全在 0 且高度还是默认
                    if (neverLaidOut || m.Item1 < m.Item2 + 4)
                    {
                        clipped++;
                        if (clippedWhich.Length < 140)
                            clippedWhich += "「" + (c.Title ?? "?") + "」"
                                + (neverLaidOut ? "从未排版；" : "高 " + m.Item1 + " < 内容 " + m.Item2 + "；");
                    }
                }
                Check("cardFitsContent", clipped == 0,
                    clipped == 0 ? "全部 " + cards.Count + " 张卡片都装得下自己的行"
                                 : clipped + " 张卡片不合格：" + clippedWhich);

                // ② 行不重叠（同一卡片内按 Top 排序后不许有交叠）
                //    ⚠ 同样要求「排过」：全在 0 位置时重叠检查也会恒真。
                int overlaps = 0;
                string overlapWhich = "";
                foreach (var c in cards)
                {
                    var rows = new List<Control>();
                    foreach (Control k in c.Controls) rows.Add(k);
                    rows.Sort((a, b) => a.Top.CompareTo(b.Top));
                    bool anyPlaced = false;
                    foreach (var r in rows) if (r.Top > 0) anyPlaced = true;
                    if (rows.Count > 1 && !anyPlaced)
                    {
                        overlaps++;
                        if (overlapWhich.Length < 140) overlapWhich += "「" + (c.Title ?? "?") + "」行全在 0 位置（从未排版）；";
                        continue;
                    }
                    for (int i = 1; i < rows.Count; i++)
                        if (rows[i].Top < rows[i - 1].Bottom - 1)
                        {
                            overlaps++;
                            if (overlapWhich.Length < 140) overlapWhich += "「" + (c.Title ?? "?") + "」内两行交叠；";
                            break;
                        }
                }
                Check("rowsDoNotOverlap", overlaps == 0,
                    overlaps == 0 ? "没有行互相压住" : overlaps + " 张卡片有问题：" + overlapWhich);

                // ③ 宽度跟随内容区（缩放窗口后必须重排）
                //    做法：把窗体拉宽 200px，重排一次，看卡片是否跟着变宽。
                int w0 = cards.Count > 0 ? cards[0].Width : 0;
                form.ClientSize = new Size(form.ClientSize.Width + 200, form.ClientSize.Height);
                form.PerformLayout();
                InvokeRelayout(form);
                form.Update();
                int w1 = cards.Count > 0 ? cards[0].Width : 0;
                bool grew = w1 >= w0 + 150;
                if (negative)
                    Check("widthTracksResize", !grew,
                        "负对照：跳过一次重排后卡片宽度 " + w0 + " → " + w1 + "（必须不跟随）");
                else
                    Check("widthTracksResize", grew,
                        "拉宽 200px 后卡片宽 " + w0 + " → " + w1 + "（必须跟着变宽）");

                // ④ 卡片宽度不超出内容区（防「卡片比容器还宽 ⇒ 右边永远看不见」）
                int over = 0;
                string overWhich = "";
                foreach (var c in cards)
                {
                    if (flows.Count == 0) break;
                    // 拿**这个卡片自己所在**的卡片流的客户区宽做基准（各页的流宽度可能不同）
                    var ownerFlow = FindOwnerFlow(c, flows);
                    int limit = (ownerFlow != null ? ownerFlow.ClientSize.Width - ownerFlow.Padding.Horizontal : flows[0].ClientSize.Width);
                    if (c.Width > limit + 1)
                    {
                        over++;
                        if (overWhich.Length < 160)
                            overWhich += "「" + (c.Title ?? "?") + "」宽 " + c.Width + " > 宿主 " + limit + "；";
                    }
                }
                Check("cardFitsFlow", over == 0,
                    over == 0 ? "卡片宽度都在宿主之内" : over + " 张卡片比宿主还宽：" + overWhich);

                // ⑤ 开关行里的开关必须落在**行内**（防「开关跑到标签下面」这种错位）
                int badToggle = 0;
                string badToggleWhich = "";
                foreach (var c in cards)
                    foreach (Control r in c.Controls)
                    {
                        var tg = FindFirstToggle(r);
                        if (tg == null) continue;
                        if (tg.Right > r.Width + 1 || tg.Left < 0 || tg.Width <= 0)
                        {
                            badToggle++;
                            if (badToggleWhich.Length < 100) badToggleWhich += "「" + (c.Title ?? "?") + "」开关越界；";
                        }
                    }
                Check("toggleInsideRow", badToggle == 0,
                    badToggle == 0 ? "所有拨动开关都在自己的行内" : badToggle + " 处开关越界：" + badToggleWhich);

                // ⑥ 真的画一遍，不许抛异常、也不许画出一片空白
                //    ⚠⚠ 这条是从真实事故里加出来的：版式数学全绿，用户看到的却是满屏红叉。
                //      原因是 GDI+ 在**退化矩形**上抛 "Parameter is not valid"，
                //      而 Paint 抛异常 = 那一整块区域画不出来 = 红色大叉。
                //      ⇒ 「排得对」和「画得出」是两件事，必须分别量。
                //
                //    ⚠ 用**反射直接调 OnPaint**，不用 Control.DrawToBitmap ——
                //      后者在隐藏窗体里的子控件上会阻塞（本机实测：45 秒不返回）。
                //      我们要验的是「绘制代码会不会抛」，直接喂一个 Graphics 最准也最快。
                if (!negative)
                {
                    var onPaint = typeof(Control).GetMethod("OnPaint",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                        null, new[] { typeof(PaintEventArgs) }, null);
                    var paintables = new List<Control>();
                    foreach (var c in FindAll(form)) if (ShouldPaint(c)) paintables.Add(c);

                    Check("paintablesFound", paintables.Count > 0,
                        "找到 " + paintables.Count + " 个自绘控件（卡片／导航项／开关…）");

                    int painted = 0, blank = 0;
                    string paintErr = null, blankWhich = "";

                    // ⚠ 真正的退化场景不是「位图小」，而是**绘制代码拿到的矩形是退化矩形** ——
                    //   实测（最小复现）：`AddArc` 对任何宽或高 ≤ 0 的矩形都抛
                    //   `ArgumentException: Parameter is not valid.`，那正是屏幕上红叉的来源。
                    // ⚠⚠ 别指望靠 `control.Size = Size.Empty` 复现：WinForms 会把控件尺寸
                    //   **夹到最小值**，赋 0 拿回来仍是 22×22 —— 判据于是永远碰不到退化分支
                    //   （我在这上面白试了三轮，防护撤光了它还是绿）。
                    //   唯一可靠的办法：**直接喂退化矩形给绘制基元**。
                    string geomErr = null;
                    foreach (int wv in new[] { 0, 1, -1, 3 })
                        foreach (int hv in new[] { 0, 1, -1, 3 })
                        {
                            int rad = 8;
                            var r = new Rectangle(0, 0, wv - 1, hv - 1);
                            try
                            {
                                using (var bmp = new Bitmap(4, 4))
                                using (var g = Graphics.FromImage(bmp))
                                {
                                    SettingsTheme.FillRound(g, r, Color.Red, rad);
                                    SettingsTheme.StrokeRound(g, r, Color.Blue, rad);
                                    using (var path = SettingsTheme.RoundRect(r, rad)) { }
                                }
                            }
                            catch (Exception ex)
                            {
                                geomErr = "矩形 " + r + " ⇒ " + ex.GetType().Name + " " + ex.Message;
                                break;
                            }
                        }
                    Check("degenerateRectSafe", geomErr == null,
                        geomErr == null ? "退化矩形（宽/高 0、1、-1、3 的各种组合）都不会抛异常"
                                        : "退化矩形把绘制基元弄抛了（屏幕红叉的根因）：" + geomErr);

                    // ① 正常尺寸下按真实大小画一遍：不许抛
                    //    ⚠ 用控件自己的 Width/Height（负的取个下限，免得位图构造失败），
                    //      这样「绘制代码里除以宽高／用宽高算矩形」那类错误才会真的暴露出来。
                    foreach (var c in paintables)
                    {
                        int pw = Math.Max(60, c.Width), ph = Math.Max(30, c.Height);
                        try
                        {
                            using (var bmp = new Bitmap(pw, ph))
                            using (var g = Graphics.FromImage(bmp))
                            using (var args = new PaintEventArgs(g, new Rectangle(0, 0, pw, ph)))
                            {
                                g.Clear(Color.Magenta);
                                onPaint.Invoke(c, new object[] { args });
                                painted++;
                                // ② 画完之后不许是「一片品红」——那说明这段绘制代码什么都没干。
                                //    ⚠ 只对本来就该画东西的控件要求（NavItem 静息态现在也会填底色，见 SettingsTheme）。
                                if (IsMagenta(bmp) && blankWhich.Length < 200)
                                    blankWhich += c.GetType().Name + "(" + c.Width + "×" + c.Height + ") ";
                                if (IsMagenta(bmp)) blank++;
                            }
                        }
                        catch (Exception ex)
                        {
                            // ⚠ 反射调用会把内层异常包成 TargetInvocationException —— 剥一层再报，
                            //   否则看到的是 "TargetInvocationException" 而看不到真正的 GDI+ 消息。
                            var real = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                            paintErr = c.GetType().Name + "(" + c.Width + "×" + c.Height + ") ⇒ " + real.GetType().Name + " " + real.Message;
                            break;
                        }
                    }
                    Check("paintDoesNotThrow", paintErr == null,
                        paintErr == null ? painted + " 个自绘控件按真实尺寸画完都没抛异常" : "绘制抛异常（屏幕红叉的根因）：" + paintErr);
                    Check("paintNotBlank", paintErr != null || blank == 0,
                        paintErr != null ? "有控件画不出来，这一条无意义"
                        : (blank == 0 ? "每个自绘控件都真的画上了内容" : blank + "/" + painted + " 个控件画出来是空白：" + blankWhich));
                }
            }
            catch (Exception ex)
            {
                Check("noException", false, "构造／量测设置面板时抛异常：" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                try { if (form != null) { form.Hide(); form.Dispose(); } } catch { }
            }

            return Report(ok, checks, negative);
        }

        /// <summary>用反射调私有的 Relayout()。为什么要绕这一下：
        /// 版式重排是**内部动作**（窗体 Resize 时自动跑），自检要能单独触发它，
        /// 又不想为它开一个 public 面（多一个公开入口 = 多一处将来会分叉的地方）。</summary>
        private static void InvokeRelayout(Form form)
        {
            var mi = form.GetType().GetMethod("Relayout",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (mi != null) mi.Invoke(form, null);
            else throw new InvalidOperationException("找不到 SettingsWindow.Relayout() —— 版式判据失去着力点");
        }

        private static List<Card> FindCards(Control root)
        {
            var list = new List<Card>();
            Action<Control> walk = null;
            walk = (c) =>
            {
                foreach (Control k in c.Controls)
                {
                    if (k is Card cd) list.Add(cd);
                    if (k.HasChildren) walk(k);
                }
            };
            walk(root);
            return list;
        }

        private static List<FlowLayoutPanel> FindFlows(Control root)
        {
            var list = new List<FlowLayoutPanel>();
            Action<Control> walk = null;
            walk = (c) =>
            {
                foreach (Control k in c.Controls)
                {
                    if (k is FlowLayoutPanel f) list.Add(f);
                    if (k.HasChildren) walk(k);
                }
            };
            walk(root);
            return list;
        }

        /// <summary>所有后代控件。</summary>
        private static List<Control> FindAll(Control root)
        {
            var list = new List<Control>();
            Action<Control> walk = null;
            walk = (c) =>
            {
                foreach (Control k in c.Controls)
                {
                    list.Add(k);
                    if (k.HasChildren) walk(k);
                }
            };
            walk(root);
            return list;
        }

        /// <summary>这个控件是不是「自己会画东西」的那几种（自绘控件）。
        /// ⚠ 只收**真正会被显示**的：隐藏页里的控件还没被排过版，拿它们问「为什么画出来是空白」
        ///   是问错了问题 —— 它们本来就不该画。
        /// ⚠ 可见性要沿 Parent 链一路查（`Control.Visible` 只看自己那一层，
        ///   隐藏页里的卡片自己是 Visible=true 的）—— 第一版就是这里误报了 6 个。</summary>
        private static bool ShouldPaint(Control c)
        {
            if (c.Width <= 0 || c.Height <= 0) return false;
            Control p = c;
            while (p != null) { if (!p.Visible) return false; p = p.Parent; }
            return c is Card || c is NavItem || c is Toggle || c.GetType().Name == "SizePanel";
        }

        /// <summary>位图上是不是**几乎只剩品红底色**（＝这个控件的 OnPaint 基本没画东西）。
        /// ⚠ 阈值必须宽：页眉那种控件本来就只画两行字，大片是透明的。
        ///   卡「70% 还是品红」会把只画了两行字的页眉误判成空白（第一版就是这么误报的）。
        ///   真正的「什么都没画」是**一个像素都没落**，所以判据是「有几处不是品红」。</summary>
        private static bool IsMagenta(Bitmap bmp)
        {
            int nonMagenta = 0;
            for (int y = 2; y < bmp.Height - 2; y += 3)
                for (int x = 2; x < bmp.Width - 2; x += 3)
                {
                    var c = bmp.GetPixel(x, y);
                    if (!(c.R > 200 && c.G < 90 && c.B > 200)) nonMagenta++;
                    if (nonMagenta > 25) return false;      // 画了东西了
                }
            return true;                                    // 满屏品红 ⇒ 什么都没画
        }

        /// <summary>找出所有「页面容器」（承载卡片流的那几个 Panel）。</summary>
        private static List<Panel> FindPages(Form form)
        {
            var list = new List<Panel>();
            Action<Control> walk = null;
            walk = (c) =>
            {
                foreach (Control k in c.Controls)
                {
                    if (k is Panel p && !(k is FlowLayoutPanel) && p.AutoScroll && p.Controls.Count > 0)
                        list.Add(p);
                    if (k.HasChildren) walk(k);
                }
            };
            walk(form);
            return list;
        }

        /// <summary>位图上是不是**全一色**（＝什么都没画出来）。
        /// 采样而非全扫：全扫一张 900×620 要 55 万次 GetPixel，慢且没必要。</summary>
        private static bool IsBlank(Bitmap bmp)
        {
            if (bmp.Width < 8 || bmp.Height < 8) return true;
            Color first = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
            int diff = 0;
            for (int y = 4; y < bmp.Height - 4; y += 13)
                for (int x = 4; x < bmp.Width - 4; x += 13)
                {
                    var c = bmp.GetPixel(x, y);
                    if (Math.Abs(c.R - first.R) + Math.Abs(c.G - first.G) + Math.Abs(c.B - first.B) > 12)
                        diff++;
                }
            return diff < 3;
        }

        /// <summary>找出左侧导航栏容器（Dock=Left 的 Panel）。
        /// 判据用它确认「左栏没有被 Dock=Fill 的内容区吃掉」——
        /// 那是真截图发现的一次事故：左栏有宽度却完全不可见，所有版式判据照样全绿。</summary>
        private static Panel FindNav(Form form)
        {
            foreach (Control c in form.Controls)
                if (c is Panel p && p.Dock == DockStyle.Left) return p;
            return null;
        }

        /// <summary>找出承载页面的那个内容区容器（AutoScroll 的 Panel，且**不含**卡片流）。
        /// 判据用它来确认 Dock 链没断 —— Dock 链一断，它会是 0 宽，而卡片照样「排得对」。</summary>
        private static Panel FindContentPanel(Form form)
        {
            Panel best = null;
            Action<Control> walk = null;
            walk = (c) =>
            {
                foreach (Control k in c.Controls)
                {
                    if (k is Panel p && !(k is FlowLayoutPanel) && p.AutoScroll && p.ClientSize.Width > 0)
                        if (best == null || p.ClientSize.Width > best.ClientSize.Width) best = p;
                    if (k.HasChildren) walk(k);
                }
            };
            walk(form);
            return best;
        }

        /// <summary>找出某个卡片所属的那个卡片流（沿 Parent 往上走）。</summary>
        private static FlowLayoutPanel FindOwnerFlow(Control card, List<FlowLayoutPanel> flows)
        {
            Control p = card.Parent;
            while (p != null)
            {
                if (p is FlowLayoutPanel f) return f;
                p = p.Parent;
            }
            return null;
        }

        private static Toggle FindFirstToggle(Control root)
        {
            if (root is Toggle t) return t;
            foreach (Control k in root.Controls)
            {
                var r = FindFirstToggle(k);
                if (r != null) return r;
            }
            return null;
        }

        private static int Report(bool ok, List<object> checks, bool negative)
        {
            var report = new Dictionary<string, object> { ["ok"] = ok, ["checks"] = checks };
            string outPath = Path.Combine(Path.GetTempPath(),
                "azhu_settingstest" + (negative ? "_neg" : "") + ".json");
            try
            {
                File.WriteAllText(outPath,
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
                using (var doc = JsonDocument.Parse(File.ReadAllText(outPath, Encoding.UTF8))) { }
            }
            catch (Exception ex) { ok = false; Console.WriteLine("报告写不出去：" + ex.Message); }

            Console.WriteLine("settingstest " + (ok ? "OK" : "FAIL") + " —— " + checks.Count + " 项检查，报告 " + outPath);
            foreach (object c in checks)
            {
                var d = (Dictionary<string, object>)c;
                Console.WriteLine("  " + ((bool)d["ok"] ? "[OK]  " : "[x]   ") + d["name"] + "：" + d["detail"]);
            }
            return ok ? 0 : 1;
        }
    }
}
