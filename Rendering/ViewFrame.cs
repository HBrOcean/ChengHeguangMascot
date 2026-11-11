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

    /// <summary>
    /// 位移补偿的跟随系数 —— 现在是 1（完全跟随）。
    ///
    /// 之前用 0.25 平滑，结果是「补偿追不上骨架」：unique 起手那几帧角色还在远处，
    /// need 比补偿到位时大一倍，窗口被顶起来；等补偿追上，窗口又要缩回去 ——
    /// 一次动作就是「涨→缩」的来回，肉眼看就是窗口在抖、角色在飘。
    ///
    /// 之所以现在敢直接跟随：参照已经从「顶点包围盒中心」换成了「骨骼 worldX 中位数」
    /// （见 SkeletonModel.BodyAxisX）。包围盒中心会被举剑/披风带偏，一帧能跳几十单位；
    /// 骨骼中位数在 idle 类动作里每帧只动 0.1~0.2 单位，本身就已经是平滑的，
    /// 再加平滑只会引入滞后。
    /// </summary>
    public const float DriftFollow = 1.0f;

    /// <summary>
    /// 骨骼中位轴的一阶低通系数。
    /// 中位数本身已经比包围盒中心稳得多，但个别动作里骨架会有单帧尖峰
    /// （313703 的 skill1 实测单帧跳 95 单位 ≈ 14 DIP）。先把它滤掉，
    /// 再拿去做 Drift，角色就不会瞬移。
    /// </summary>
    public const float BodySmooth = 0.35f;

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

    /// <summary>平滑后的骨骼中位轴（Drift 的输入）。</summary>
    private float _bodyXSmoothed;

    /// <summary>是否已经收到过第一帧（第一帧不做平滑，避免起手滞后把窗口顶起来）。</summary>
    private bool _bodyInited;

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
                     (float MinX, float MinY, float MaxX, float MaxY)? bodyBounds = null,
                     float? bodyCenterX = null, float? driftThresholdX = null)
    {
        _anchorX = anchorX;
        _anchorY = anchorY;

        _statHalfW = Math.Max(1e-3f, Math.Max(anchorX - bounds.MinX, bounds.MaxX - anchorX));
        _statHalfH = Math.Max(1e-3f, Math.Max(anchorY - bounds.MinY, bounds.MaxY - anchorY));

        // Drift 参照：优先用「待机骨骼中位轴」（SkeletonModel.BodyAxisCenter），
        // 这样和 ComputeBounds 做位移归一化时用的参照完全一致。
        var centerRef = bodyBounds ?? bounds;
        _centerX = bodyCenterX ?? (centerRef.MinX + centerRef.MaxX) * 0.5f;
        _centerY = (centerRef.MinY + centerRef.MaxY) * 0.5f;
        _bodyXSmoothed = _centerX;

        var refBox = driftRefBounds ?? bounds;
        _driftThresholdX = driftThresholdX ?? Math.Max(0f, (refBox.MaxX - refBox.MinX) * DriftThresholdRatio);
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
        _bodyXSmoothed = _centerX;
        _bodyInited = false;
        _lastGrowTime = 0f;
    }

    /// <summary>
    /// 每帧推进：先用当前帧包围盒更新位移补偿，再按「补偿后」的位置算需求，
    /// 最后走「扩张立即 / 收缩延迟 1.2 秒一次到位」的状态机。
    /// </summary>
    /// <param name="now">单调秒数（Stopwatch）。</param>
    /// <param name="fx0">当前帧顶点包围盒（未补偿，模型单位）。</param>
    /// <param name="dynamic">是否允许视框随动作扩张。</param>
    /// <param name="bodyX">骨架横向身体轴（骨骼 worldX 中位数），Drift 的参照；不是顶点包围盒中心。</param>
    public void Update(float now, float bodyX, float fx0, float fy0, float fx1, float fy1, bool dynamic)
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

        // 1) 位移补偿：只补「骨架整体的平移」，四肢摆动不补（死区）。
        //    参照用骨骼中位数而不是包围盒中心 —— 后者会被举剑/披风带偏，
        //    补偿量跟着抖，表现就是角色在窗口里来回蹭。
        // 先滤掉骨骼中位轴的单帧尖峰，再做死区判断。
        // 第一帧直接对齐（换皮肤/首次出帧时，从 0 慢慢爬会把窗口顶起来一次）。
        if (!_bodyInited) { _bodyXSmoothed = bodyX; _bodyInited = true; }
        else _bodyXSmoothed += (bodyX - _bodyXSmoothed) * BodySmooth;
        float targetDriftX = DriftTarget(_centerX - _bodyXSmoothed, _driftThresholdX);

        DriftX += (targetDriftX - DriftX) * DriftFollow;
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
