using System;

namespace ChengHeguangMascot.Rendering;

/// <summary>
/// 视框状态机（纯计算，不依赖 Avalonia）。
///
/// 为什么单独抽出来：
///   1. 渲染控件每帧要跑它，--fit 诊断也要跑同一套逻辑，两处必须完全一致，
///      否则「诊断说不会扩」和「实际窗口在扩」会打架；
///   2. 纯 C# 才能在无窗口环境（--fit）里跑 60 秒模拟。
///
/// 关键量：
///   HalfW/HalfH —— 当前视框半宽/半高（相对锚点，向两侧对称扩张）
///   DriftX/DriftY —— 位移补偿量（模型单位），直接加在顶点上
///
/// 为什么要有 Drift：
///   313701 的 unique 在真实播放（从 idle 用 0.15 的 mix 进入）里会把骨架整体
///   甩到 x≈-1798，帧中心相对本体偏 -734 —— 这是「整体位移」，不是「动作幅度」。
///   只靠窗口变大去兜，会得到一个永远很宽的窗口；靠 Drift 把角色拉回中心，
///   窗口才能只按「本体 + 少量余量」维持。
/// </summary>
public sealed class ViewFrame
{
    // —— 参数（改这里就能调手感，--fit 读的是同一组常量）——

    /// <summary>基础余量：视框至少是静态框的 1.5 倍上限（只作为天花板的下界）。</summary>
    public const float BaseExpand = 1.5f;

    /// <summary>运行时累积需求上方留的余量。</summary>
    public const float SeenHeadroom = 1.15f;

    /// <summary>收缩延迟（秒）。扩张立即生效，收缩要等这么久，避免动作之间反复缩涨。</summary>
    public const float ShrinkDelay = 1.2f;

    /// <summary>位移补偿的平滑系数（每帧向目标值靠近 25%）。</summary>
    public const float DriftSmooth = 0.25f;

    /// <summary>
    /// 补偿量「归零」时用的系数，比增长快一倍。
    /// 动作从 unique 切回 idle 时，补偿量还挂在最大位移上；如果也按 25% 慢慢退，
    /// 角色会被残余补偿推向另一侧，窗口会先撑到两倍再缩回来（实测 125 → 271 DIP）。
    /// </summary>
    public const float DriftRelease = 0.5f;

    /// <summary>触发位移补偿的阈值：偏移超过静态框尺寸的这个比例才补偿。</summary>
    public const float DriftThresholdRatio = 0.08f;

    private readonly float _statHalfW;
    private readonly float _statHalfH;
    private readonly float _anchorX;
    private readonly float _anchorY;

    // 静态框（本体）中心：Drift 的参照点
    private readonly float _centerX;
    private readonly float _centerY;

    // 补偿阈值（静态框宽/高的 8%）
    private readonly float _driftThresholdX;
    private readonly float _driftThresholdY;

    // 运行期实际出现过的最大需求（跨动作累积；换皮肤时由新实例归零）
    private float _seenHalfW;
    private float _seenHalfH;

    private float _lastGrowTime;

    public float HalfW { get; private set; }
    public float HalfH { get; private set; }

    public float DriftX { get; private set; }
    public float DriftY { get; private set; }

    /// <summary>运行期累积到的最大半宽（诊断用）。</summary>
    public float SeenHalfW => _seenHalfW;

    /// <summary>运行期累积到的最大半高（诊断用）。</summary>
    public float SeenHalfH => _seenHalfH;

    /// <summary>当前天花板（诊断用）。</summary>
    public float CapW => Math.Max(_statHalfW * BaseExpand, _seenHalfW * SeenHeadroom);

    public float CapH => Math.Max(_statHalfH * BaseExpand, _seenHalfH * SeenHeadroom);

    /// <param name="bounds">静态包围盒（模型单位）。</param>
    /// <param name="anchorX">角色锚点 X（脚底中轴）。</param>
    /// <param name="anchorY">角色锚点 Y（脚底线）。</param>
    /// <param name="bounds">静态包围盒（可能是归一化后的框，用于窗口尺寸）。</param>
    /// <param name="anchorX">角色锚点 X（脚底中轴）。</param>
    /// <param name="anchorY">角色锚点 Y（脚底线）。</param>
    /// <param name="driftRefBounds">
    /// Drift 阈值的参照框（不传就用 bounds）。应当传「未归一化的绝对框」：
    /// 阈值取它的 8%，这样 die 那种 8~9% 的姿势偏移不会被误判成整体位移。
    /// </param>
    /// <param name="bodyBounds">
    /// 本体框（idle 等常驻动画的并集）。Drift 的参照中心取它的中心 ——
    /// 必须和 ComputeBounds 做位移归一化时用的参照点完全一致，
    /// 否则「归一化时按本体中心拉回」和「运行时按静态框中心判断」会互相打架。
    /// </param>
    public ViewFrame((float MinX, float MinY, float MaxX, float MaxY) bounds, float anchorX, float anchorY,
                     (float MinX, float MinY, float MaxX, float MaxY)? driftRefBounds = null,
                     (float MinX, float MinY, float MaxX, float MaxY)? bodyBounds = null)
    {
        _anchorX = anchorX;
        _anchorY = anchorY;

        _statHalfW = Math.Max(1e-3f, Math.Max(anchorX - bounds.MinX, bounds.MaxX - anchorX));
        _statHalfH = Math.Max(1e-3f, Math.Max(anchorY - bounds.MinY, bounds.MaxY - anchorY));

        var centerRef = bodyBounds ?? bounds;
        _centerX = (centerRef.MinX + centerRef.MaxX) * 0.5f;
        _centerY = (centerRef.MinY + centerRef.MaxY) * 0.5f;

        var refBox = driftRefBounds ?? bounds;
        _driftThresholdX = Math.Max(0f, (refBox.MaxX - refBox.MinX) * DriftThresholdRatio);
        _driftThresholdY = Math.Max(0f, (refBox.MaxY - refBox.MinY) * DriftThresholdRatio);

        Reset();
    }

    /// <summary>视框立刻回到静态框（关闭动态尺寸、换皮肤时用）。</summary>
    public void Reset()
    {
        HalfW = _statHalfW;
        HalfH = _statHalfH;
        _seenHalfW = _statHalfW;
        _seenHalfH = _statHalfH;
        DriftX = 0f;
        DriftY = 0f;
        _lastGrowTime = 0f;
    }

    /// <summary>
    /// 每帧推进：先用当前帧包围盒更新位移补偿，再按「补偿后」的位置算需求，
    /// 最后走「扩张立即 / 收缩延迟 1.2 秒一次到位」的状态机。
    /// </summary>
    /// <param name="now">单调秒数（Stopwatch）。</param>
    /// <param name="fx0">当前帧顶点包围盒（未补偿，模型单位）。</param>
    /// <param name="dynamic">是否允许视框随动作扩张。</param>
    public void Update(float now, float fx0, float fy0, float fx1, float fy1, bool dynamic)
    {
        if (!dynamic)
        {
            // 关闭动态尺寸：视框恒定、也不做位移补偿（否则角色会在固定窗口里乱跑）
            HalfW = _statHalfW;
            HalfH = _statHalfH;
            DriftX = 0f;
            DriftY = 0f;
            return;
        }

        // 1) 位移补偿：只补「整体位移」，小幅摆动不补（死区），避免角色动作本身被拉走
        float frameCx = (fx0 + fx1) * 0.5f;
        float frameCy = (fy0 + fy1) * 0.5f;

        // 只补水平方向。
        //   横移是「骨架被甩到很远」（unique 实测偏 -1706 单位），不补就一定出窗；
        //   纵向的偏移基本来自动作本身（die 躺下、born 落下），补了会把动作拉变形，
        //   而且高度方向靠视框扩张就能装下，不需要也不应该补。
        float targetDriftX = DriftTarget(_centerX - frameCx, _driftThresholdX);

        // 目标在减小（角色往中心回来）时退得快一点，避免「过冲 → 窗口反向撑大」
        float k = Math.Abs(targetDriftX) < Math.Abs(DriftX) ? DriftRelease : DriftSmooth;
        DriftX += (targetDriftX - DriftX) * k;
        DriftY = 0f;

        // 2) 补偿后的需求：补偿量直接加在顶点上，所以包围盒整体平移
        float px0 = fx0 + DriftX, px1 = fx1 + DriftX;
        float py0 = fy0, py1 = fy1;

        float needW = Math.Max(_anchorX - px0, px1 - _anchorX);
        float needH = Math.Max(_anchorY - py0, py1 - _anchorY);

        // 3) 累积累计需求，再算天花板。
        //    ⚠ 天花板绝不能取「静态包围盒的绝对框」—— 那和静态框同源同错，
        //      一旦静态框偏大，上限就等于静态框，扩张永远失效（旧版就是这么死的）。
        if (needW > _seenHalfW) _seenHalfW = needW;
        if (needH > _seenHalfH) _seenHalfH = needH;

        float targetW = Math.Clamp(needW, _statHalfW, CapW);
        float targetH = Math.Clamp(needH, _statHalfH, CapH);

        // 4) 扩张立即生效；收缩延迟 ShrinkDelay 后一次到位（不做逐帧插值）
        if (targetW > HalfW + 1e-4f) { HalfW = targetW; _lastGrowTime = now; }
        else if (now - _lastGrowTime >= ShrinkDelay && Math.Abs(HalfW - targetW) > 1e-4f) HalfW = targetW;

        if (targetH > HalfH + 1e-4f) { HalfH = targetH; _lastGrowTime = now; }
        else if (now - _lastGrowTime >= ShrinkDelay && Math.Abs(HalfH - targetH) > 1e-4f) HalfH = targetH;
    }

    /// <summary>
    /// 死区补偿：偏移没超过阈值就不动（角色正常的左右摆动），
    /// 超过阈值只补「超出部分」—— 这样 die 那种 8~9% 的小偏移不会被补成整段横移。
    /// </summary>
    private static float DriftTarget(float offset, float threshold)
    {
        float abs = Math.Abs(offset);
        if (abs <= threshold) return 0f;
        return offset - Math.Sign(offset) * threshold;
    }
}
