namespace ChengHeguangMascot.Rendering;

/// <summary>
/// 一个动画的拟合体检数据（--fit 表格的一行）。
/// 目的：把「框架量到的」和「运行时真正需要的」摆在一起，
/// 任何一次「角色飞出窗口」都能从这张表里直接读出原因。
/// </summary>
public sealed class AnimFit
{
    public string Name { get; set; } = "";

    /// <summary>旧算法（MixBlend.Setup 单放，49 帧）量到的宽度。</summary>
    public float SetupWidth { get; set; }

    /// <summary>真实播放（idle 一循环 → 0.15 mix 进入）时的最大帧宽。</summary>
    public float RuntimeWidth { get; set; }

    /// <summary>真实播放的帧中心相对 setup 采样中心的偏移（X）。</summary>
    public float CenterOffsetX { get; set; }

    public float CenterOffsetY { get; set; }

    /// <summary>补偿位移后，相对锚点还需要多大的半宽。</summary>
    public float PeakNeedW { get; set; }

    public float PeakNeedH { get; set; }

    /// <summary>峰值倍率 = PeakNeedW / 静态框半宽。</summary>
    public float PeakRatioW { get; set; }

    public float PeakRatioH { get; set; }

    /// <summary>减去位移补偿后，仍然超出静态框的最大距离（模型单位）。</summary>
    public float ClipUnits { get; set; }

    /// <summary>发生过位移补偿的帧数。</summary>
    public int DriftedFrames { get; set; }

    public int FrameCount { get; set; }

    /// <summary>本动画出现过的最大位移补偿量（模型单位）。</summary>
    public float MaxDriftX { get; set; }

    public float MaxDriftY { get; set; }
}
