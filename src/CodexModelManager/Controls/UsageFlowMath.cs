namespace CodexModelManager.Controls;

public readonly record struct FlowPoint(double X, double Y);

public readonly record struct CubicSegment(FlowPoint Start, FlowPoint Control1, FlowPoint Control2, FlowPoint End);

/// <summary>
/// 用量曲线的平滑数学：Catmull-Rom 样条转三次贝塞尔，让折线变圆润。
/// 纯函数、不依赖 WPF，可独立单元测试。
/// </summary>
public static class UsageFlowMath
{
    /// <summary>
    /// 把锚点序列转成平滑贝塞尔段。tension 0=折线，0.5=标准圆滑，1=最松弛。
    /// 顺序保持：结果首段起点=第一个锚点，末段终点=最后一个锚点。
    /// </summary>
    public static IReadOnlyList<CubicSegment> SmoothCurve(IReadOnlyList<FlowPoint> anchors, double tension = 0.5)
    {
        if (anchors is null || anchors.Count == 0) return Array.Empty<CubicSegment>();
        if (anchors.Count == 1) return new[] { new CubicSegment(anchors[0], anchors[0], anchors[0], anchors[0]) };
        var k = Math.Clamp(tension, 0, 1) / 3.0;
        var segments = new List<CubicSegment>(anchors.Count - 1);
        for (var i = 0; i < anchors.Count - 1; i++)
        {
            var p0 = anchors[Math.Max(0, i - 1)];
            var p1 = anchors[i];
            var p2 = anchors[i + 1];
            var p3 = anchors[Math.Min(anchors.Count - 1, i + 2)];
            var c1 = new FlowPoint(p1.X + (p2.X - p0.X) * k, p1.Y + (p2.Y - p0.Y) * k);
            var c2 = new FlowPoint(p2.X - (p3.X - p1.X) * k, p2.Y - (p3.Y - p1.Y) * k);
            segments.Add(new CubicSegment(p1, c1, c2, p2));
        }
        return segments;
    }

    /// <summary>把数值向上取整到 1/2/5×10ⁿ 的“好看刻度”，给纵轴留出呼吸空间。</summary>
    public static double NiceCeiling(double value)
    {
        if (double.IsNaN(value) || value <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var scaled = value / magnitude;
        double nice = scaled switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 5 => 5,
            _ => 10
        };
        return nice * magnitude;
    }
}
