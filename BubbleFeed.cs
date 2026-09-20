// 气泡流 —— 桌宠「说话」与「状态读数」统一成同一条气泡。
// 设计目标（用户拍板，2026-09-20）：
//   ① 不再单槽互斥：多条气泡同屏共存，状态读数和发言依次浮现。
//   ② 视觉重做：柔色浮出卡片（发言暖、状态冷、出错红），带圆角边框与柔和投影。
//   ③ 动效：新气泡在**流底部浮现**（轻微上浮＋放大进入）→ 定格 → **上移淡出**。
// 旧实现是「一个浮窗 + 一块文本 + 状态>台词仲裁」；这里换成与宠物渲染同步推进的 Canvas 气泡栈。
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace AzhuPet
{
    // ⚠ 命名不能和 PetWindow.BubbleKind（string 属性，判据在用）撞 —— 那是实例成员，
    //   类体内裸用 `BubbleKind.Status` 会被它遮蔽成「字符串取 .Status」，编译报错。
    internal enum FeedKind { Speech, Status, Error }

    /// <summary>流里的一条气泡：持有视觉卡片 + 生命周期状态。下标顺序＝出生顺序（最旧在顶）。</summary>
    internal sealed class BubbleItem
    {
        public Border Card;
        public TextBlock Text;
        public double StartT;      // 出生时刻（含延迟）
        public double HoldSec;     // 定格时长（秒）
        public double Width, Height;
        public double Y;           // 当前 Canvas 顶距（DIP）
        public double TargetY;     // 布局算出的目标顶距
        public double Op, Scale;
        public double ExitT = -1;  // ≥0 表示已进入淡出
        public bool Done;
    }

    /// <summary>可自测量的 Canvas：装载若干条 BubbleItem，向上堆叠；由宿主每帧调 <see cref="Step"/> 推进。</summary>
    internal sealed class BubbleFeed : Canvas
    {
        public const double Gap = 6;      // 相邻气泡间距
        public const double MaxW = 300;   // 单条宽上限
        public const double Entry = 0.26; // 进入动画时长
        public const double Exit = 0.62;  // 淡出动画时长

        private readonly List<BubbleItem> _items = new List<BubbleItem>();

        public double TotalWidth, TotalHeight;
        public event Action Changed;   // 有气泡波及 / 变空时触发，供窗口显示与重定位

        public bool AnyLive
        {
            get { for (int i = 0; i < _items.Count; i++) if (!_items[i].Done) return true; return false; }
        }

        /// <summary>最近出生的一条的实际文本（她是气泡流的"当前内容"，给 BubbleVisible 判据用）。</summary>
        public string LastText
        {
            get { return _items.Count == 0 ? null : _items[_items.Count - 1].Text.Text; }
        }

        public void Push(FeedKind kind, string text, double holdSec = 3.2, double delaySec = 0, double now = 0)
        {
            var t = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = MaxW - 22,
                FontSize = 12,
                LineHeight = 15,
                Foreground = new SolidColorBrush(Fg(kind)),
            };
            var card = new Border
            {
                Background = new SolidColorBrush(Bg(kind)),
                BorderBrush = new SolidColorBrush(Edge(kind)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(9, 6, 9, 6),
                Child = t,
                UseLayoutRounding = true,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 10, ShadowDepth = 1.5, Direction = 270,
                    Opacity = 0.34, Color = Shadow(kind),
                },
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 1),
                RenderTransform = new ScaleTransform(0.9, 0.9),
            };
            Children.Add(card);
            _items.Add(new BubbleItem { Card = card, Text = t, StartT = now + delaySec, HoldSec = holdSec });
            MarkChanged();
            // 立刻量一次并显窗：即使渲染循环没跑，测试/托盘触发后窗口也要当场可见
            Step(now, 0.016);
        }

        /// <summary>推进所有气泡并重排。由宿主（桌宠渲染循环）每帧调用。</summary>
        public void Step(double now, double dt)
        {
            if (dt <= 0) dt = 0.016;
            for (int i = 0; i < _items.Count; i++) UpdateOne(_items[i], now, dt);
            double w = 0, hTotal = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.Done) continue;
                it.Card.Measure(new Size(MaxW, double.PositiveInfinity));
                it.Width = Math.Max(1, Math.Round(it.Card.DesiredSize.Width));
                it.Height = Math.Max(1, Math.Round(it.Card.DesiredSize.Height));
                if (it.Width > w) w = it.Width;
                hTotal += it.Height + Gap;
            }
            TotalWidth = Math.Max(1, w);
            TotalHeight = Math.Max(1, hTotal - Gap);

            // 布局（顶锚定）：最旧在顶，新的在底；顶部条目淡出后下方的顶上来自动补位
            double y = 0;
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                if (it.Done) { it.Card.Visibility = Visibility.Collapsed; continue; }
                it.TargetY = y;
                y += it.Height + Gap;
                // 平滑追目标（换挡/被顶上/被移除时的纵向滑动都在这一条里）
                double ease = 1 - Math.Pow(0.001, dt / 0.10);
                it.Y += (it.TargetY - it.Y) * Math.Min(1, ease);
                Apply(it);
            }
            Width = TotalWidth;
            Height = TotalHeight;
        }

        private void UpdateOne(BubbleItem it, double now, double dt)
        {
            if (it.Done) return;
            double age = now - it.StartT;
            if (age < 0) { it.Card.Visibility = Visibility.Collapsed; return; }
            it.Card.Visibility = Visibility.Visible;

            double holdEnd = it.StartT + Entry + it.HoldSec;

            if (age < Entry)   // 进入：从下方"浮现"，放大加渐显
            {
                double k = Math.Min(1, age / Entry);
                double rise = 14 * (1 - EaseOut(k));
                double ease = 1 - Math.Pow(0.001, dt / 0.18);
                it.Y += ((it.TargetY + rise) - it.Y) * Math.Min(1, ease);
                it.Op = k;
                it.Scale = 0.9 + 0.1 * EaseOut(k);
                Apply(it);
                return;
            }

            if (now >= holdEnd && it.ExitT < 0) it.ExitT = now;

            if (it.ExitT < 0)   // 定格中
            {
                it.Op = 1; it.Scale = 1;
                Apply(it);
                return;
            }

            // 淡出
            double fade = (now - it.ExitT) / Exit;
            if (fade >= 1)
            {
                it.Done = true;
                it.Card.Visibility = Visibility.Collapsed;
                Children.Remove(it.Card);
                MarkChanged();
                return;
            }
            it.Op = Math.Max(0, 1 - EaseIn(fade));
            it.Scale = 1;
            it.Y -= 8 * EaseIn(fade);   // 淡出时再略上飘
            Apply(it);
        }

        private void Apply(BubbleItem it)
        {
            it.Card.Opacity = it.Op;
            var sc = it.Card.RenderTransform as ScaleTransform;
            if (sc != null) { sc.ScaleX = it.Scale; sc.ScaleY = it.Scale; }
            Canvas.SetLeft(it.Card, 0);
            Canvas.SetTop(it.Card, it.Y);
        }

        private void MarkChanged() { if (Changed != null) Changed(); }

        private static double EaseOut(double k) { return 1 - (1 - k) * (1 - k); }
        private static double EaseIn(double k) { return k * k; }

        // ---- 视觉（重新设计：柔色浮出卡片）----
        private static Color Bg(FeedKind k)
        {
            switch (k)
            {
                case FeedKind.Speech: return Color.FromRgb(255, 246, 236);   // 暖象牙
                case FeedKind.Status: return Color.FromRgb(241, 247, 255);   // 冷浅蓝
                default:                return Color.FromRgb(255, 240, 239);   // 浅红（错误）
            }
        }
        private static Color Edge(FeedKind k)
        {
            switch (k)
            {
                case FeedKind.Speech: return Color.FromRgb(255, 201, 163);
                case FeedKind.Status: return Color.FromRgb(190, 214, 247);
                default:                return Color.FromRgb(242, 191, 186);
            }
        }
        private static Color Fg(FeedKind k)
        {
            switch (k)
            {
                case FeedKind.Speech: return Color.FromRgb(90, 58, 34);
                case FeedKind.Status: return Color.FromRgb(34, 50, 76);
                default:                return Color.FromRgb(124, 46, 40);
            }
        }
        private static Color Shadow(FeedKind k)
        {
            switch (k)
            {
                case FeedKind.Speech: return Color.FromRgb(224, 138, 74);
                case FeedKind.Status: return Color.FromRgb(127, 166, 216);
                default:                return Color.FromRgb(217, 106, 94);
            }
        }
    }
}