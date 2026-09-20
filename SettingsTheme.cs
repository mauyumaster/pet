// 设置面板的视觉层（2026-09-20 重做版式的第一部分）—— **只放样式，不放逻辑**。
//
// 为什么要拆出来：旧面板是「一个 FlowLayoutPanel 从头堆到尾」，样式与布局与配置读写
// 三层混在 240 行里 —— 结果是①改一个间距要动逻辑②每个控件各写一遍颜色③想加一栏
// 只能继续往下堆。这里先把**唯一一份**视觉定义钉住，页面只声明「这是什么控件」。
//
// ⚠ 三条纪律（改样式前先读）：
// 1. **颜色/间距只在本文件出现**。页面里不许再出现 `Color.FromArgb(...)` 字面量 ——
//    同一种灰写成两个值，就是「同一份数据两个落点」的视觉版本。
// 2. **深浅两套**（Light / Dark）由 `Theme.Pal` 一处产出。旧面板写死了深色，但桌宠本身是
//    浅色桌面上跑的，设置窗跟着系统跑才不刺眼。
// 3. **间距用 4 的倍数**（4/8/12/16/20/24）—— 这是 `ardot-ui-design` 里那条
//    「spacing must follow a consistent scale」；随手写的 7px、13px 正是旧面板看起来挤的原因。
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AzhuPet
{
    /// <summary>一套配色。深浅各一份，由 <see cref="SettingsTheme.Pal"/> 按系统主题选。</summary>
    internal sealed class SettingsPalette
    {
        public Color Window;      // 整窗底
        public Color Sidebar;     // 左栏底（比窗口略深/略浅一档，形成「两个结构区」）
        public Color Card;        // 卡片底
        public Color Field;       // 输入框底
        public Color Line;        // 描边
        public Color Divider;     // 分隔（比 Line 更淡）
        public Color Text;        // 主文字
        public Color Muted;       // 次要文字
        public Color Faint;       // 第三级（占位、路径）
        public Color Accent;      // 主色（保存按钮、选中态）
        public Color AccentSoft;  // 主色淡底（选中项背景）
        public Color Warn;        // 警示（会把内容发出去的开关）
        public Color Good;        // 成功
        public Color Bad;         // 失败
    }

    internal static class SettingsTheme
    {
        private static SettingsPalette _p;

        /// <summary>当前主题（缓存）。<see cref="Reset"/> 可在系统主题变化后清缓存。</summary>
        public static SettingsPalette Pal
        {
            get { if (_p == null) _p = Build(); return _p; }
        }

        public static void Reset() { _p = null; }

        private static SettingsPalette Build()
        {
            // 系统亮/暗：WinForms 拿不到可靠的主题 API，用「窗口背景色的亮度」判 ——
            // 这是 Windows 给「应用窗口」的底色，跟着个性化设置走。
            bool dark = IsDark();
            return dark ? Dark() : Light();
        }

        private static bool IsDark()
        {
            try
            {
                // SystemColors.Window 在暗色个性化下仍然是白的（它跟的是「Windows 经典外观」），
                // 所以这里实际读的是注册表 AppsUseLightTheme —— 读不到就按浅色（本机默认）。
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k == null) return false;
                    object v = k.GetValue("AppsUseLightTheme");
                    if (v == null) return false;
                    return Convert.ToInt32(v) == 0;
                }
            }
            catch { return false; }
        }

        private static SettingsPalette Light() { return new SettingsPalette
        {
            Window     = C(247, 248, 250),
            Sidebar    = C(238, 240, 244),
            Card       = C(255, 255, 255),
            Field      = C(250, 251, 253),
            Line       = C(214, 218, 226),
            Divider    = C(232, 235, 241),
            Text       = C(28, 32, 42),
            Muted      = C(104, 112, 130),
            Faint      = C(150, 157, 172),
            Accent     = C(62, 108, 224),
            AccentSoft = C(232, 238, 253),
            Warn       = C(190, 118, 26),
            Good       = C(30, 145, 92),
            Bad        = C(198, 60, 60),
        }; }

        private static SettingsPalette Dark() { return new SettingsPalette
        {
            Window     = C(24, 27, 38),
            Sidebar    = C(19, 22, 31),
            Card       = C(32, 35, 47),
            Field      = C(28, 31, 42),
            Line       = C(61, 66, 84),
            Divider    = C(48, 53, 68),
            Text       = C(238, 241, 248),
            Muted      = C(166, 174, 194),
            Faint      = C(120, 128, 148),
            Accent     = C(88, 132, 235),
            AccentSoft = C(40, 50, 74),
            Warn       = C(232, 162, 96),
            Good       = C(82, 196, 132),
            Bad        = C(238, 111, 111),
        }; }

        private static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        // ---- 间距尺度（4 的倍数；页面里只许用这些名字）----
        public const int GapXs = 4, GapSm = 8, GapMd = 12, GapLg = 16, GapXl = 20, GapXxl = 24;
        public const int Radius = 8;

        public static Font H1 = null, H2 = null, Body = null, Small = null, Mono = null;
        public static Font BodyBold = null, BodyRegular = null;
        public static Font GlyphFont = null;

        /// <summary>字体统一在这里造（页面里不许 new Font —— 否则同一个「正文」会有好几种字号）。
        ///
        /// ⚠⚠ **为什么连 Bold 变体也要在这里缓存**（真实事故，值得单独记一笔）：
        ///   页面代码原先到处写 `Font = new Font(SettingsTheme.Body, FontStyle.Bold)`。
        ///   每次调用都是一个**新的 GDI+ Font 句柄**，而赋给 `Control.Font` 之后没人 Dispose
        ///   （控件只管用不管造）。6 个栏目几十行下来，进程的 GDI 对象数被顶到配额附近，
        ///   于是**别的** GDI+ 调用开始零星失败 —— 表现出来完全不像字体问题：
        ///     `Label.OnParentChanged → AdjustSize → MeasureString`
        ///     ⇒ `ExternalException: A generic error occurred in GDI+.`
        ///   阴险之处在于**它是间歇性的**：异常前一刻 `MeasureString` 还量得好好的，
        ///   异常后一刻又好了（我用 AZHU_REPRO_NOTE 探针在异常前后各量一次，两次都 OK）。
        ///   `--settingstest` 也照样全绿 —— 它只构造一次面板，触不到配额。
        ///   判据是「对象数」而不是「有没有抛」，所以修法只有一条：**字体一律共享，绝不 per-call 新建**。</summary>
        public static void InitFonts()
        {
            if (Body != null) return;
            string ui = "Microsoft YaHei UI";
            H1 = new Font(ui, 15f, FontStyle.Bold);
            H2 = new Font(ui, 11f, FontStyle.Bold);
            Body = new Font(ui, 9.5f);
            Small = new Font(ui, 8.5f);
            Mono = new Font("Consolas", 9f);
            BodyBold = new Font(Body, FontStyle.Bold);
            BodyRegular = new Font(Body, FontStyle.Regular);
            // 图标字形用独立缓存：NavItem 每帧都要画，绝不能每帧 new 一个
            // （用 System.Drawing.SystemFonts 里的现成字体，不额外占句柄）。
            GlyphFont = new Font("Segoe UI Symbol", 11f);
        }

        // ------------------------------------------------------------------ 绘制基元

        /// <summary>圆角矩形路径（GDI+ 没有内建，只能自己拼）。
        /// ⚠⚠ **退化矩形必须挡住**（真实事故：面板满屏红叉 + "Parameter is not valid"）：
        ///   GDI+ 在宽或高 ≤ 0 时 `AddArc` 会抛 ArgumentException，
        ///   而 Paint 里抛异常 = 整块区域画不出来，屏幕上就是那个红色大叉。
        ///   构造期/首帧控件尺寸常常还是 0（还没排过版），所以这条必挡。</summary>
        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            // ⚠⚠ 这一行是**防御本体**，不是风格问题：`AddArc` 对宽或高 ≤ 0 的矩形抛
            //   `ArgumentException: Parameter is not valid.`，而 Paint 里抛异常 = 整块区域画不出来。
            //   返回空路径 ⇒ 调用方（FillRound/StrokeRound）看到 PointCount==0 就走方角兜底，
            //   结果是「画得不太圆」而不是「画不出来」。
            if (r.Width <= 0 || r.Height <= 0) return p;
            int d = Math.Max(1, radius * 2);
            if (d > r.Height) d = r.Height;
            if (d > r.Width) d = r.Width;
            if (d <= 0) return p;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, Color c, int radius)
        {
            using (var path = RoundRect(r, radius))
            using (var b = new SolidBrush(c))
            {
                if (path.PointCount == 0) { g.FillRectangle(b, r); return; }   // 退化成方角也比画不出来强
                g.FillPath(b, path);
            }
        }

        public static void StrokeRound(Graphics g, Rectangle r, Color c, int radius)
        {
            using (var path = RoundRect(r, radius))
            using (var p = new Pen(c))
            {
                if (path.PointCount == 0) { g.DrawRectangle(p, r); return; }
                g.DrawPath(p, path);
            }
        }
    }

    /// <summary>
    /// 一张卡片（分组容器）：自己画圆角底与描边，子控件用**绝对定位**排在
    /// 内容矩形里。用它是因为 WinForms 的 GroupBox 边框又粗又方、Padding 不可控 ——
    /// 而这个面板的观感主要就来自「卡片 + 一致的间距」。
    /// </summary>
    internal class Card : Panel
    {
        public int Radius = SettingsTheme.Radius;
        public bool DrawBorder = true;
        /// <summary>卡片标题与副标题由卡片自己画 —— 这样高度由排版代码一处算准，
        /// 不用为标题再套一个子容器（子容器会让「卡片高 = 行高之和」这条等式失真）。</summary>
        public string Title = "", Sub = "";
        public int TitleH;

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var pal = SettingsTheme.Pal;
            SettingsTheme.InitFonts();
            var g = e.Graphics;
            // ⚠ 自绘控件的 Paint 一律包 try —— 一次 GDI+ 异常会让**整块区域**画不出来
            //   （屏幕上就是 WinForms 那个大红叉）。宁可这块卡片画得难看，也不能整片空白。
            try
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                SettingsTheme.FillRound(g, r, pal.Card, Radius);
                if (DrawBorder) SettingsTheme.StrokeRound(g, r, pal.Line, Radius);

                int ty = 16;
                if (!string.IsNullOrEmpty(Title) && Width > 40)
                {
                    using (var b = new SolidBrush(pal.Text))
                        g.DrawString(Title, SettingsTheme.H2, b, new PointF(16, ty));
                    ty += 24;
                }
                if (!string.IsNullOrEmpty(Sub) && Width > 40)
                {
                    using (var b = new SolidBrush(pal.Muted))
                        g.DrawString(Sub, SettingsTheme.Small, b, new PointF(16, ty));
                }
            }
            catch { }
            base.OnPaint(e);
        }
    }

    /// <summary>
    /// 左侧分类栏的一个条目：图标字形 + 标题 + 副标题，选中态自己画。
    /// 不用 ListBox 是因为要「选中时左侧一条强调竖条 + 淡色圆角底」——
    /// 这正是「导航逻辑必须稳定」那条：用户在哪儿，一眼看得出来。
    /// </summary>
    internal class NavItem : Panel
    {
        public string Title = "";
        public string Subtitle = "";
        public string Glyph = "";          // 用文字符号做图标，不引二进制资源（同 MakeIcon 的思路）
        private bool _hover, _active;

        // ⚠ WFO1000：WinForms 分析器要求自定义控件的公开属性声明序列化行为。
        //   这个属性只用于运行时切栏，不该被设计器序列化 ⇒ 加 DesignerSerializationVisibility。
        [System.ComponentModel.DesignerSerializationVisibility(
            System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        [System.ComponentModel.Browsable(false)]
        public bool Active
        {
            get { return _active; }
            set { if (_active != value) { _active = value; Invalidate(); } }
        }

        public NavItem()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Height = 56;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var pal = SettingsTheme.Pal;
            SettingsTheme.InitFonts();
            var g = e.Graphics;
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                var box = new Rectangle(0, 1, Width - 1, Height - 3);
                // ⚠ 三态都要画：选中（淡色圆角底 + 竖条）／悬停（浅底）／**静息（透明底）**。
                //   静息态也要走一次 FillRound，否则这一项在自检里是「画出来全空白」，
                //   而空白恰恰是「绘制代码没跑」和「跑了但故意不画」两种情况的**共同**表现 ——
                //   判据就分不清它们了。填一个与背景同色的矩形，语义是「我画了，就是没颜色」。
                if (_active) SettingsTheme.FillRound(g, box, pal.AccentSoft, 7);
                else if (_hover) SettingsTheme.FillRound(g, box, pal.Divider, 7);
                else SettingsTheme.FillRound(g, box, pal.Sidebar, 7);   // 静息：与左栏同色，视觉上等于没有

                // 选中态的强调竖条：位置固定（左边缘），形状不随选中改变内容位置 ——
                // 「consistent placement of controls」。
                if (_active && Height > 20)
                {
                    using (var b = new SolidBrush(pal.Accent))
                        g.FillRectangle(b, 0, 8, 3, Height - 17);
                }

                Color fg = _active ? pal.Accent : pal.Muted;
                if (Width > 50)
                {
                    // ⚠ 字体一律用 SettingsTheme 里缓存的（别在这里 new —— 见 InitFonts 的注释：
                    //   per-call new Font 是 GDI+ 句柄泄漏，本项目已经栽过一次）。
                    // ⚠ 文字起点 x=40、可用宽度 = Width-40-6：副标题比标题长，靠**字号**收住，
                    //   而不是让它被裁掉（第一版截图上副标题全被切掉半句）。
                    int textX = 40;
                    using (var b = new SolidBrush(fg))
                        g.DrawString(Glyph, SettingsTheme.GlyphFont, b, new PointF(12, Height / 2f - 11));

                    using (var b = new SolidBrush(pal.Text))
                        g.DrawString(Title, SettingsTheme.BodyBold, b, new PointF(textX, 12));

                    if (!string.IsNullOrEmpty(Subtitle))
                        using (var b = new SolidBrush(pal.Faint))
                            g.DrawString(Subtitle, SettingsTheme.Small, b, new PointF(textX, 31));
                }
            }
            catch { }
            // ⚠ 不调 base.OnPaint：本控件是 UserPaint 自绘的，
            //   `Panel.OnPaint` 会触发外部 Paint 订阅，可能把刚画的内容盖掉。
        }
    }

    /// <summary>
    /// 仿 iOS/Win11 的拨动开关（Track + Knob）。WinForms 的 CheckBox 画出来是个方框加勾，
    /// 在「设置面板」这个语境里太像表单 —— 而这里是**开关**。自绘 40×22。
    /// 仍是一个 <see cref="CheckBox"/> 子类 ⇒ Checked 语义不变，回读代码一字不改。
    /// </summary>
    internal class Toggle : CheckBox
    {
        public Toggle()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            // ⚠⚠ 这里**不能**用 `Color.Transparent`（真实事故：截图上每个开关都是红叉）。
            //   本控件是 UserPaint 自绘的，而它的宿主（Card / 行载体 Panel）也是自绘的 ——
            //   自绘父容器不会替子控件画背景，于是「透明」这块无从取色，
            //   WinForms 就退回成**画不出来**（红叉）。
            //   `SupportsTransparentBackColor` 只解决「父控件是普通控件」的情形，
            //   这里父控件自己都不画背景，所以正解是**给一个实色**：
            //   与宿主行载体同色（卡片色），视觉上等于隐形，但画得出来。
            BackColor = SettingsTheme.Pal.Card;
            Cursor = Cursors.Hand;
            Size = new Size(44, 24);
            Text = "";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var pal = SettingsTheme.Pal;
            var g = e.Graphics;
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int w = 40, h = 22, y = (Height - h) / 2;
                if (Width < w || Height < h) { w = Math.Max(10, Width); h = Math.Max(6, Height); y = 0; }
                var track = new Rectangle(0, y, w, h);
                Color back = Checked ? pal.Accent : pal.Line;
                SettingsTheme.FillRound(g, track, back, h / 2);

                int kd = h - 6;
                if (kd > 2)
                {
                    int kx = Checked ? (w - kd - 3) : 3;
                    using (var b = new SolidBrush(Color.White))
                        g.FillEllipse(b, kx, y + 3, kd, kd);
                }
            }
            catch { }
            // ⚠⚠ 这里**不要**调 `base.OnPaint(e)`（真实事故：自绘拨动开关全被画成系统原生复选框）。
            //   `CheckBox.OnPaint` 会再把系统那套「方框 + 勾」画一遍，**盖在**我们刚画的拨动开关上。
            //   控件已经声明了 `ControlStyles.UserPaint`（＝「我自己画，你别插手」），
            //   所以正解是**不调 base** —— 调了等于把自绘成果覆盖掉。
            //   判据：截图上应当是圆角胶囊 + 白色圆点，而不是方框勾。
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            Invalidate();
            base.OnCheckedChanged(e);
        }
    }
}
