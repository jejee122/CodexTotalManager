using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using CodexModelManager.Models;
using CodexModelManager.Services;
using ShapesPath = System.Windows.Shapes.Path;

namespace CodexModelManager.Controls;

/// <summary>
/// 每日 token 用量“能量曲线”控件：
/// - 圆润曲线（Catmull-Rom 转贝塞尔）+ 渐变主线；
/// - 能量流动：一串圆点沿曲线持续向前流动（DashOffset 动画）；
/// - 辉光呼吸：曲线下方辉光缓慢脉冲；
/// - 流动光点：一颗金色光珠沿整条曲线巡游（MatrixAnimationUsingPath）；
/// - 最新一天的数据点带光晕脉冲。
/// 全部动画纯视觉，不触碰任何数据路径；卸载时停止全部动画。
/// </summary>
public sealed class UsageFlowChart : Canvas
{
    private static readonly Color LineColor = Color.FromRgb(126, 212, 214);
    private static readonly Color LineBright = Color.FromRgb(191, 247, 255);
    private static readonly Color AccentGold = Color.FromRgb(227, 190, 117);
    private static readonly Color GridColor = Color.FromArgb(70, 143, 200, 208);
    private static readonly Color LabelColor = Color.FromRgb(169, 194, 191);

    private readonly List<Storyboard> _storyboards = new();
    private IReadOnlyList<DailyTokenUsagePoint>? _points;

    public UsageFlowChart()
    {
        ClipToBounds = true;
        SizeChanged += (_, _) => Render();
        Unloaded += (_, _) => StopAnimations();
    }

    public void SetDailySeries(IReadOnlyList<DailyTokenUsagePoint>? points)
    {
        _points = points;
        Render();
    }

    private void StopAnimations()
    {
        foreach (var storyboard in _storyboards)
        {
            storyboard.Stop(this);
        }
        _storyboards.Clear();
    }

    private void Render()
    {
        StopAnimations();
        Children.Clear();

        var width = ActualWidth;
        var height = ActualHeight;
        if (double.IsNaN(width) || double.IsNaN(height) || width < 80 || height < 60) return;
        if (_points is null || _points.Count == 0)
        {
            Children.Add(new TextBlock
            {
                Text = "暂无本地用量记录，成功请求出现后这里会亮起能量曲线",
                Foreground = new SolidColorBrush(LabelColor),
                FontSize = 11
            });
            return;
        }

        var padding = new Thickness(38, 16, 14, 20);
        var plotWidth = Math.Max(10, width - padding.Left - padding.Right);
        var plotHeight = Math.Max(10, height - padding.Top - padding.Bottom);

        var max = 0.0;
        foreach (var point in _points) max = Math.Max(max, point.TotalTokens);
        var yMax = UsageFlowMath.NiceCeiling(max * 1.15);
        var baseline = padding.Top + plotHeight;

        DrawGrid(padding, plotWidth, plotHeight, baseline, yMax);
        DrawDateLabels(padding, plotWidth, baseline);

        var anchors = new List<FlowPoint>(_points.Count);
        for (var index = 0; index < _points.Count; index++)
        {
            var x = _points.Count == 1
                ? padding.Left + plotWidth / 2
                : padding.Left + plotWidth * index / (_points.Count - 1);
            var ratio = yMax <= 0 ? 0 : _points[index].TotalTokens / yMax;
            var y = baseline - plotHeight * ratio;
            anchors.Add(new FlowPoint(x, y));
        }

        var geometry = BuildGeometry(anchors);
        DrawAreaFill(geometry, baseline, padding.Left, plotWidth);
        DrawGlowLine(geometry);
        DrawMainLine(geometry);
        DrawFlowOverlay(geometry);
        DrawComet(geometry);
        DrawLatestPoint(anchors[^1]);
    }

    private PathGeometry BuildGeometry(IReadOnlyList<FlowPoint> anchors)
    {
        var segments = UsageFlowMath.SmoothCurve(anchors);
        var figure = new PathFigure
        {
            StartPoint = new Point(anchors[0].X, anchors[0].Y),
            IsClosed = false
        };
        foreach (var segment in segments)
        {
            figure.Segments.Add(new BezierSegment(
                new Point(segment.Control1.X, segment.Control1.Y),
                new Point(segment.Control2.X, segment.Control2.Y),
                new Point(segment.End.X, segment.End.Y),
                isStroked: true));
        }
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }

    private void DrawGrid(Thickness padding, double plotWidth, double plotHeight, double baseline, double yMax)
    {
        for (var line = 0; line <= 3; line++)
        {
            var ratio = line / 3.0;
            var y = baseline - plotHeight * ratio;
            Children.Add(new Line
            {
                X1 = padding.Left,
                X2 = padding.Left + plotWidth,
                Y1 = y,
                Y2 = y,
                Stroke = new SolidColorBrush(GridColor),
                StrokeThickness = line == 0 ? 1 : 0.5,
                StrokeDashArray = line == 0 ? null : new DoubleCollection { 2, 4 }
            });
            var value = (long)Math.Round(yMax * ratio);
            Children.Add(new TextBlock
            {
                Text = UsageFormatting.Number(value),
                Foreground = new SolidColorBrush(LabelColor),
                FontSize = 9.5,
                TextAlignment = TextAlignment.Right,
                Width = padding.Left - 6
            }.WithCanvasPosition(0, y - 7));
        }
    }

    private void DrawDateLabels(Thickness padding, double plotWidth, double baseline)
    {
        if (_points!.Count == 0) return;
        var skip = Math.Max(1, (int)Math.Ceiling(_points.Count / 6.0));
        for (var index = 0; index < _points.Count; index++)
        {
            if (index % skip != 0 && index != _points.Count - 1) continue;
            var x = padding.Left + (_points.Count == 1
                ? plotWidth / 2
                : plotWidth * index / (_points.Count - 1));
            var isToday = index == _points.Count - 1;
            Children.Add(new TextBlock
            {
                Text = _points[index].LocalDate.ToString(isToday ? "'今天' MM-dd" : "MM-dd"),
                Foreground = new SolidColorBrush(isToday
                    ? Color.FromRgb(227, 190, 117)
                    : LabelColor),
                FontSize = 9.5,
                TextAlignment = TextAlignment.Center,
                Width = 46
            }.WithCanvasPosition(x - 23, baseline + 4));
        }
    }

    private void DrawAreaFill(PathGeometry geometry, double baseline, double left, double plotWidth)
    {
        var areaFigure = geometry.Figures[0].Clone();
        areaFigure.IsClosed = true;
        areaFigure.Segments.Add(new LineSegment(new Point(left + plotWidth, baseline), true));
        areaFigure.Segments.Add(new LineSegment(new Point(left, baseline), true));
        var areaGeometry = new PathGeometry(new[] { areaFigure });
        var fill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(96, LineColor.R, LineColor.G, LineColor.B), 0),
                new GradientStop(Color.FromArgb(10, LineColor.R, LineColor.G, LineColor.B), 1)
            }
        };
        var area = new ShapesPath { Data = areaGeometry, Fill = fill, Opacity = 0.65 };
        Children.Add(area);
        var breathe = new DoubleAnimation(0.45, 0.85, TimeSpan.FromSeconds(3.2))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase()
        };
        StartStoryboard(area, UIElement.OpacityProperty, breathe);
    }

    private void DrawGlowLine(PathGeometry geometry)
    {
        var glow = new ShapesPath
        {
            Data = geometry,
            Stroke = new SolidColorBrush(Color.FromArgb(200, LineColor.R, LineColor.G, LineColor.B)),
            StrokeThickness = 8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 16 },
            Opacity = 0.22
        };
        Children.Add(glow);
        var pulse = new DoubleAnimation(0.12, 0.4, TimeSpan.FromSeconds(2.6))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase()
        };
        StartStoryboard(glow, UIElement.OpacityProperty, pulse);
    }

    private void DrawMainLine(PathGeometry geometry)
    {
        var stroke = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            GradientStops =
            {
                new GradientStop(Color.FromRgb(84, 158, 168), 0),
                new GradientStop(LineColor, 0.62),
                new GradientStop(LineBright, 1)
            }
        };
        Children.Add(new ShapesPath
        {
            Data = geometry,
            Stroke = stroke,
            StrokeThickness = 2.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        });
    }

    private void DrawFlowOverlay(PathGeometry geometry)
    {
        var flow = new ShapesPath
        {
            Data = geometry,
            Stroke = new SolidColorBrush(LineBright),
            StrokeThickness = 3.4,
            StrokeDashCap = PenLineCap.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeDashArray = new DoubleCollection { 0, 11 }
        };
        Children.Add(flow);
        var dash = new DoubleAnimation
        {
            From = 0,
            To = -11,
            Duration = TimeSpan.FromSeconds(1.25),
            RepeatBehavior = RepeatBehavior.Forever
        };
        StartStoryboard(flow, Shape.StrokeDashOffsetProperty, dash);
    }

    private void DrawComet(PathGeometry geometry)
    {
        var cometHolder = new Canvas { Width = 0, Height = 0 };
        var transform = new MatrixTransform(Matrix.Identity);
        cometHolder.RenderTransform = transform;
        var comet = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = new SolidColorBrush(AccentGold),
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 7 }
        };
        Canvas.SetLeft(comet, -4.5);
        Canvas.SetTop(comet, -4.5);
        cometHolder.Children.Add(comet);
        Children.Add(cometHolder);

        var core = new Ellipse
        {
            Width = 4.5,
            Height = 4.5,
            Fill = Brushes.White
        };
        cometHolder.Children.Add(core);
        Canvas.SetLeft(core, -2.25);
        Canvas.SetTop(core, -2.25);

        var ride = new MatrixAnimationUsingPath
        {
            PathGeometry = geometry,
            Duration = TimeSpan.FromSeconds(3.4),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(ride, cometHolder);
        Storyboard.SetTargetProperty(ride, new PropertyPath("(UIElement.RenderTransform).(MatrixTransform.Matrix)"));
        storyboard.Children.Add(ride);
        storyboard.Begin(this, true);
        _storyboards.Add(storyboard);
    }

    private void DrawLatestPoint(FlowPoint latest)
    {
        var halo = new Ellipse
        {
            Width = 18,
            Height = 18,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(200, LineBright.R, LineBright.G, LineBright.B), 0),
                    new GradientStop(Color.FromArgb(0, LineBright.R, LineBright.G, LineBright.B), 1)
                }
            },
            Opacity = 0.35
        };
        Canvas.SetLeft(halo, latest.X - 9);
        Canvas.SetTop(halo, latest.Y - 9);
        Children.Add(halo);
        var pulse = new DoubleAnimation(0.12, 0.65, TimeSpan.FromSeconds(1.8))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase()
        };
        StartStoryboard(halo, UIElement.OpacityProperty, pulse);

        Children.Add(new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = new SolidColorBrush(LineBright),
            Stroke = Brushes.White,
            StrokeThickness = 1.2
        }.WithCanvasPosition(latest.X - 3.5, latest.Y - 3.5));
    }

    private void StartStoryboard(FrameworkElement target, DependencyProperty property, DoubleAnimation animation)
    {
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        storyboard.Children.Add(animation);
        storyboard.Begin(this, true);
        _storyboards.Add(storyboard);
    }
}

internal static class CanvasPositionExtensions
{
    public static T WithCanvasPosition<T>(this T element, double left, double top) where T : UIElement
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        return element;
    }
}
