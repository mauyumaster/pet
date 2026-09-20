// 渲染层 —— 可替换的那一半。
//
// IPetRenderer 只回答三件事：①我长什么样（给我一个 Visual）②按这个姿态摆一下
// ③这个点算不算在我身上（逐像素命中）。壳完全不关心像素是怎么来的。
//
// v1 实现 = WPF 原生 3D（Viewport3D）：
//   · 零外部依赖、产物 < 1 MB、透明／置顶已实测通过；
//   · 代价是**明暗比 three.js 简化**（WPF 3D 是固定管线的逐光多趟渲染，
//     没有环境贴图、没有 PBR；每多一盏灯就多一整趟 12 万面）。
//     想要 three.js 那套观感，就再写一个 ThreeJsRenderer 挂在同一个接口上。
//
// ⚠ 每盏灯 = 一整趟渲染。所以这里只用 2 盏（环境光 ＋ 主光），不加轮廓光。
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;

namespace AzhuPet
{
    internal interface IPetRenderer : IDisposable
    {
        FrameworkElement Host { get; }
        void Resize(double wDip, double hDip);
        void ApplyPose(Pose p);
        bool HitTest(Point pDip);
        double FeetYDip { get; }        // 脚底在窗口内的纵向位置（落地判据用它）
        void SetNight(double k);        // 0 = 白天，1 = 深夜
        string Stats { get; }
        double LastHitMs { get; }
        string LastHitType { get; }
    }

    internal sealed class WpfPetRenderer : IPetRenderer
    {
        /// <summary>0 = 正常（漫反射＋贴图）；1 = 纯白（只看几何/光照）；2 = 自发光贴图（只看 UV/采样）；
        /// 3 = UV 渐变图。这是排查「贴图碎了」与「明暗不对」的判决开关。
        /// ⚠ 进程启动时给一次即可；量测要在一趟里扫完好几种，所以实例上还有个 <see cref="Mode"/>。</summary>
        public static int MatMode;

        /// <summary>拍摄层次。做差法要「分别量到只有模型 / 只有影子」，靠这个开关分三次拍。
        /// 判据不同就不能共用集合：把影子和模型混进同一次读数，均值和中位数都会偏。</summary>
        public enum LayerKind { All = 0, NoModel = 1, Nothing = 2 }

        /// <summary>光照覆盖：非空则按给定的 0..255 颜色直接用（不再随昼夜插值）。
        /// 用途是把「测出来的每通道增益」直接除进去，一次校准到中性白。</summary>
        public int[] AmbOverride, KeyOverride;

        private int _mode;
        public int Mode { get { return _mode; } set { _mode = value; BuildMaterial(); } }

        private readonly GlbModel _m;
        private readonly Canvas _root = new Canvas();
        private readonly Viewport3D _vp = new Viewport3D();
        private readonly Ellipse _shadow = new Ellipse();
        private readonly ScaleTransform _shadowScale = new ScaleTransform(1, 1);
        private readonly PerspectiveCamera _cam = new PerspectiveCamera();
        private readonly GeometryModel3D _gm = new GeometryModel3D();
        private readonly Transform3DGroup _tg = new Transform3DGroup();
        private readonly ScaleTransform3D _scale = new ScaleTransform3D();
        private readonly AxisAngleRotation3D _ax = new AxisAngleRotation3D(new Vector3D(1, 0, 0), 0);
        private readonly AxisAngleRotation3D _ay = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
        private readonly AxisAngleRotation3D _az = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
        private readonly TranslateTransform3D _lift = new TranslateTransform3D();
        // ⚠ 两盏灯就是两趟渲染：环境光给底（背光面亮度），主光给体积（受光面）。
        //   嫌暗就同时抬这两个值；别单抬主光，那只会把受光面顶到削波、背光面反而更黑。
        private readonly AmbientLight _amb = new AmbientLight(Color.FromRgb(0x8C, 0x99, 0xB2));
        private readonly DirectionalLight _key = new DirectionalLight(Color.FromRgb(0xB0, 0xB8, 0xC6), new Vector3D(0, 0, -1));

        // ⚠⚠ 昼间灯色用**中性灰**，不要偏蓝。这两盏灯的色相会直接烙进渲染结果：
        //   实测（--pix 报告）现行偏蓝灯色下，渲染的 B/R 比是 1.618，而贴图本身只有 1.468
        //   ——相当于给一个本来就蓝调的角色又盖了一层冷色，画面发闷。
        //   换成中性灰（总量不变）后，渲染 B/R = 1.469，与贴图的 1.468 重合，
        //   平均亮度 97.6 → 98.2（几乎不动），削波 3.1% → 2.4%（还降了）。
        //   结论：灯只管「有没有光、够不够」，**色相交给贴图自己**。
        private static readonly Color AmbDay = Color.FromRgb(0x98, 0x98, 0x98);
        private static readonly Color AmbNight = Color.FromRgb(0x8A, 0x72, 0x68);
        private static readonly Color KeyDay = Color.FromRgb(0xC0, 0xC0, 0xC0);
        private static readonly Color KeyNight = Color.FromRgb(0xC8, 0xA8, 0x86);

        private const double VFOV = 30.0 * Math.PI / 180.0;   // 与 pet-preview.html 的 fov 一致
        internal const double MarginY = 1.30;
        internal const double MarginX = 1.40;

        private double _w = 240, _h = 300, _dist = 2.8, _nightK;
        private readonly double _halfV = Math.Tan(VFOV / 2);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Point _cachePt; private bool _cacheHit, _cached; private double _cacheMs;

        public double LastHitMs { get; private set; }
        public string LastHitType { get; private set; }
        public double FeetYDip { get; private set; }
        public double PxPerMeter { get; private set; }
        public FrameworkElement Host { get { return _root; } }

        public WpfPetRenderer(GlbModel m)
        {
            _m = m;
            _root.Background = null;                 // 透明是硬要求：Canvas 不能有底色
            _root.Width = _w; _root.Height = _h;
            _root.ClipToBounds = false;

            _shadow.IsHitTestVisible = false;        // 影子不能被点到
            _shadow.Fill = ShadowBrush();
            _shadow.RenderTransformOrigin = new Point(0.5, 0.5);
            _shadow.RenderTransform = _shadowScale;
            _root.Children.Add(_shadow);

            _gm.Geometry = m.Mesh;
            _mode = MatMode;
            BuildMaterial();
            _gm.BackMaterial = null;                 // 与 glb 的 doubleSided 缺省（= 背面剔除）一致

            // 变换顺序 = 内层到外层：非等比伸缩 → 倾摆(Z) → 前倾(X) → 转头(Y) → 浮沉(Y)
            // 全部以原点为中心，而原点＝脚底中心（Glb.Load 已把模型落地并水平居中）
            _tg.Children.Add(_scale);
            _tg.Children.Add(new RotateTransform3D(_az));
            _tg.Children.Add(new RotateTransform3D(_ax));
            _tg.Children.Add(new RotateTransform3D(_ay));
            _tg.Children.Add(_lift);
            _gm.Transform = _tg;

            var lights = new Model3DGroup();
            lights.Children.Add(_amb);
            var kd = new Vector3D(-1.5, -2.7, -2.3);   // 与 three.js 的主光同位（方向取反）
            kd.Normalize();
            _key.Direction = kd;
            lights.Children.Add(_key);
            ApplyLights();                           // 让 --amb/--key 覆盖在首次渲染前就生效

            var vis = new ModelVisual3D();
            vis.Content = _gm;
            _vp.Children.Add(vis);
            var lv = new ModelVisual3D();
            lv.Content = lights;
            _vp.Children.Add(lv);
            _root.Children.Add(_vp);

            Resize(_w, _h);
        }

        /// <summary>按当前 Mode 重建材质。可以在运行时反复调用（量测要扫好几种对照）。</summary>
        private void BuildMaterial()
        {
            var mat = new MaterialGroup();
            // ⚠⚠ WPF 3D 贴图最贵的一个坑：TileBrush 默认 `ViewportUnits = RelativeToBoundingBox`，
            //   而 3D 里那个「包围盒」是**每个图元自己的纹理坐标包围盒** ⇒ 每个三角形都会把
            //   整张贴图摊在自己那一小块 UV 上。12 万面的模型于是变成彩色马赛克
            //   （本机实测：第一版就是这么渲染的，远看像「贴图碎了」）。
            //   正解：ViewportUnits 用 Absolute，视口钉死在绝对 UV 空间 [0,1]²。
            if (_mode == 1)
            {
                mat.Children.Add(new DiffuseMaterial(Brushes.White));   // 判决 A：只看几何与光照
            }
            else if (_mode == 2)
            {
                // 判决 B：不受光，输出 = 贴图原色。EmissiveMaterial 不参与光照，
                // 所以这里不用去动 _amb/_key（Group 里没有 Diffuse 就没有受光项）。
                mat.Children.Add(new EmissiveMaterial(TexBrush(_m.BaseColor)));
            }
            else if (_mode == 3)
            {
                mat.Children.Add(new EmissiveMaterial(TexBrush(UvDiag())));   // 判决 C：平滑=UV 对，碎=UV 没到位
            }
            else
            {
                mat.Children.Add(new DiffuseMaterial(TexBrush(_m.BaseColor)));
            }
            _gm.Material = mat;
        }

        private static ImageBrush TexBrush(ImageSource src)
        {
            var b = new ImageBrush(src);
            b.ViewportUnits = BrushMappingMode.Absolute;
            b.Viewport = new Rect(0, 0, 1, 1);
            b.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
            b.Viewbox = new Rect(0, 0, 1, 1);
            b.TileMode = TileMode.None;
            b.Stretch = Stretch.Fill;
            return b;
        }

        /// <summary>量测用的背板。⚠ 一定要做在**窗口内部**（_root.Background），不要在桌宠下面
        /// 另开一个窗口垫着：另开窗口要跟 z 序、DPI 折算、窗口矩形对齐三件事搏斗，
        /// 实测会有 5% 的面积盖不住，掩码跟着不准。做进窗口就没有坐标系可言了。</summary>
        public Brush Backdrop { set { _root.Background = value; } }

        /// <summary>分层拍摄开关：All 全画 / NoModel 只留影子 / Nothing 全隐。
        /// 做差法靠它把「模型」和「接地影」分成两个互不重叠的集合。</summary>
        public LayerKind Layer
        {
            set
            {
                // All：模型＋影；NoModel：只留影；Nothing：什么都不画（露出桌面）
                _vp.Visibility = value == LayerKind.All ? Visibility.Visible : Visibility.Hidden;
                _shadow.Visibility = value == LayerKind.Nothing ? Visibility.Hidden : Visibility.Visible;
                _root.Visibility = Visibility.Visible;   // 壳本身要一直可见，否则窗口不参与合成
            }
        }


        /// <summary>UV 渐变图：R=u、G=v。贴对了就是一片平滑渐变；碎成色块就说明 UV 没送到光栅化器。</summary>
        private static ImageSource UvDiag()
        {
            const int N = 256;
            var px = new byte[N * N * 4];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    int i = (y * N + x) * 4;
                    px[i + 0] = 0;
                    px[i + 1] = (byte)(y * 255 / (N - 1));
                    px[i + 2] = (byte)(x * 255 / (N - 1));
                    px[i + 3] = 255;
                }
            var bmp = new WriteableBitmap(N, N, 96, 96, PixelFormats.Bgra32, null);
            bmp.WritePixels(new Int32Rect(0, 0, N, N), px, N * 4, 0);
            bmp.Freeze();
            return bmp;
        }

        private static Brush ShadowBrush()
        {
            var g = new RadialGradientBrush();
            g.MappingMode = BrushMappingMode.RelativeToBoundingBox;
            g.Center = new Point(0.5, 0.5);
            g.RadiusX = 0.5; g.RadiusY = 0.5;
            g.GradientStops.Add(new GradientStop(Color.FromArgb(122, 20, 28, 52), 0.00));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(66, 20, 28, 52), 0.42));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(0, 20, 28, 52), 1.00));
            return g;
        }

        public void Resize(double w, double h)
        {
            _w = Math.Max(40, w); _h = Math.Max(40, h);
            _root.Width = _w; _root.Height = _h;
            _vp.Width = _w; _vp.Height = _h;
            Canvas.SetLeft(_vp, 0); Canvas.SetTop(_vp, 0);

            double aspect = _w / _h;
            double mh = _m.Height;
            // 与预览页同一套取景公式，保证「模型占视野高度 ≈ 77%」两边一致
            double distV = (mh * MarginY / 2) / _halfV;
            double distH = (_m.Width * MarginX / 2) / (_halfV * aspect);
            _dist = Math.Max(distV, distH);
            double lookY = mh * 0.5;

            _cam.FieldOfView = 2 * Math.Atan(_halfV * aspect) * 180 / Math.PI;   // WPF 的 FOV 是水平角
            _cam.Position = new Point3D(0, lookY + mh * 0.045, _dist);
            _cam.LookDirection = new Vector3D(0, lookY - _cam.Position.Y, -_dist);
            _cam.UpDirection = new Vector3D(0, 1, 0);
            _cam.NearPlaneDistance = Math.Max(0.05, _dist * 0.05);
            _cam.FarPlaneDistance = _dist * 4;
            _vp.Camera = _cam;

            // 脚底在窗口内的纵向位置：把世界点 (0,0,0) 投到屏幕
            // ⚠ 不重算这个，影子就会和脚脱节（窗口一改大小就露馅）
            double vHalf = _dist * _halfV;
            PxPerMeter = _h / (2 * vHalf);                 // 屏幕 DIP / 米
            FeetYDip = _h / 2 + (_cam.Position.Y / vHalf) * (_h / 2);

            double shW = 0.46 * _h, shH = 0.13 * _h;
            _shadow.Width = shW; _shadow.Height = shH;
            Canvas.SetLeft(_shadow, _w / 2 - shW / 2);
            Canvas.SetTop(_shadow, FeetYDip - shH / 2);
        }

        public void ApplyPose(Pose p)
        {
            _scale.ScaleX = p.ScaleX; _scale.ScaleY = p.ScaleY; _scale.ScaleZ = p.ScaleZ;
            _az.Angle = p.Roll * 180 / Math.PI;
            _ax.Angle = p.Pitch * 180 / Math.PI;
            _ay.Angle = p.Yaw * 180 / Math.PI;
            // 世界坐标 → 窗口 DIP：vHalf 是相机处可视半高（米）
            _lift.OffsetY = p.Lift;
            _lift.OffsetX = _lift.OffsetZ = 0;

            // 影子：浮起来时略小略淡（与预览页同一条规则）
            double lift = Math.Max(0, p.Lift) / 0.055;
            double sc = 1 - lift * 0.14;
            _shadowScale.ScaleX = sc; _shadowScale.ScaleY = sc;
            _shadow.Opacity = 0.9 - lift * 0.32;
        }

        public bool HitTest(Point pDip)
        {
            // ① 视锥内的屏幕包围盒先粗筛 —— 绝大多数查询（光标在窗口空白处）在这里就返回了
            if (!InSilhouetteBox(pDip)) { LastHitMs = 0; LastHitType = "outside-box"; Cache(pDip, false); return false; }
            // ② 同一位置 80 ms 内的重复询问直接复用（WM_NCHITTEST 会为一次鼠标移动连发多条）
            if (_cached && Math.Abs(pDip.X - _cachePt.X) <= 1.5 && Math.Abs(pDip.Y - _cachePt.Y) <= 1.5
                && _clock.Elapsed.TotalMilliseconds - _cacheMs < 80)
            {
                LastHitMs = 0; LastHitType = "cache"; return _cacheHit;
            }
            var sw = Stopwatch.StartNew();
            HitTestResult r = VisualTreeHelper.HitTest(_vp, pDip);
            sw.Stop();
            LastHitMs = sw.Elapsed.TotalMilliseconds;
            LastHitType = r == null ? "null" : r.GetType().Name;
            // 只有真的打到网格才算命中。落空时 WPF 可能仍返回一个「打到了 Viewport3D」的
            // 2D 结果，那不是我们要的 —— 判据收窄到 RayHitTestResult。
            bool hit = r is RayHitTestResult;
            Cache(pDip, hit);
            return hit;
        }

        private void Cache(Point p, bool hit)
        {
            _cachePt = p; _cacheHit = hit; _cached = true;
            _cacheMs = _clock.Elapsed.TotalMilliseconds;
        }

        /// <summary>模型包围盒的 8 个角投到屏幕上的矩形（略放margin，覆盖姿态摆动）。</summary>
        private bool InSilhouetteBox(Point p)
        {
            double m = _m.Height * 0.13;
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (double x in new[] { _m.Min.X - m, _m.Max.X + m })
                foreach (double y in new[] { _m.Min.Y - m, _m.Max.Y + m })
                    foreach (double z in new[] { _m.Min.Z - m, _m.Max.Z + m })
                    {
                        Point s = Project(x, y, z);
                        if (s.X < minX) minX = s.X;
                        if (s.X > maxX) maxX = s.X;
                        if (s.Y < minY) minY = s.Y;
                        if (s.Y > maxY) maxY = s.Y;
                    }
            return p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY;
        }

        /// <summary>世界坐标 → 窗口 DIP。忽略相机那 1.1° 的俯角（误差远小于上面那圈 margin）。</summary>
        private Point Project(double x, double y, double z)
        {
            double depth = _dist - z;
            if (depth < 0.05) depth = 0.05;
            double vHalf = depth * _halfV;
            return new Point(_w / 2 + (x / (vHalf * (_w / _h))) * (_w / 2),
                             _h / 2 - (y - _cam.Position.Y) / vHalf * (_h / 2));
        }

        public void SetNight(double k)
        {
            if (k < 0) k = 0; if (k > 1) k = 1;
            _nightK = k;
            ApplyLights();
        }

        /// <summary>把「昼／夜插值」和「外部覆盖」合成一次，写到两盏灯上。
        /// 覆盖存在的意义：量测算出每通道光照增益后，直接把这盏灯的颜色除以增益，
        /// 一次就把「偏暗 + 偏蓝」校准到「贴图原色」——因为 WPF 的照明对灯色是线性的。</summary>
        private void ApplyLights()
        {
            Color a = Lerp(AmbDay, AmbNight, _nightK);
            Color d = Lerp(KeyDay, KeyNight, _nightK);
            if (AmbOverride != null && AmbOverride.Length >= 3)
                a = Color.FromRgb(Cl8(AmbOverride[0]), Cl8(AmbOverride[1]), Cl8(AmbOverride[2]));
            if (KeyOverride != null && KeyOverride.Length >= 3)
                d = Color.FromRgb(Cl8(KeyOverride[0]), Cl8(KeyOverride[1]), Cl8(KeyOverride[2]));
            _amb.Color = a;
            _key.Color = d;
        }

        private static byte Cl8(int v) { return (byte)(v < 0 ? 0 : v > 255 ? 255 : v); }

        private static Color Lerp(Color a, Color b, double t)
        {
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        public string Stats
        {
            get
            {
                return "tris=" + _m.Triangles + " verts=" + _m.Vertices
                     + " tex=" + _m.BaseColorW + "x" + _m.BaseColorH + "(" + _m.BaseColorName + ")"
                     + " lights=2 dist=" + _dist.ToString("0.###")
                     + " amb=" + _amb.Color.R + "," + _amb.Color.G + "," + _amb.Color.B
                     + " key=" + _key.Color.R + "," + _key.Color.G + "," + _key.Color.B;
            }
        }

        public void Dispose() { }
    }
}
