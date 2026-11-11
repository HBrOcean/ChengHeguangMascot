using System;
using System.Collections.Generic;
using System.IO;
using ChengHeguangMascot.Services;
using SkiaSharp;
using Spine;

namespace ChengHeguangMascot.Rendering;

/// <summary>
/// 加载一个 Spine Q 版角色（atlas 文本 + .skel 二进制 + 图集图），持有贴图、骨骼与动画状态。
/// 贴图用 SkiaSharp 的 SKImage 承载，供 SpineCanvasRenderer 采样。
/// </summary>
public sealed class SkeletonModel : IDisposable
{
    public Atlas Atlas { get; }
    public SkeletonData Data { get; }
    public Skeleton Skeleton { get; }
    public AnimationState State { get; }

    // 图集页 → SKImage
    private readonly Dictionary<AtlasPage, SKImage> _images = new();

    // 动画名缓存：这个属性被 UI 高频访问（设置面板每个按键行都会取一次），
    // 原来每次访问都 new 一个 List，纯浪费。
    private readonly List<string> _animationNames = new();

    private bool _disposed;

    /// <summary>模型自然包围盒（Spine 本地坐标，未缩放），用于确定绘制基准。</summary>
    public (float MinX, float MinY, float MaxX, float MaxY) Bounds { get; }

    /// <summary>
    /// 「本体」包围盒：只用 idle / channel_idle / posture / sleep 这类常驻动画的帧算出来的框。
    /// 它是 Drift 的参照中心，也是判断「一帧是不是被整体甩出去了」的基线。
    /// </summary>
    public (float MinX, float MinY, float MaxX, float MaxY) BodyBounds { get; private set; }

    /// <summary>
    /// 未做位移归一化的绝对框（所有帧的原始并集）。
    /// 它只用来当 Drift 阈值的基准（阈值 = 8% 这个宽度），不参与窗口尺寸。
    /// </summary>
    public (float MinX, float MinY, float MaxX, float MaxY) RawBounds { get; private set; }

    private readonly List<AnimFit> _fit = new();

    // ComputeBounds 的中间结果（构造函数在 anchor 算完后用它建表）
    private Dictionary<string, (float X0, float Y0, float X1, float Y1)>? _fitSetup;
    private Dictionary<string, (float X0, float Y0, float X1, float Y1)>? _fitRuntime;
    private Dictionary<string, List<(float X0, float Y0, float X1, float Y1, float BodyX)>>? _fitFrames;
    private (float X0, float Y0, float X1, float Y1) _fitBox;

    /// <summary>逐动画的拟合诊断（--fit 表格的数据源）。</summary>
    public IReadOnlyList<AnimFit> FitReport => _fit;

    /// <summary>静态框的覆盖率（有多少比例的帧完整落在静态框内）。</summary>
    public float BoundsCoverage { get; private set; }

    /// <summary>
    /// 待机时骨骼中位轴的稳态值（模型单位）。
    /// 运行时 Drift 的参照点：drift = 该值 − 当前帧的骨骼中位轴。
    /// </summary>
    public float BodyAxisCenter { get; private set; }

    /// <summary>Drift 阈值（模型单位）：未归一化绝对框宽的 8%。</summary>
    public float DriftThreshold { get; private set; }

    /// <summary>按 BodyX 算这一帧要补偿的平移量（与运行时同一公式）。</summary>
    private float DriftFor(float bodyX)
    {
        float offset = BodyAxisCenter - bodyX;
        return Math.Abs(offset) <= DriftThreshold ? 0f : offset - Math.Sign(offset) * DriftThreshold;
    }

    private static float Median(List<float> values)
    {
        if (values.Count == 0) return 0f;
        values.Sort();
        return values[values.Count / 2];
    }

    /// <summary>某只骨骼的横向身体轴（骨骼 worldX 中位数）。</summary>
    private static float BodyAxisXOf(Skeleton skel)
    {
        int n = 0;
        foreach (var b in skel.Bones) n++;
        if (n == 0) return 0f;
        var buf = new float[n];
        int i = 0;
        foreach (var b in skel.Bones) buf[i++] = b.WorldX;
        Array.Sort(buf);
        return buf[n / 2];
    }

    /// <summary>
    /// 角色锚点的 X：身体中轴（Spine 本地坐标）。
    /// 取自「头部带」顶点的 x 中位数 —— 中位数天然忽略伸出去的武器/披风。
    /// </summary>
    public float AnchorX { get; private set; }

    /// <summary>
    /// 角色锚点的 Y：脚底（Spine 本地坐标，Y 向上）。
    /// 取自待机动画全部顶点 y 的 2% 分位 —— 跳过偶尔出现的脚下光圈/剑尖插地。
    /// </summary>
    public float AnchorY { get; private set; }

    public SkeletonModel(string atlasPath, string skelPath, float defaultMix = 0.15f)
    {
        var loader = new SkiaTextureLoader(_images);
        string imagesDir = Path.GetDirectoryName(Path.GetFullPath(atlasPath)) ?? ".";

        using (var reader = new StreamReader(atlasPath))
            Atlas = new Atlas(reader, imagesDir, loader);

        using (var fs = File.OpenRead(skelPath))
            Data = new SkeletonBinary(Atlas).ReadSkeletonData(fs);

        Skeleton = new Skeleton(Data);
        var stateData = new AnimationStateData(Data) { DefaultMix = defaultMix };
        State = new AnimationState(stateData);

        foreach (var a in Data.Animations) _animationNames.Add(a.Name);

        Bounds = ComputeBounds();

        var anchor = ComputeAnchor();
        AnchorX = anchor.X;
        AnchorY = anchor.Y;

        // 诊断表要用真实的 anchor（脚底/中轴）算「峰值倍率」，所以放在 anchor 之后。
        BuildFitReport();
    }

    /// <summary>取某个区域对应的贴图（RegionAttachment / MeshAttachment 共用）。</summary>
    public SKImage? GetImage(TextureRegion region)
    {
        if (region is AtlasRegion ar && ar.page != null && _images.TryGetValue(ar.page, out var img))
            return img;
        // 兜底：任取一张
        foreach (var kv in _images) return kv.Value;
        return null;
    }

    public float PageWidth(TextureRegion region) =>
        (region is AtlasRegion ar && ar.page != null) ? ar.page.width : region.width;

    public float PageHeight(TextureRegion region) =>
        (region is AtlasRegion ar && ar.page != null) ? ar.page.height : region.height;

    /// <summary>动画名列表（用于设置面板下拉）。构造时算好，之后只读。</summary>
    public IReadOnlyList<string> AnimationNames => _animationNames;

    public bool HasAnimation(string name) => Data.FindAnimation(name) != null;

    /// <summary>动画时长（秒）；不存在返回 0。用于排除 freeze 这类零时长动画。</summary>
    public float DurationOf(string name)
    {
        var anim = Data.FindAnimation(name);
        return anim?.Duration ?? 0f;
    }

    /// <summary>track0 是否仍在播放一次性动作（尚未播完、还没回到循环待机）。</summary>
    public bool IsActionPlaying
    {
        get
        {
            var cur = State.GetCurrent(0);
            return cur != null && !cur.Loop && cur.TrackTime < cur.AnimationEnd;
        }
    }

    /// <summary>播放动画；track0 为常规动作轨。</summary>
    public void SetAnimation(int track, string name, bool loop)
    {
        if (string.IsNullOrEmpty(name) || Data.FindAnimation(name) == null) return;
        State.SetAnimation(track, name, loop);
    }

    public void AddAnimation(int track, string name, bool loop, float delay = 0f)
    {
        if (string.IsNullOrEmpty(name) || Data.FindAnimation(name) == null) return;
        State.AddAnimation(track, name, loop, delay);
    }

    /// <summary>推进一帧：时间 → 动画 → 骨骼世界变换。</summary>
    public void Update(float deltaSeconds)
    {
        State.Update(deltaSeconds);
        State.Apply(Skeleton);
        Skeleton.UpdateWorldTransform(Skeleton.Physics.None);
    }

    private (float, float, float, float) ComputeBounds()
    {
        // ────────────────────────────────────────────────────────────────
        // 采样方式：模拟「真实播放」，而不是把每个动画单独摆到 setup pose 上量。
        //
        // 为什么必须换：313701 的 unique 没有把所有骨骼关键帧化。单独按
        // MixBlend.Setup 播，得到 x∈[-283,102]（宽 385）；而运行时是从 idle
        // 用 0.15 的 mix 进入，得到的是 x∈[-1798,149]（宽 1947，中心偏 -734）。
        // 旧算法只认 385，于是 unique 有 35% 的帧被裁掉，而窗口一点不涨。
        // ────────────────────────────────────────────────────────────────
        const float dt = 1f / 30f;
        const float MixSeconds = 0.15f;

        // 每帧同时记「顶点包围盒」和「骨骼中位轴 BodyX」：
        //   包围盒决定要多大画布；BodyX 决定「骨架整体有没有被甩出去」。
        //   两者必须成对采样，否则诊断算的补偿量和运行时的补偿量不是一回事。
        var frames = new List<(float X0, float Y0, float X1, float Y1, float BodyX)>();
        var bodyFrames = new List<(float X0, float Y0, float X1, float Y1, float BodyX)>();
        var runtimeBox = new Dictionary<string, (float X0, float Y0, float X1, float Y1)>(StringComparer.Ordinal);
        var runtimeFrames = new Dictionary<string, List<(float X0, float Y0, float X1, float Y1, float BodyX)>>(StringComparer.Ordinal);

        // 临时骨骼：与正式那只完全独立，算完就丢，绝不影响调用方状态
        var probe = new Skeleton(Data);
        var probeState = new AnimationState(new AnimationStateData(Data) { DefaultMix = MixSeconds });

        string idleName = PickBodyAnimationName();

        float idleDur = 0.1f;
        var idleAnim = idleName.Length > 0 ? Data.FindAnimation(idleName) : null;
        if (idleAnim != null && idleAnim.Duration > 0) idleDur = idleAnim.Duration;
        int idleSteps = Math.Max(1, (int)Math.Ceiling(idleDur / dt));

        void Step(int count, List<(float X0, float Y0, float X1, float Y1, float BodyX)>? sink)
        {
            for (int i = 0; i < count; i++)
            {
                probeState.Update(dt);
                probeState.Apply(probe);
                probe.UpdateWorldTransform(Skeleton.Physics.None);
                var b = BoxOf(probe);
                if (b.HasValue) sink?.Add((b.Value.X0, b.Value.Y0, b.Value.X1, b.Value.Y1, BodyAxisXOf(probe)));
            }
        }

        // ① 待机一个完整循环 —— 待机本身就是常驻显示内容，必须进统计
        if (idleName.Length > 0)
        {
            probeState.ClearTracks();
            probe.SetToSetupPose();
            probeState.SetAnimation(0, idleName, true);
            var idleFrames = new List<(float X0, float Y0, float X1, float Y1, float BodyX)>();
            Step(idleSteps, idleFrames);
            frames.AddRange(idleFrames);
            bodyFrames.AddRange(idleFrames);
        }

        // ② 每个动作：先跑一个 idle 循环把骨架摆到「真实待机姿势」，
        //    再用默认 mix 混合进入动作，按 1/30 步进到动作结束（+ mix 期）
        foreach (var anim in Data.Animations)
        {
            probeState.ClearTracks();
            probe.SetToSetupPose();
            if (idleName.Length > 0)
            {
                probeState.SetAnimation(0, idleName, true);
                Step(idleSteps, null);      // 只为建立姿势，不重复计入统计
            }

            probeState.SetAnimation(0, anim.Name, false);
            float dur = anim.Duration <= 0 ? 0.1f : anim.Duration;
            int steps = (int)Math.Ceiling((dur + MixSeconds + 0.35f) / dt);

            var local = new List<(float X0, float Y0, float X1, float Y1, float BodyX)>();
            Step(steps, local);

            frames.AddRange(local);
            runtimeFrames[anim.Name] = local;
            runtimeBox[anim.Name] = Union(local);
            if (IsBodyAnimation(anim.Name)) bodyFrames.AddRange(local);
        }

        // ③ 旧算法（MixBlend.Setup 单放）—— 只为 --fit 表格里的「setup 采样宽」留一份对照
        var setupBox = new Dictionary<string, (float X0, float Y0, float X1, float Y1)>(StringComparer.Ordinal);
        foreach (var anim in Data.Animations)
        {
            float dur = anim.Duration <= 0 ? 0.1f : anim.Duration;
            var u = (X0: float.MaxValue, Y0: float.MaxValue, X1: float.MinValue, Y1: float.MinValue);
            for (int i = 0; i <= 48; i++)
            {
                probe.SetToSetupPose();
                anim.Apply(probe, 0f, dur * i / 48f, false, null, 1f, MixBlend.Setup, MixDirection.In);
                probe.UpdateWorldTransform(Skeleton.Physics.None);
                var b = BoxOf(probe);
                if (!b.HasValue) continue;
                u.X0 = Math.Min(u.X0, b.Value.X0); u.Y0 = Math.Min(u.Y0, b.Value.Y0);
                u.X1 = Math.Max(u.X1, b.Value.X1); u.Y1 = Math.Max(u.Y1, b.Value.Y1);
            }
            setupBox[anim.Name] = u.X0 <= u.X1 ? u : (0f, 0f, 0f, 0f);
        }

        // 临时骨骼用完复位；正式骨骼也恢复干净状态交给调用方
        probe.SetToSetupPose();
        Skeleton.SetToSetupPose();
        State.Apply(Skeleton);
        Skeleton.UpdateWorldTransform(Skeleton.Physics.None);

        if (frames.Count == 0) return (-100, -100, 100, 100);

        var absRaw = Union(frames);
        RawBounds = absRaw;
        BodyBounds = bodyFrames.Count > 0 ? Union(bodyFrames) : absRaw;

        // ④ 位移归一化：把「整体被甩出去」的帧平移回本体中心，再统计静态框。
        //    阈值用「绝对框」的 8%：unique 偏 734 → 归一化；die 偏 162 → 低于阈值保留原样。
        float refW = Math.Max(1e-3f, absRaw.X1 - absRaw.X0);
        float thX = refW * ViewFrame.DriftThresholdRatio;

        // 参照点 = 待机（本体）帧里骨骼中位轴的中位数。
        // 这和运行时 ViewFrame 用的 BodyAxisCenter 是同一个量，诊断与运行才对得上。
        var bodyXs = new List<float>(bodyFrames.Count);
        foreach (var f in bodyFrames) bodyXs.Add(f.BodyX);
        BodyAxisCenter = bodyXs.Count > 0 ? Median(bodyXs) : 0f;

        DriftThreshold = thX;

        var norm = new List<(float X0, float Y0, float X1, float Y1)>(frames.Count);
        foreach (var f in frames)
        {
            float dx = DriftFor(f.BodyX);
            // 只归一化 X：纵向偏移多是动作本身（die 躺下 / unique2 上浮），
            // 归一化会把静态框的高度裁掉一块；纵向交给视框扩张。
            norm.Add((f.X0 + dx, f.Y0, f.X1 + dx, f.Y1));
        }

        var absNorm = Union(norm);
        var q = QuantileBox(norm);

        // ⑤ 选静态框：分位框能否覆盖 ≥95% 的帧？
        //    「每个帧的整个包围盒都落在分位框内」才算覆盖 —— 只要有一大批帧被裁，
        //    说明 10%/90% 的裁剪砍掉的是真实内容，而不是零星特效，这时必须用绝对框。
        int inside = 0;
        foreach (var f in norm)
        {
            if (f.X0 >= q.X0 - 1e-3f && f.X1 <= q.X1 + 1e-3f &&
                f.Y0 >= q.Y0 - 1e-3f && f.Y1 <= q.Y1 + 1e-3f) inside++;
        }
        BoundsCoverage = (float)inside / norm.Count;

        var box = BoundsCoverage >= 0.95f ? q : absNorm;

        // 1.5% 呼吸余量：抗浮点误差，也让「刚好贴合」的动画（unique3 正好 1.00×）不至于因为
        // 四舍五入掉一个像素就被判成裁切。这点量在窗口上约 1~2 DIP，看不出来。
        box = Expand(box, 0.015f);

        Logger.Log($"[Bounds] 真实播放采样 {frames.Count} 帧：绝对框 "
                 + $"{absNorm.X1 - absNorm.X0:0}×{absNorm.Y1 - absNorm.Y0:0}"
                 + $"，分位框 {q.X1 - q.X0:0}×{q.Y1 - q.Y0:0}"
                 + $"，覆盖率 {BoundsCoverage:P1} → 静态框取{(BoundsCoverage >= 0.95f ? "分位框" : "绝对框")}"
                 + $"（{box.X1 - box.X0:0}×{box.Y1 - box.Y0:0}）");

        // 中间结果留给构造函数（那时 anchor 才算出来）
        _fitSetup = setupBox;
        _fitRuntime = runtimeBox;
        _fitFrames = runtimeFrames;
        _fitBox = (box.X0, box.Y0, box.X1, box.Y1);

        return box;
    }

    /// <summary>
    /// 逐动画体检表：把「旧算法量到的宽度」和「真实播放时实际需要的宽度」摆在一起。
    /// 峰值倍率 = 该动画实际需要的半宽 / 静态框半宽（>1 就说明静态框装不下）。
    /// 裁切 = 减去 Drift 之后仍然超出静态框的最大距离（模型单位）。
    /// </summary>
    private void BuildFitReport()
    {
        if (_fitSetup == null || _fitRuntime == null || _fitFrames == null) return;
        var setupBox = _fitSetup;
        var runtimeBox = _fitRuntime;
        var runtimeFrames = _fitFrames;
        var box = _fitBox;

        _fit.Clear();

        float statHalfW = Math.Max(1e-3f, Math.Max(AnchorX - box.X0, box.X1 - AnchorX));
        float statHalfH = Math.Max(1e-3f, Math.Max(AnchorY - box.Y0, box.Y1 - AnchorY));

        // 与运行时 ViewFrame 完全同一口径：参照 = 待机骨骼中位轴，阈值 = 绝对框宽的 8%
        float thX = DriftThreshold;

        foreach (var anim in Data.Animations)
        {
            if (!runtimeBox.TryGetValue(anim.Name, out var rt)) continue;
            var st = setupBox.TryGetValue(anim.Name, out var sv) ? sv : rt;

            var fit = new AnimFit
            {
                Name = anim.Name,
                SetupWidth = st.X1 - st.X0,
                RuntimeWidth = rt.X1 - rt.X0,
                CenterOffsetX = ((rt.X0 + rt.X1) - (st.X0 + st.X1)) * 0.5f,
                CenterOffsetY = ((rt.Y0 + rt.Y1) - (st.Y0 + st.Y1)) * 0.5f,
            };

            if (runtimeFrames.TryGetValue(anim.Name, out var list))
            {
                foreach (var f in list)
                {
                    float dx = DriftFor(f.BodyX);   // 与运行时一致：按骨骼中位轴补 X

                    float px0 = f.X0 + dx, px1 = f.X1 + dx;
                    float py0 = f.Y0, py1 = f.Y1;

                    fit.PeakNeedW = Math.Max(fit.PeakNeedW, Math.Max(AnchorX - px0, px1 - AnchorX));
                    fit.PeakNeedH = Math.Max(fit.PeakNeedH, Math.Max(AnchorY - py0, py1 - AnchorY));

                    float over = Math.Max(Math.Max(box.X0 - px0, px1 - box.X1),
                                          Math.Max(box.Y0 - py0, py1 - box.Y1));
                    if (over > fit.ClipUnits) fit.ClipUnits = over;

                    if (Math.Abs(dx) > 15f) fit.DriftedFrames++;
                    if (Math.Abs(dx) > fit.MaxDriftX) fit.MaxDriftX = Math.Abs(dx);
                }
                fit.FrameCount = list.Count;
            }

            fit.PeakRatioW = fit.PeakNeedW / statHalfW;
            fit.PeakRatioH = fit.PeakNeedH / statHalfH;
            _fit.Add(fit);
        }
    }

    /// <summary>待机类动画名（idle 优先，其次 posture，再退第一个）。</summary>
    private string PickBodyAnimationName()
    {
        var a = Data.FindAnimation("idle") ?? Data.FindAnimation("posture");
        if (a != null) return a.Name;
        foreach (var b in Data.Animations) return b.Name;
        return "";
    }

    /// <summary>「常驻状态」动画：它们是角色的本体，用来算本体框和 Drift 参照。</summary>
    private static bool IsBodyAnimation(string name) =>
        name is "idle" or "channel_idle" or "posture" or "sleep" or "giga_idle";

    private float[] _boneBuf = new float[256];

    /// <summary>
    /// 骨架的横向「身体轴」：所有骨骼 worldX 的中位数。
    ///
    /// 为什么不直接用顶点包围盒中心：
    ///   包围盒会被「抬手举剑 / 披风张开 / 脚下特效」拉偏。313703 的 channel_idle
    ///   里包围盒中心在 -152..-24 之间摆（跨度 132 单位），而骨骼中位数只在 -7..0 之间动
    ///   （跨度 17）。拿包围盒中心当位移参照，补偿量就会跟着四肢一起抖 —— 这正是
    ///   播放时「角色在窗口里来回蹭」的来源。骨骼中位数只反映骨架整体的平移，稳得多。
    /// </summary>
    public float BodyAxisX()
    {
        int n = 0;
        foreach (var b in Skeleton.Bones) n++;
        if (n == 0) return 0f;

        if (_boneBuf.Length < n) _boneBuf = new float[n];
        int i = 0;
        foreach (var b in Skeleton.Bones) _boneBuf[i++] = b.WorldX;

        Array.Sort(_boneBuf, 0, n);
        return _boneBuf[n / 2];
    }

    /// <summary>取一只骨骼当前所有可见附件的顶点包围盒。</summary>
    internal static (float X0, float Y0, float X1, float Y1)? BoxOf(Skeleton skel)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue;
        float x1 = float.MinValue, y1 = float.MinValue;

        foreach (var slot in skel.DrawOrder)
        {
            if (slot.Attachment is not VertexAttachment va || va.WorldVerticesLength < 2) continue;

            var verts = new float[va.WorldVerticesLength];
            va.ComputeWorldVertices(slot, verts);
            for (int i = 0; i + 1 < verts.Length; i += 2)
            {
                if (verts[i] < x0) x0 = verts[i];
                if (verts[i] > x1) x1 = verts[i];
                if (verts[i + 1] < y0) y0 = verts[i + 1];
                if (verts[i + 1] > y1) y1 = verts[i + 1];
            }
        }

        return x0 <= x1 && y0 <= y1 ? (x0, y0, x1, y1) : null;
    }

    /// <summary>四边各按宽高的比例外扩一点。</summary>
    private static (float X0, float Y0, float X1, float Y1) Expand(
        (float X0, float Y0, float X1, float Y1) b, float ratio)
    {
        float dx = (b.X1 - b.X0) * ratio;
        float dy = (b.Y1 - b.Y0) * ratio;
        return (b.X0 - dx, b.Y0 - dy, b.X1 + dx, b.Y1 + dy);
    }

    private static (float X0, float Y0, float X1, float Y1) Union(
        List<(float X0, float Y0, float X1, float Y1)> list)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue;
        float x1 = float.MinValue, y1 = float.MinValue;
        foreach (var f in list)
        {
            if (f.X0 < x0) x0 = f.X0;
            if (f.Y0 < y0) y0 = f.Y0;
            if (f.X1 > x1) x1 = f.X1;
            if (f.Y1 > y1) y1 = f.Y1;
        }
        return x0 <= x1 ? (x0, y0, x1, y1) : (0f, 0f, 0f, 0f);
    }

    private static (float X0, float Y0, float X1, float Y1) Union(
        List<(float X0, float Y0, float X1, float Y1, float BodyX)> list)
    {
        float x0 = float.MaxValue, y0 = float.MaxValue;
        float x1 = float.MinValue, y1 = float.MinValue;
        foreach (var f in list)
        {
            if (f.X0 < x0) x0 = f.X0;
            if (f.Y0 < y0) y0 = f.Y0;
            if (f.X1 > x1) x1 = f.X1;
            if (f.Y1 > y1) y1 = f.Y1;
        }
        return x0 <= x1 ? (x0, y0, x1, y1) : (0f, 0f, 0f, 0f);
    }

    /// <summary>
    /// 用「头顶 → 脚底」的轮廓算角色锚点，而不是用包围盒中心。
    ///
    /// 为什么不用包围盒中心：不同皮肤的框里，角色站的位置并不一样 ——
    /// 有的头顶留白多，有的披风/长剑把框拉宽，还有的脚下带一圈特效。
    /// 直接把包围盒中心对准窗口中心，换皮肤时脚会上下跳、中轴会左右跳。
    ///
    /// 做法：
    ///   1. 采样待机动画（idle → posture → 第一个可用）若干帧，收集全部顶点；
    ///   2. 脚底 = 所有 y 的 2% 分位（跳过零星的特效点，比直接取 min 稳）；
    ///   3. 中轴 = 「头部带」（高度上方 20%）顶点的 x 中位数，
    ///      中位数不会被单手持武器、披风拖偏；头部带点太少时退回全体中位数；
    ///   4. 最后把中轴夹在轮廓的 15%~85% 之间，防止异常模型算出离谱的值。
    /// </summary>
    private (float X, float Y) ComputeAnchor()
    {
        Animation? anim = Data.FindAnimation("idle") ?? Data.FindAnimation("posture");

        // 注意：这里不能写 Data.Animations[0]。
        // spine-csharp 的 ExposedList<T> 在这个版本不提供公开索引器，会报 CS0021。
        // 用枚举取第一个，既不依赖索引器也不依赖 Items 字段名。
        if (anim == null)
        {
            foreach (var a in Data.Animations)
            {
                anim = a;
                break;
            }
        }

        var xs = new List<float>(2048);
        var ys = new List<float>(2048);

        void Collect()
        {
            foreach (var slot in Skeleton.DrawOrder)
            {
                if (slot.Attachment is not VertexAttachment va || va.WorldVerticesLength < 2) continue;

                var verts = new float[va.WorldVerticesLength];
                va.ComputeWorldVertices(slot, verts);
                for (int i = 0; i + 1 < verts.Length; i += 2)
                {
                    xs.Add(verts[i]);
                    ys.Add(verts[i + 1]);
                }
            }
        }

        if (anim != null)
        {
            float dur = anim.Duration <= 0 ? 0.1f : anim.Duration;
            const int steps = 12;
            for (int i = 0; i < steps; i++)
            {
                anim.Apply(Skeleton, 0f, dur * i / steps, false, null, 1f, MixBlend.Setup, MixDirection.In);
                Skeleton.UpdateWorldTransform(Skeleton.Physics.None);
                Collect();
            }
        }
        else
        {
            Skeleton.SetToSetupPose();
            Skeleton.UpdateWorldTransform(Skeleton.Physics.None);
            Collect();
        }

        // 恢复干净状态
        Skeleton.SetToSetupPose();
        State.Apply(Skeleton);
        Skeleton.UpdateWorldTransform(Skeleton.Physics.None);

        if (xs.Count == 0)
            return ((Bounds.MinX + Bounds.MaxX) / 2f, Bounds.MinY);

        var sortedY = new List<float>(ys);
        sortedY.Sort();
        int loIdx = Math.Clamp((int)(sortedY.Count * 0.02f), 0, sortedY.Count - 1);
        int hiIdx = Math.Clamp((int)(sortedY.Count * 0.98f), 0, sortedY.Count - 1);
        float groundY = sortedY[loIdx];
        float topY = sortedY[hiIdx];
        float bodyH = Math.Max(1e-3f, topY - groundY);

        // 头部带：上方 20%
        float headCut = topY - bodyH * 0.20f;
        var headXs = new List<float>(256);
        for (int i = 0; i < ys.Count; i++)
            if (ys[i] >= headCut) headXs.Add(xs[i]);

        var pick = headXs.Count >= 8 ? headXs : xs;
        pick.Sort();
        float axisX = pick[pick.Count / 2];

        float spanX = Math.Max(1e-3f, Bounds.MaxX - Bounds.MinX);
        axisX = Math.Clamp(axisX, Bounds.MinX + spanX * 0.15f, Bounds.MaxX - spanX * 0.15f);

        Logger.Log($"[Anchor] 中轴 x={axisX:0.0} 脚底 y={groundY:0.0} "
                   + $"（头顶 y={topY:0.0} 身高 {bodyH:0.0}；"
                   + $"包围盒中心 x={(Bounds.MinX + Bounds.MaxX) / 2f:0.0} y={(Bounds.MinY + Bounds.MaxY) / 2f:0.0}）");

        return (axisX, groundY);
    }

    /// <summary>
    /// 包围盒自检：比较「最终框」和「每一帧的实际尺寸」。
    /// 最大单帧明显大于最终框时，说明有帧注定被裁 —— 排查"渲染超出窗口"的第一手数据。
    /// </summary>
    private static void LogBoundsDiagnosis(
        List<(float X0, float Y0, float X1, float Y1)> frames,
        float x0, float y0, float x1, float y1)
    {
        float finalW = Math.Max(1e-3f, x1 - x0);
        float finalH = Math.Max(1e-3f, y1 - y0);

        float maxFW = 0f, maxFH = 0f;
        foreach (var f in frames)
        {
            float w = f.X1 - f.X0, h = f.Y1 - f.Y0;
            if (w > maxFW) maxFW = w;
            if (h > maxFH) maxFH = h;
        }

        Logger.Log($"[Bounds] 采样 {frames.Count} 帧 → 最终框 {finalW:0}×{finalH:0}"
                 + $"，最大单帧 {maxFW:0}×{maxFH:0}");

        if (maxFW > finalW * 1.02f || maxFH > finalH * 1.02f)
        {
            Logger.Log($"[Bounds] ⚠ 存在比最终框更大的帧"
                     + $"（宽 +{(maxFW / finalW - 1) * 100:0}%，高 +{(maxFH / finalH - 1) * 100:0}%）"
                     + "，这些帧会被裁掉");
        }
    }

    /// <summary>取四边各的 10% / 90% 分位，排除大幅位移的异常帧。</summary>
    private static (float X0, float Y0, float X1, float Y1) QuantileBox(
        List<(float X0, float Y0, float X1, float Y1)> frames)
    {
        static float Pick(List<float> values, float p)
        {
            if (values.Count == 0) return 0f;
            values.Sort();
            int i = (int)Math.Round((values.Count - 1) * p, MidpointRounding.AwayFromZero);
            return values[Math.Clamp(i, 0, values.Count - 1)];
        }

        var left = new List<float>(frames.Count);
        var top = new List<float>(frames.Count);
        var right = new List<float>(frames.Count);
        var bottom = new List<float>(frames.Count);

        foreach (var f in frames)
        {
            left.Add(f.X0);
            top.Add(f.Y0);
            right.Add(f.X1);
            bottom.Add(f.Y1);
        }

        return (Pick(left, 0.10f), Pick(top, 0.10f), Pick(right, 0.90f), Pick(bottom, 0.90f));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var kv in _images) kv.Value?.Dispose();
        _images.Clear();
    }

    private sealed class SkiaTextureLoader : TextureLoader
    {
        private readonly Dictionary<AtlasPage, SKImage> _map;
        public SkiaTextureLoader(Dictionary<AtlasPage, SKImage> map) => _map = map;

        public void Load(AtlasPage page, string path)
        {
            if (!File.Exists(path)) return;

            var bytes = File.ReadAllBytes(path);

            // 统一到 RGBA8888 + Premul。不同平台/后端（Windows GPU、Avalonia 内置 libskia）
            // 默认解码格式不一致（实测 Linux 为 BGRA8888/Premul），显式指定可避免
            // alpha 通道丢失或预乘状态错乱导致的色块。
            using var data = SKData.CreateCopy(bytes);
            using var codec = SKCodec.Create(data);
            if (codec == null) throw new InvalidOperationException("贴图解码失败: " + path);

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height,
                SKColorType.Rgba8888, SKAlphaType.Premul);

            using var bmp = SKBitmap.Decode(codec, info);
            if (bmp == null) throw new InvalidOperationException("贴图解码为 RGBA8888 失败: " + path);

            // FromPixelCopy 会复制像素，不依赖 bitmap 的生命周期
            var img = SKImage.FromPixelCopy(info, bmp.GetPixels(), bmp.RowBytes);
            if (img == null) throw new InvalidOperationException("贴图创建失败: " + path);

            // 兜底：真无 alpha 的贴图会在透明区画出白/黑色块，这里明确暴露出来
            if (img.AlphaType == SKAlphaType.Opaque)
                Logger.Log($"[贴图] 警告：{Path.GetFileName(path)} 不含 alpha 通道（Opaque），透明区可能出现色块");

            Logger.Log($"[贴图] {Path.GetFileName(path)} {img.Width}x{img.Height} {img.ColorType}/{img.AlphaType}");

            page.rendererObject = img;
            _map[page] = img;
        }

        public void Unload(object texture) { (texture as SKImage)?.Dispose(); }
    }
}
