// 姿态引擎 —— 与「用什么渲染器」无关的那一半。
//
// 模型没有骨骼、没有动画（单 node 静态网格），所以「活感」只能程序化给。
// 这里产出的是一组**语义化姿态量**（浮沉／偏航／倾摆／前倾／伸缩），
// 由渲染器决定怎么落到像素上：
//   WPF 渲染器  → Transform3DGroup
//   three.js 渲染器 → pivot.position / rotation / scale（参数与 pet-preview.html 完全一致）
//   预烘序列渲染器 → 查表选帧
// ⇒ 换渲染器不用动这段，换这段不用动渲染器。
//
// ⚠ 幅度必须是算术，不能靠「看着在动」：第一版呼吸 ±5.5 mm 在 560 px 画布上只有
//   4.02 px，等于没动。现在按身高 ~2% 给（±12 mm）。Extremes 把实际幅度记下来，
//   自检时一次读出来，不靠盯屏幕。
using System;

namespace AzhuPet
{
    internal enum DozeState { Awake, Dozing, Asleep }

    internal sealed class Pose
    {
        public double Lift;     // 米，+ 向上（呼吸浮沉、跳跃）
        public double Yaw;      // 弧度，绕 Y（转头）
        public double Roll;     // 弧度，绕 Z（缓摆）
        public double Pitch;    // 弧度，绕 X（打盹前倾、瞌睡点头）
        public double ScaleX = 1, ScaleY = 1, ScaleZ = 1;
    }

    internal sealed class PoseEngine
    {
        // ---- 节奏参数：与 pet-preview.html 的 CONFIG 一一对应，改一处两边都变 ----
        public double BreatheT = 3.4;
        public double SwayT = 5.7;
        public double JumpT = 0.44;          // 跳跃周期（秒）。0.62 太"飘"，压到 0.44 = 峰值速度 +41%，顶点停留也等比缩短
        public double BreatheAmp = 0.012;   // 米 ≈ 身高 1.05%
        public double SwayAmp = 0.028;      // 弧度
        public double ScaleAmp = 0.012;
        public double FollowMax = 0.50;     // 弧度：跟随模式转头上限（约 ±29°），面向用户 = 0

        // ---- 形变（squash & stretch）幅度：与 pet-preview.html CONFIG 一一对应 ----
        // ⚠ 纵向拉伸**从脚底往上长**（ScaleTransform3D 中心 = 模型原点 = 脚底），
        //   所以要拿「取景留白」当上限。模型占视野高 1/1.30 ⇒ 框顶 = 1.15 × 身高 = 1.310 m。
        //   跳跃最高点 = 身高 × (1 + JumpStretch) + JumpLift + 侧倾抬升
        //   ⇒ 0.09 / 0.060 时 1.242 + 0.060 + 0.004 = 1.306 m（余 0.004 m ≈ 1 DIP）
        //      0.11 / 0.062 就削顶了。改这三个数之前先重做这条算术 —— `--selftest` 的
        //      pose.jumpTopInFrame 行就是它的守门人（现已把侧倾项算进去，见 SelfTest）。
        //   ⚠ 「拉伸」和「浮起」抢的是**同一份**留白：想让跳跃更高，就得先砍拉伸。
        public double JumpStretch = 0.09;   // 起跳拉伸：纵向 +9%（横向收细一半，近似保体积）
        public double JumpLift = 0.060;     // 跳跃浮起峰值（米）：4.8% → 5.3% 身高
        public double LandSquash = 0.36;    // 落地挤压：纵向最深 -36%（横向鼓起一半）
        public double JumpLandBounce = 0.55;   // 点击跳跃落地时给的一次挤压强度（0..1，抛物落地满值 1.0）
        //   横向这边不用守：视野横框 = 1.40 × 宽（0.821 m）= 1.185 m，鼓起 18% 才 0.969 m，余量足。

        // ---- 状态 ----
        public bool IdleOn = true;          // 待机动作总开关（拖拽时可暂时关掉）
        public double CursorYaw;            // 由壳写入：跟随模式目标偏航（限定在 ±FollowMax）
        public bool Dragging;               // 由壳写入：是否正在拖拽（拖拽时翻滚）
        public bool Airborne;               // 由壳写入：是否处于自由落体（抛物，也翻滚）
        public double SpinDrive;            // 由壳写入：横向速度(px/s,带符号)，用于给角速度；非拖拽/飞行=0
        public double SpinAccel;            // 由壳写入：拎起时的**固定角加速度**(rad/s²,带符号)；非拖拽=0
                                            //   ⚠ 符号初值在**按下的那一刻**按「她在屏幕左/右半边」定（见 SpinDirFor）；
                                            //     拖拽途中**跨过半屏就整体反号**（见 SpinDirForHyst）。
                                            //   ⚠⚠ 换向时壳侧**只改这一个字段**、绝不碰角速度本身 ⇒ 角速度连续：
                                            //     先被原加速度刹到 0，再由反向加速度从 0 带起来。这就是「不突变」的全部机制 ——
                                            //     谁要是在换向处顺手写一句 `_spinVel = 0`（或 `= -_spinVel`），
                                            //     那一帧 Δω 会达到 A·dt 的几十倍，`--spintest` 的 I 组当场变红。
        public bool Near;                   // 光标靠近 → 呼吸变快
        public bool Turntable;              // 转台模式（调试用）

        // ⚠ 冻结 = 输出恒等姿态。这是「做差法量像素」的前提：
        //   量测要连拍好几张（空壳／只有影子／全画），只要呼吸还在动，
        //   两张图就错位，差值里混进一圈幽灵边缘，统计量跟着失真。
        public bool Freeze;

        public double IdleSeconds;          // 由壳写入：距上次键鼠输入
        public double DozeAfter = 45;       // 秒，进入打盹
        public double SleepAfter = 180;     // 秒，进入熟睡
        public double DozeK { get; private set; }   // 0..1，打盹强度（平滑过渡）
        public double Pulse { get; private set; }   // 外壳可用：交互触发的「精神一下」

        public DozeState State
        {
            get { return DozeK > 0.66 ? DozeState.Asleep : (DozeK > 0.05 ? DozeState.Dozing : DozeState.Awake); }
        }

        // ---- 极值记录（自检用）----
        public int Frames;
        public double LiftMin = double.MaxValue, LiftMax = double.MinValue;
        public double RollMin = double.MaxValue, RollMax = double.MinValue;
        public double YawMin = double.MaxValue, YawMax = double.MinValue;
        public double PitchMin = double.MaxValue, PitchMax = double.MinValue;
        public double SyMin = double.MaxValue, SyMax = double.MinValue;

        private const double TAU = Math.PI * 2;
        private const double SpinK = 0.006;     // 角速度增速系数：∫SpinDrive·SpinK·dt（**飞行**阶段用）
        private const double SpinDecay = 1.4;   // 角速度衰减率（1/秒），越小旋转越持久

        // ---- 拎起旋转（2026-09-22）：**固定角加速度** ＋ **角速度上限** ＋ **跨半屏换向** ----
        // 需求原话：「拎起桌宠时，让桌宠进行角速度迅速增加的旋转（给定一个固定的加速度）。
        //   左半边屏幕就从右往左转，右边屏幕就从左往右转」＋「也需要限定一个最大角速度」
        //   ＋（同日追加）「拎起桌宠跨越屏幕半边时，角速度不进行突变而是让加速度反向」
        //   ＋（同日追加）「让角速度上限提高」。
        // ⚠ 这三个是**实例字段**（不是 const）：自检要能构造不同参数去逼红。
        //
        // ⚠⚠ **加速度与上限是耦合的**：到顶时间 = SpinMaxVel / SpinAccelMag。
        //   只提上限会让「迅速增加」被拉长（8→12 而不动加速度 ⇒ 0.75 秒才到顶，手感反而变钝），
        //   所以 2026-09-22 提高上限时**两个一起抬**，把配比 0.5 秒原样保住。
        //   改这里的任一个之前，先重算这个比值 —— `--spintest` 的 J 组就是它的守门人。
        public double SpinAccelMag = 24.0;      // rad/s² ≈ 1375°/s²（原 16；随上限同步提高，保住 0.5 秒到顶）
        public double SpinMaxVel = 12.0;        // rad/s ≈ 688°/s ≈ 1.91 圈/秒（原 8，2026-09-22 按要求提高）
        public double SpinHystPx = 40.0;        // 跨半屏换向的**迟滞带半宽**（物理像素，与 WorkArea 同单位）
                                                //   ⇒ 中心要越过中线 40px 才换向，退回来 40px 才换回。
                                                //   ⚠ 没有它，中心贴中线时的抖动会让加速度以帧率在 ±A 之间翻 ——
                                                //     角速度仍连续（`_spinVel` 没被碰），但每帧增量正负相消，
                                                //     表现是「她贴在中线附近僵住不转」。带宽一给，抖动就被吸收。
                                                //   ⚠ 单位是**物理像素**而非 DIP：跨不同缩放的屏时，「离中线多远算翻」保持同一绝对距离。
        private double _spinVel;                // 当前翻滚角速度（弧度/秒，带符号）
                                                //   ⚠⚠ 这是**连续状态**：任何换向/交接都不许直接写它（唯一例外是回正分支的归零）。
                                                //     判据一律用 `p.Yaw` 差分反推 ω，不读这个字段（读它就等于拿实现的输出当输入）。
        private double _retFrom, _retT, _retDur, _retTargetYaw;   // 回正贝塞尔动画
        private bool _retAnim;
        private double _t, _yaw, _drift, _driftTarget, _driftTimer = 4, _jump = -1, _dozeTarget;
        private double _bounce = -1, _bounceAmp = 1;

        public void TriggerJump() { _jump = 0; _driftTimer = 0; DozeKReset(); }
        // ⚠ 顺手唤醒不是装饰：打盹时 Pitch 最大 0.09 rad，前倾会让**后脑**抬高 ≈ TopHalfDepth·sin(Pitch)，
        //   把纵向形变那点取景余量吃光（量过会削顶）。跳一下当然该醒。

        /// <summary>落地／撞墙的挤压回弹。strength 0..1（由撞击速度换算）。</summary>
        public void TriggerBounce(double strength)
        {
            _bounce = 0;
            _bounceAmp = Math.Min(1.0, Math.Max(0.25, strength));
        }

        /// <summary>叫醒：清掉打盹强度（不必等它自己降下来）。</summary>
        public void DozeKReset() { DozeK = 0; _dozeTarget = 0; }

        public Pose Step(double dt)
        {
            if (dt <= 0) dt = 1.0 / 30;
            if (Freeze) { Frames++; return new Pose(); }   // 量测模式：恒等姿态，连拍可逐像素相减
            _t += dt;

            // 打盹强度平滑过渡（不然「突然睡着」很假）
            if (IdleSeconds >= SleepAfter) _dozeTarget = 1.0;
            else if (IdleSeconds >= DozeAfter) _dozeTarget = 0.55;
            else _dozeTarget = 0.0;
            DozeK += (_dozeTarget - DozeK) * Math.Min(1, dt / 1.5);
            Pulse *= Math.Max(0, 1 - dt / 2.5);

            // 呼吸／缓摆的节奏随状态变：打盹更慢更深，光标靠近则略快
            double breatheT = BreatheT * (1 + 1.7 * DozeK) * (Near ? 0.78 : 1.0);
            double breatheAmp = BreatheAmp * (1 + 0.45 * DozeK);
            double swayAmp = SwayAmp * (1 - 0.55 * DozeK);

            // 偏航：鼠标跟随 ＋ 每 6~11 秒自己转一下（像在看别处）
            _driftTimer -= dt;
            if (_driftTimer <= 0)
            {
                _driftTimer = 6 + Random_.NextDouble() * 5;
                _driftTarget = (Random_.NextDouble() * 2 - 1) * 0.16;
            }
            _drift += (_driftTarget - _drift) * Math.Min(1, dt * 1.2);
            if (Dragging)
            {
                // 拎起：**固定角加速度**（SpinAccel，带符号）⇒ 角速度线性上升，撞到 SpinMaxVel 后**保持**。
                // ⚠ 这里**故意不读 SpinDrive**：拎起来转多快不该取决于你手甩得多快，
                //   只取决于「拎起那一刻她在屏幕哪半边」（决定方向）。手抖不该改变转速。
                // ⚠ 也**不衰减**：有 SpinMaxVel 兜着就不必靠衰减收敛；否则「迅速增加」会被衰减吃掉，
                //   永远到不了上限（旧口径的终端速度 = SpinK·SpinDrive/SpinDecay，是个速度的函数）。
                _spinVel = Math.Clamp(_spinVel + SpinAccel * dt, -SpinMaxVel, SpinMaxVel);
                _yaw += _spinVel * dt;
            }
            else if (Airborne)
            {
                // 自由落体：按横向速度(SpinDrive)持续给角速度，角速度随时间衰减
                // ⚠ 松手瞬间 _spinVel 会**沿用拖拽末刻的角速度** ⇒ 拎起来转起来的惯性自然接得上
                _spinVel += SpinK * SpinDrive * dt;
                _spinVel *= Math.Exp(-SpinDecay * dt);
                _yaw += _spinVel * dt;
            }
            else
            {
                // 回正：沿**贝塞尔曲线**（easeInOutCubic）趋近目标，缓慢回正面向用户
                _spinVel = 0;
                double target = CursorYaw + _drift;
                if (!_retAnim || Math.Abs(target - _retTargetYaw) > 0.22)
                {
                    _retTargetYaw = target; _retFrom = _yaw; _retT = 0;
                    _retDur = Math.Clamp(0.16 + 0.9 * Math.Abs(target - _yaw), 0.18, 0.55);
                    _retAnim = true;
                }
                double u = Math.Min(1, _retT / _retDur);
                _yaw = _retFrom + (target - _retFrom) * BezierEase(u);
                _retT += dt;
                if (u >= 1) { _yaw = target; _retAnim = false; }
            }

            var p = new Pose();
            double b = 0, s = 0;
            if (IdleOn)
            {
                b = Math.Sin(_t * TAU / breatheT);
                s = Math.Sin(_t * TAU / SwayT);
                p.Lift = b * breatheAmp;
                p.ScaleY = 1 + b * ScaleAmp;
                p.ScaleX = p.ScaleZ = 1 - b * ScaleAmp * 0.4;
                p.Roll = s * swayAmp;
            }
            // 打盹：身体前倾 ＋ 慢点头
            p.Pitch = 0.060 * DozeK + Math.Sin(_t * TAU / (breatheT * 2.4)) * 0.030 * DozeK;
            if (Turntable) _yaw += dt * 0.9;
            p.Yaw = _yaw;

            // 点击跳跃：靠 sin 包络做「起—落」，落地压一下。
            // ⚠ 点击跳跃原本**没有落地形变**（只有起跳抻长），首尾不对称就显得"轻飘飘"；
            //   这里在包络跑完的那一帧补一次 TriggerBounce（挤压是变矮，不占取景额度，白赚）。
            if (_jump >= 0)
            {
                _jump += dt;
                double q = _jump / JumpT;
                if (q >= 1) { _jump = -1; TriggerBounce(JumpLandBounce); }
                else
                {
                    double hgo = Math.Sin(q * Math.PI);
                    p.Lift += hgo * JumpLift;
                    p.ScaleY *= 1 + hgo * JumpStretch;
                    p.ScaleX *= 1 - hgo * JumpStretch * 0.5;
                    p.ScaleZ *= 1 - hgo * JumpStretch * 0.5;
                }
            }

            // 落地挤压回弹（抛物落地的「软着陆」全靠这个包络）
            if (_bounce >= 0)
            {
                _bounce += dt;
                double q = _bounce / 0.30;
                if (q >= 1) _bounce = -1;
                else
                {
                    double e = (1 - q) * Math.Sin(q * Math.PI);
                    p.ScaleY *= 1 - e * LandSquash * _bounceAmp;
                    p.ScaleX *= 1 + e * LandSquash * 0.5 * _bounceAmp;
                    p.ScaleZ *= 1 + e * LandSquash * 0.5 * _bounceAmp;
                }
            }

            Frames++;
            LiftMin = Math.Min(LiftMin, p.Lift); LiftMax = Math.Max(LiftMax, p.Lift);
            RollMin = Math.Min(RollMin, p.Roll); RollMax = Math.Max(RollMax, p.Roll);
            YawMin = Math.Min(YawMin, p.Yaw); YawMax = Math.Max(YawMax, p.Yaw);
            PitchMin = Math.Min(PitchMin, p.Pitch); PitchMax = Math.Max(PitchMax, p.Pitch);
            SyMin = Math.Min(SyMin, p.ScaleY); SyMax = Math.Max(SyMax, p.ScaleY);
            return p;
        }

        public void ResetExtremes()
        {
            Frames = 0;
            LiftMin = RollMin = YawMin = PitchMin = SyMin = double.MaxValue;
            LiftMax = RollMax = YawMax = PitchMax = SyMax = double.MinValue;
        }

        /// <summary>
        /// 拎起时的旋转方向：**左半屏 → -1（从右往左转），右半屏 → +1（从左往右转）**。
        ///
        /// ⚠ 符号约定的两条独立依据（想改符号之前必须先重验这两条，不要凭感觉翻）：
        ///   ① **跟随鼠标**：`CursorYaw = FollowMax·clamp(dx/(400·DipScale), -1, 1)`，
        ///      dx = 光标X − 她中心X ⇒ 光标在她右边时 dx&gt;0 ⇒ CursorYaw&gt;0；
        ///      而「跟随」的语义就是转头看向光标 ⇒ **yaw 为正 = 面向屏幕右侧**。
        ///   ② **渲染**：`Renderer.cs` 里 `_ay.Angle = p.Yaw·180/π`（绕 +Y 轴）。右手系下
        ///      绕 +Y 转 θ 把 (0,0,1) 送到 (sinθ, 0, cosθ) ⇒ 正角转向 +X = 屏幕右。
        /// ⇒「从左往右转」= yaw 增加 = **+1**；「从右往左转」= yaw 减小 = **-1**。
        ///
        /// 参数用三个裸 double（而不是 Rect）是为了**能被离线判据直接喂合成输入** ——
        /// 依赖真屏幕、真窗口的函数没有资格失败。
        /// ⚠ 边界：正好落在中线时判为 **+1**（右半屏）；`--spintest` 有一条专门钉这个边界。
        /// </summary>
        public static int SpinDirFor(double centerX, double left, double right)
        {
            double mid = (left + right) / 2.0;
            return centerX < mid ? -1 : +1;
        }

        /// <summary>
        /// 拖拽途中的方向判定：**带迟滞** —— 只有明确越过「中线 ± band」才换向，回到带内时沿用 currentDir。
        ///
        /// 为什么要迟滞：判据取的是**窗口中心**，它在中线附近会随手抖来回越界。无迟滞时方向以帧率翻转，
        /// 加速度在 ±A 之间高频跳 —— 角速度虽然仍连续（`_spinVel` 没被碰），但每帧的净增量正负相消，
        /// 表现是「她贴在中线附近僵住不转」。带宽一给，抖动就被吸收。
        ///
        /// ⚠ `band = 0` 时与 <see cref="SpinDirFor"/> **逐点一致**（`--spintest` 有一条专门钉这个退化等价）。
        /// ⚠ `currentDir` 不是 ±1（首次调用传 0）时，带内按中线判 —— 边界口径与 SpinDirFor 相同（正好在中线判 +1）。
        /// </summary>
        public static int SpinDirForHyst(double centerX, double left, double right, int currentDir, double band)
        {
            double mid = (left + right) / 2.0;
            double b = band > 0 ? band : 0;
            if (centerX < mid - b) return -1;                       // 明确在左 ⇒ 从右往左
            if (centerX > mid + b) return +1;                       // 明确在右 ⇒ 从左往右
            if (currentDir == -1 || currentDir == 1) return currentDir;   // 迟滞带内 ⇒ 不翻
            return centerX < mid ? -1 : +1;                         // 方向未定 ⇒ 按中点判
        }

        /// <summary>三次贝塞尔缓动（easeInOutCubic）：慢→快→慢，用于回正动画调速。</summary>
        private static double BezierEase(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        }

        internal static class Random_
        {
            private static readonly System.Random R = new System.Random(12345);
            public static double NextDouble() { return R.NextDouble(); }
        }
    }
}
