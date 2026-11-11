using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;
using Spine;

namespace ChengHeguangMascot.Rendering;

/// <summary>
/// 桌宠渲染控件：CPU 光栅 SKSurface → 直接拷进 WriteableBitmap，交给 Avalonia 显示。
/// 不做 PNG 编解码（旧实现每帧 PNG 编码约 10~20ms，是最大的 CPU 浪费）。
/// 30fps 更新，复用 surface 与位图，避免每帧分配。
/// </summary>
public sealed class PetRenderControl : Control, IDisposable
{
    private readonly SkeletonModel _model;
    private readonly SpineCanvasRenderer _renderer = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private WriteableBitmap? _wbmp;
    private SKSurface? _surface;
    private int _surfaceW, _surfaceH;
    private double _lastSeconds;
    private double _cachedScaling = 1.0;
    private bool _disposed;

    // 视框状态机：扩张 / 延迟收缩 / 位移补偿都在 ViewFrame 里。
    // 抽出去是为了让 --fit 诊断跑同一套逻辑（纯计算，不需要窗口）。
    private readonly ViewFrame _view;

    // 算当前帧包围盒用的临时缓冲（够长就不重新分配）
    private float[] _boundsBuf = new float[256];

    // 安全边距：绘制框相对「静态包围盒」的放大比例，四周各留这么多。
    // 横向给得多一些 —— 技能动画大多是左右挥动，纵向主要是轻微起伏。
    // ⚠ 这两个值同时决定窗口尺寸和绘制比例，两边必须一致（见 RenderFrame 里的说明）。
    public const double PadX = 0.25;
    public const double PadY = 0.12;

    /// <summary>
    /// 角色「脚底线」到窗口下沿的固定距离（DIP）。
    ///
    /// 必须是常数，不能写成窗口高度的比例 —— 窗口会随动作长高，
    /// 用比例的话脚会跟着往下滑。用常数时，只要窗口底边不动，脚就绝对不动。
    /// </summary>
    public const double GroundInset = 12;

    /// <summary>桌宠缩放（DIP）。</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>是否允许渲染循环运行（窗口隐藏时应置 false，避免空跑）。</summary>
    public bool Running { get; set; } = true;

    /// <summary>上下浮动（轻微正弦漂浮，像在呼吸）。</summary>
    public bool Bobbing { get; set; }

    /// <summary>水平翻转（左右镜像）。</summary>
    public bool Flip { get; set; }

    /// <summary>
    /// 视框是否跟随动作幅度扩张。关掉则永远用静态包围盒（窗口尺寸恒定）。
    /// </summary>
    public bool DynamicView { get; set; } = true;

    public SkeletonModel Model => _model;

    public PetRenderControl(SkeletonModel model)
    {
        _model = model;
        _view = new ViewFrame(model.Bounds, model.AnchorX, model.AnchorY, model.RawBounds, model.BodyBounds,
                              model.BodyAxisCenter, model.DriftThreshold);
        // 30fps：桌面挂件足够顺，且比 60fps 省一半 CPU
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, OnFrame);
        _timer.Start();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!Running || _disposed) return;

        double now = _clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Clamp(now - _lastSeconds, 0.0, 0.1);
        _lastSeconds = now;

        _model.Update(dt);
        RenderFrame();
        InvalidateVisual();
    }

    /// <summary>
    /// 脚底到窗口下沿的距离（DIP）。
    ///
    /// 动态量：至少要装下 AnchorY 以下的那部分轮廓（Bounds.MinY..AnchorY），再留 4 DIP 缝隙。
    /// 之所以不是固定 12：有的皮肤脚底下面还有一小段影子/衣摆，固定 12 会把它们裁掉。
    /// </summary>
    public double GroundInsetDips
    {
        get
        {
            var b = _model.Bounds;
            double belowAnchor = (_model.AnchorY - b.MinY) * Scale;
            return Math.Max(GroundInset, belowAnchor + 4.0);
        }
    }

    /// <summary>
    /// 把 DIP 尺寸量化到整数物理像素。
    /// 窗口和 canvas 必须用同一个量化结果，否则两边差 1px，连续 resize 时会看到 1px 抖动。
    /// </summary>
    public static double SnapDip(double dip, double scaling)
    {
        if (scaling <= 0 || double.IsNaN(dip) || double.IsInfinity(dip)) return dip;
        return Math.Round(dip * scaling, MidpointRounding.AwayFromZero) / scaling;
    }

    /// <summary>
    /// 期望尺寸 = 视框 × 缩放 × (1 + 边距) + 脚底内缩。
    /// 这里的边距与 RenderFrame 用的是同一组常量，两边必须同步，
    /// 否则会一边贴边、一边有余量。
    /// </summary>
    public Size GetDesiredSize(double scaling)
    {
        double w = _view.HalfW * 2.0 * Scale * (1 + PadX);
        double h = _view.HalfH * 2.0 * Scale * (1 + PadY) + GroundInsetDips;
        return new Size(SnapDip(Math.Max(1.0, w), scaling),
                        SnapDip(Math.Max(1.0, h), scaling));
    }

    /// <summary>按当前渲染缩放算尺寸（没拿到 TopLevel 时按 1.0 算）。</summary>
    public Size DesiredPetSize => GetDesiredSize(_cachedScaling > 0 ? _cachedScaling : 1.0);

    /// <summary>当前视框半宽/半高（诊断用）。</summary>
    public float ViewHalfW => _view.HalfW;
    public float ViewHalfH => _view.HalfH;

    private void RenderFrame()
    {
        // 缩放系数缓存：不需要每帧去查 TopLevel
        var top = TopLevel.GetTopLevel(this);
        if (top != null) _cachedScaling = top.RenderScaling > 0 ? top.RenderScaling : 1.0;

        // 与 PetWindow 里 newPxW/newPxH 的算法保持一致（都是 Round），
        // 否则两处差 1px，连续 resize 时会看到 1px 级的抖动。
        int pw = Math.Max(1, (int)Math.Round(Bounds.Width * _cachedScaling, MidpointRounding.AwayFromZero));
        int ph = Math.Max(1, (int)Math.Round(Bounds.Height * _cachedScaling, MidpointRounding.AwayFromZero));
        if (pw < 2 || ph < 2) return; // 尚未完成布局

        if (_surface == null || _surfaceW != pw || _surfaceH != ph)
        {
            _surface?.Dispose();
            _surface = SKSurface.Create(new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul));
            _surfaceW = pw;
            _surfaceH = ph;
        }

        var surface = _surface;
        if (surface == null) return;

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // 视框跟随动作幅度（窗口尺寸由 PetWindow 根据 DesiredPetSize 同步调整）
        UpdateView();

        // 锚点 = 「身体中轴 + 脚底」，不再用包围盒中心。
        //
        // 包围盒中心会随头顶留白、披风、武器、脚下特效漂移，换皮肤时角色
        // 就会上下左右跳。中轴 + 脚底是角色自带的特征，几套皮肤基本一致，
        // 拿它当基准才能让不同皮肤站在同一个位置。
        float ax = _model.AnchorX;
        float ay = _model.AnchorY;

        canvas.Save();

        // 上下浮动：屏幕空间的正弦偏移（物理像素），与模型缩放无关
        float bobPx = Bobbing
            ? (float)Math.Sin(_clock.Elapsed.TotalSeconds * 1.8) * 5f * (float)_cachedScaling
            : 0f;

        // 脚底线钉在窗口下沿上方 GroundInsetDips 处；水平钉在窗口正中
        float groundPx = ph - (float)(GroundInsetDips * _cachedScaling) + bobPx;
        canvas.Translate(pw * 0.5f, groundPx);

        // 绘制比例只由「用户缩放 × 屏幕 DPI」决定，与窗口尺寸彻底解耦。
        //
        // 这里刻意**不做**「窗口装不下就缩小」的钳制：那个钳制会在窗口追上的那一帧
        // 把角色猛地缩一下再弹回，正是之前看到的"振动"。窗口尺寸最多晚一帧（33ms），
        // 短暂越界远比尺寸抖动更难被察觉。
        float s = (float)(Scale * _cachedScaling);

        // 水平翻转：X 方向取负
        canvas.Scale(Flip ? -s : s, -s);

        // 位移补偿：把「整体被甩出去」的帧拉回窗口中心。
        // 不加这一项，unique 会把角色画到窗口左边界外（实测偏 734 单位）。
        canvas.Translate(-(ax + _view.DriftX), -(ay + _view.DriftY));
        _renderer.Draw(canvas, _model.Skeleton, _model);
        canvas.Restore();
        canvas.Flush();

        // 零拷贝：像素直接搬进 WriteableBitmap，不做 PNG 编解码
        using var pixmap = surface.PeekPixels();
        if (pixmap == null) return;

        if (_wbmp == null || _wbmp.PixelSize.Width != pw || _wbmp.PixelSize.Height != ph)
        {
            _wbmp?.Dispose();
            _wbmp = new WriteableBitmap(new PixelSize(pw, ph), new Vector(96, 96),
                                        PixelFormat.Rgba8888, AlphaFormat.Premul);
        }

        unsafe
        {
            using var fb = _wbmp.Lock();
            byte* dst = (byte*)fb.Address;
            byte* src = (byte*)pixmap.GetPixels();
            int srcRow = pixmap.RowBytes;
            int dstRow = fb.RowBytes;
            int copyLen = Math.Min(srcRow, dstRow);
            for (int y = 0; y < ph; y++)
                Buffer.MemoryCopy(src + (long)y * srcRow, dst + (long)y * dstRow, dstRow, copyLen);
        }
    }

    /// <summary>
    /// 更新视框：把当前帧的顶点包围盒交给 ViewFrame，由它处理
    /// 「位移补偿 → 需求 → 运行时累积天花板 → 扩张立即/收缩延迟」。
    ///
    /// 只用模型顶点，绝不参考窗口尺寸 —— 否则会形成
    /// 「窗口变大 → 比例变大 → 视框变大 → 窗口更大」的反馈环。
    /// </summary>
    private void UpdateView()
    {
        float now = (float)_clock.Elapsed.TotalSeconds;

        if (!TryGetFrameBounds(out var fb))
        {
            // 还没有可用顶点（首帧/资源异常）：保持静态框
            _view.Update(now, _model.BodyAxisX(), _model.Bounds.MinX, _model.Bounds.MinY,
                              _model.Bounds.MaxX, _model.Bounds.MaxY, false);
            return;
        }

        _view.Update(now, _model.BodyAxisX(), fb.MinX, fb.MinY, fb.MaxX, fb.MaxY, DynamicView);
    }

    /// <summary>把视框立刻复位到静态包围盒（关闭动态尺寸、换皮肤时用）。</summary>
    public void ResetView() => _view.Reset();

    /// <summary>
    /// 取当前帧实际顶点包围盒（Spine 本地坐标）。
    /// 动作幅度大时会比静态 Bounds 大，动态窗口就是靠它。
    /// </summary>
    private bool TryGetFrameBounds(out (float MinX, float MinY, float MaxX, float MaxY) bounds)
    {
        bounds = default;
        if (_disposed) return false;

        float x0 = float.MaxValue, y0 = float.MaxValue;
        float x1 = float.MinValue, y1 = float.MinValue;

        foreach (var slot in _model.Skeleton.DrawOrder)
        {
            if (slot.Attachment is not VertexAttachment va || va.WorldVerticesLength < 2) continue;

            int need = va.WorldVerticesLength;
            if (_boundsBuf.Length < need) _boundsBuf = new float[need];

            va.ComputeWorldVertices(slot, _boundsBuf);
            for (int i = 0; i + 1 < need; i += 2)
            {
                if (_boundsBuf[i] < x0) x0 = _boundsBuf[i];
                if (_boundsBuf[i] > x1) x1 = _boundsBuf[i];
                if (_boundsBuf[i + 1] < y0) y0 = _boundsBuf[i + 1];
                if (_boundsBuf[i + 1] > y1) y1 = _boundsBuf[i + 1];
            }
        }

        if (x0 > x1 || y0 > y1) return false;

        bounds = (x0, y0, x1, y1);
        return true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var img = (IImage?)_wbmp;
        if (img != null)
            context.DrawImage(img, new Rect(0, 0, Bounds.Width, Bounds.Height));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _renderer.Dispose();
        _surface?.Dispose();
        _wbmp?.Dispose();
    }
}
