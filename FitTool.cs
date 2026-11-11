using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChengHeguangMascot.Config;
using ChengHeguangMascot.Rendering;
using ChengHeguangMascot.Services;
using Spine;

namespace ChengHeguangMascot;

/// <summary>
/// --fit：纯计算体检，不开窗口。
///
/// 回答两个问题：
///   1. 每个动作真实播放时到底有多宽？静态框装不装得下？装不下的是多少像素？
///   2. 待机 60 秒，窗口会不会自己动（resize 次数、尺寸极差）？
///
/// 用法：ChengHeguangMascot --fit [--all]
///   --all  三套皮肤都做 60 秒模拟（默认只模拟默认皮肤）
/// </summary>
public static class FitTool
{
    private const double SimScale = 0.15;   // 与 AppConfig.Scale 默认值一致
    private const double SimSeconds = 60.0;
    private const double SimDt = 1.0 / 30.0;

    public static void Run(bool allSkins = false)
    {
        var usable = SkinCatalog.All.Where(e => e.IsUsable && e.Dir != null).ToList();
        if (usable.Count == 0)
        {
            Console.WriteLine("[fit] 没有可用皮肤：Assets/characters 下缺少 .atlas/.skel");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("=== 动作拟合体检（Scale=" + SimScale.ToString("0.00") + "，数值为模型单位 / 括号内为 DIP） ===");

        bool first = true;
        foreach (var skin in usable)
        {
            using var model = new SkeletonModel(
                Path.Combine(skin.Dir!, skin.Id + ".atlas"),
                Path.Combine(skin.Dir!, skin.Id + ".skel"));

            PrintTable(skin.Id, skin.DisplayName, model);

            if (first || allSkins)
            {
                PrintIdleSimulation(skin.Id, model);
                if (first) PrintPlaySequence(skin.Id, model);
                first = false;
            }
        }

        Console.WriteLine();
        Console.WriteLine("[fit] 判据：unique 的运行时宽/静态框宽 ≤ 1.15 且裁切 = 0；idle 60 秒 resize = 0 且极差 = 0。");
    }

    private static void PrintTable(string id, string name, SkeletonModel model)
    {
        var b = model.Bounds;
        float boxW = b.MaxX - b.MinX;
        float boxH = b.MaxY - b.MinY;
        float halfW = Math.Max(Math.Abs(model.AnchorX - b.MinX), Math.Abs(b.MaxX - model.AnchorX));
        float halfH = Math.Max(Math.Abs(model.AnchorY - b.MinY), Math.Abs(b.MaxY - model.AnchorY));

        var view = new ViewFrame(b, model.AnchorX, model.AnchorY, model.RawBounds, model.BodyBounds, model.BodyAxisCenter, model.DriftThreshold);
        double inset = Math.Max(12.0, (model.AnchorY - b.MinY) * SimScale + 4.0);
        double winW = halfW * 2 * SimScale * (1 + PetRenderControl.PadX);
        double winH = halfH * 2 * SimScale * (1 + PetRenderControl.PadY) + inset;

        Console.WriteLine();
        Console.WriteLine($"--- {id} {name} ---");
        Console.WriteLine($"  静态框 {boxW:0}×{boxH:0}（x {b.MinX:0}..{b.MaxX:0}, y {b.MinY:0}..{b.MaxY:0}）"
                          + $"  覆盖率 {model.BoundsCoverage:P1}  anchor ({model.AnchorX:0.0}, {model.AnchorY:0.0})");
        Console.WriteLine($"  本体框 {model.BodyBounds.MaxX - model.BodyBounds.MinX:0}×{model.BodyBounds.MaxY - model.BodyBounds.MinY:0}"
                          + $"  窗口 {winW:0}×{winH:0} DIP（Scale {SimScale:0.00}，脚底内缩 {inset:0} DIP）");
        Console.WriteLine();
        Console.WriteLine("  | 动画 | setup 采样宽 | 运行时混合宽 | 中心偏移 | 静态框 | 峰值倍率 | 裁切像素 | Drift 帧(>2DIP) |");
        Console.WriteLine("  |---|---|---|---|---|---|---|---|---|");

        foreach (var f in model.FitReport)
        {
            double clipDip = f.ClipUnits * SimScale;
            Console.WriteLine($"  | {f.Name} | {f.SetupWidth:0} | {f.RuntimeWidth:0} | "
                              + $"{f.CenterOffsetX:+0;-0;0} | {boxW:0} | "
                              + $"{f.PeakRatioW:0.00}× | {clipDip:0.0} ({f.ClipUnits:0}) | {f.DriftedFrames}/{f.FrameCount} | "
                              + $"{f.MaxDriftX * SimScale:0.0} / {f.MaxDriftY * SimScale:0.0} DIP |");
        }

        // 验收判据（针对位移型动作）
        foreach (var animName in new[] { "unique", "unique2", "unique3" })
        {
            var f = model.FitReport.FirstOrDefault(x => x.Name == animName);
            if (f == null) continue;
            // 真正决定「会不会裁」的是「补偿后单帧需求 / 静态框半宽」，
            // 而不是「跨帧 union 宽 / 静态框宽」：后者把 N 个不同位置的帧叠在一起量，
            // 位移已经由 Drift 抵消，拿它当判据会把「骨架跑到远处」误读成「帧太宽」。
            double needRatio = f.PeakRatioW;
            string ok = needRatio <= 1.15 && f.ClipUnits <= 1e-3f ? "PASS" : "FAIL";
            Console.WriteLine($"  → {animName}: 补偿后需求/静态框半宽 = {needRatio:0.000}×，"
                              + $"跨帧union宽/静态框宽 = {f.RuntimeWidth / Math.Max(1e-3f, boxW):0.000}，"
                              + $"裁切 {f.ClipUnits:0} 单位 ({f.ClipUnits * SimScale:0.0} DIP)  [{ok}]");
        }
    }

    /// <summary>
    /// --shake：逐个动作模拟「idle → 动作 → idle」，量化抖动。
    ///
    /// 抖动的两个来源分开量：
    ///   · 窗口抖 = 动作期间窗口尺寸被改了多少次（2 DIP 死区，与 PetWindow 同口径）
    ///   · 角色抖 = 位移补偿 DriftX 的总变差（每帧变化量之和，DIP）；变差越大角色越像在飘
    /// </summary>
    public static void RunShake(bool allSkins = false)
    {
        var usable = SkinCatalog.All.Where(e => e.IsUsable && e.Dir != null).ToList();
        foreach (var skin in usable)
        {
            using var model = new SkeletonModel(
                Path.Combine(skin.Dir!, skin.Id + ".atlas"),
                Path.Combine(skin.Dir!, skin.Id + ".skel"));

            var view = new ViewFrame(model.Bounds, model.AnchorX, model.AnchorY, model.RawBounds, model.BodyBounds, model.BodyAxisCenter, model.DriftThreshold);
            double inset = Math.Max(12.0, (model.AnchorY - model.Bounds.MinY) * SimScale + 4.0);

            Console.WriteLine();
            Console.WriteLine($"=== 抖动体检 · {skin.Id} {skin.DisplayName} ===");
            Console.WriteLine("  | 动画 | 窗口resize | 窗口范围(DIP) | Drift总变差(DIP) | Drift单帧最大(DIP) | Drift范围(DIP) |");
            Console.WriteLine("  |---|---|---|---|---|---|");

            foreach (var animName in model.AnimationNames)
            {
                var anim = model.Data.FindAnimation(animName);
                if (anim == null) continue;

                // 从 idle 建立真实姿势（与运行时 PlayAction 一致）
                view.Reset();
                double now = 0;
                model.SetAnimation(0, "idle", true);
                int warmSteps = (int)(1.0 / SimDt);
                for (int i = 0; i < warmSteps; i++)
                {
                    model.Update((float)SimDt);
                    now += SimDt;
                    var b0 = SkeletonModel.BoxOf(model.Skeleton);
                    if (b0.HasValue) view.Update((float)now, model.BodyAxisX(), b0.Value.X0, b0.Value.Y0, b0.Value.X1, b0.Value.Y1, true);
                }

                model.SetAnimation(0, animName, false);

                double prevW = PetRenderControl.SnapDip(view.HalfW * 2 * SimScale * (1 + PetRenderControl.PadX), 1);
                double prevH = PetRenderControl.SnapDip(view.HalfH * 2 * SimScale * (1 + PetRenderControl.PadY) + inset, 1);
                double curW = prevW, curH = prevH;
                double minW = prevW, maxW = prevW;
                double prevDrift = view.DriftX;
                double driftVariance = 0, driftMaxStep = 0;
                double minDrift = view.DriftX, maxDrift = view.DriftX;
                int resizes = 0;

                int steps = (int)((Math.Max(0.1f, anim.Duration) + 0.5) / SimDt);
                for (int i = 0; i < steps; i++)
                {
                    model.Update((float)SimDt);
                    now += SimDt;
                    var fb = SkeletonModel.BoxOf(model.Skeleton);
                    if (fb.HasValue) view.Update((float)now, model.BodyAxisX(), fb.Value.X0, fb.Value.Y0, fb.Value.X1, fb.Value.Y1, true);

                    double w = PetRenderControl.SnapDip(view.HalfW * 2 * SimScale * (1 + PetRenderControl.PadX), 1);
                    double h = PetRenderControl.SnapDip(view.HalfH * 2 * SimScale * (1 + PetRenderControl.PadY) + inset, 1);
                    if (Math.Abs(w - curW) >= 2 || Math.Abs(h - curH) >= 2) { resizes++; curW = w; curH = h; }
                    minW = Math.Min(minW, w); maxW = Math.Max(maxW, w);

                    double d = view.DriftX * SimScale;
                    double step = Math.Abs(d - prevDrift);
                    driftVariance += step;
                    driftMaxStep = Math.Max(driftMaxStep, step);
                    minDrift = Math.Min(minDrift, d); maxDrift = Math.Max(maxDrift, d);
                    prevDrift = d;
                }

                Console.WriteLine($"  | {animName} | {resizes} | {minW:0}→{maxW:0} | {driftVariance:0.0} | "
                                  + $"{driftMaxStep:0.0} | {minDrift:0.0}..{maxDrift:0.0} |");
            }
        }
    }

    /// <summary>
    /// 播放时序模拟：idle → unique → idle → unique2 → idle → unique3 → idle。
    /// 验证「播放大位移动作时窗口不会暴涨」，以及动作之间来回切时 resize 次数。
    /// </summary>
    private static void PrintPlaySequence(string id, SkeletonModel model)
    {
        var seq = new (string Anim, double Seconds)[]
        {
            ("idle", 3), ("unique", 4), ("idle", 2),
            ("unique2", 3), ("idle", 2), ("unique3", 3), ("idle", 2),
        };

        var view = new ViewFrame(model.Bounds, model.AnchorX, model.AnchorY, model.RawBounds, model.BodyBounds, model.BodyAxisCenter, model.DriftThreshold);
        double inset = Math.Max(12.0, (model.AnchorY - model.Bounds.MinY) * SimScale + 4.0);

        double prevW = -1, prevH = -1;
        int totalResizes = 0;
        Console.WriteLine();
        Console.WriteLine($"  [播放时序模拟 · {id}]");

        foreach (var (anim, seconds) in seq)
        {
            if (!model.HasAnimation(anim)) continue;
            model.SetAnimation(0, anim, anim == "idle");

            double minW = double.MaxValue, maxW = double.MinValue;
            double minH = double.MaxValue, maxH = double.MinValue;
            int resizes = 0;

            int steps = (int)(seconds / SimDt);
            for (int i = 0; i < steps; i++)
            {
                model.Update((float)SimDt);
                double now = (i + 1) * SimDt;

                var fb = SkeletonModel.BoxOf(model.Skeleton);
                if (fb.HasValue)
                    view.Update((float)now, model.BodyAxisX(), fb.Value.X0, fb.Value.Y0, fb.Value.X1, fb.Value.Y1, true);

                double w = PetRenderControl.SnapDip(view.HalfW * 2.0 * SimScale * (1 + PetRenderControl.PadX), 1.0);
                double h = PetRenderControl.SnapDip(view.HalfH * 2.0 * SimScale * (1 + PetRenderControl.PadY) + inset, 1.0);

                // 真实窗口只在「与当前尺寸差 ≥2 DIP」时才 resize（PetWindow.OnSizeTick 的口径）
                if (prevW < 0) { prevW = w; prevH = h; }
                else if (Math.Abs(w - prevW) >= 2 || Math.Abs(h - prevH) >= 2) { resizes++; totalResizes++; prevW = w; prevH = h; }

                minW = Math.Min(minW, w); maxW = Math.Max(maxW, w);
                minH = Math.Min(minH, h); maxH = Math.Max(maxH, h);
            }

            Console.WriteLine($"    {anim,-8} {seconds:0}s  窗口 {minW:0}×{minH:0} → {maxW:0}×{maxH:0} DIP"
                              + $"  涨幅 {(maxW / Math.Max(1e-3, minW) - 1) * 100:0}%   resize {resizes}");
        }

        Console.WriteLine($"    → 整段时序 resize 合计 {totalResizes} 次（含动作切换时的扩张/收缩）");
    }

    /// <summary>
    /// 待机 60 秒模拟：用真实播放推进骨架，把每帧包围盒喂给 ViewFrame，
    /// 记录 DesiredPetSize 的变化次数与极差。resize 次数应为 0、极差应为 0。
    /// </summary>
    private static void PrintIdleSimulation(string id, SkeletonModel model)
    {
        string idle = model.HasAnimation("idle") ? "idle" : model.AnimationNames.FirstOrDefault() ?? "";
        if (idle.Length == 0) { Console.WriteLine("  （无动画，跳过 idle 模拟）"); return; }

        model.SetAnimation(0, idle, true);

        var view = new ViewFrame(model.Bounds, model.AnchorX, model.AnchorY);
        double inset = Math.Max(12.0, (model.AnchorY - model.Bounds.MinY) * SimScale + 4.0);

        double prevW = -1, prevH = -1;
        double minW = double.MaxValue, maxW = double.MinValue;
        double minH = double.MaxValue, maxH = double.MinValue;
        int resizes = 0;

        int steps = (int)(SimSeconds / SimDt);
        for (int i = 0; i < steps; i++)
        {
            model.Update((float)SimDt);
            double now = (i + 1) * SimDt;

            var fb = SkeletonModel.BoxOf(model.Skeleton);
            if (fb.HasValue)
                view.Update((float)now, model.BodyAxisX(), fb.Value.X0, fb.Value.Y0, fb.Value.X1, fb.Value.Y1, true);
            else
                view.Update((float)now, model.BodyAxisX(), model.Bounds.MinX, model.Bounds.MinY, model.Bounds.MaxX, model.Bounds.MaxY, false);

            // 与 PetWindow 同口径：DIP 量化到整数物理像素（scaling=1）
            double w = PetRenderControl.SnapDip(view.HalfW * 2.0 * SimScale * (1 + PetRenderControl.PadX), 1.0);
            double h = PetRenderControl.SnapDip(view.HalfH * 2.0 * SimScale * (1 + PetRenderControl.PadY) + inset, 1.0);

            if (prevW < 0) { prevW = w; prevH = h; }
            else if (Math.Abs(w - prevW) >= 2 || Math.Abs(h - prevH) >= 2) { resizes++; prevW = w; prevH = h; }

            minW = Math.Min(minW, w); maxW = Math.Max(maxW, w);
            minH = Math.Min(minH, h); maxH = Math.Max(maxH, h);
        }

        double rangeW = maxW - minW, rangeH = maxH - minH;
        string ok = resizes == 0 && rangeW < 0.5 && rangeH < 0.5 ? "PASS" : "FAIL";

        Console.WriteLine();
        Console.WriteLine($"  [idle 60s 模拟 · {id}] resize 次数 = {resizes}，窗口尺寸极差 = "
                          + $"{rangeW:0.0}×{rangeH:0.0} DIP（{minW:0}×{minH:0} → {maxW:0}×{maxH:0}）  [{ok}]");
    }
}
