using System.Diagnostics;
using System.Globalization;
using ClipPort.Models;
using ClipPort.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace ClipPort;

public sealed partial class MainWindow
{
    private const string SideBySideChartsGlyph = "\uE8A9";
    private const string StackedChartsGlyph = "\uE8FD";

    private static readonly IReadOnlyList<double> EmptyWaveformSamples = Array.Empty<double>();
    private static readonly double[] WaveformScaleMultipliers = [3, 2, 1, 0];
    private static readonly TimeSpan WaveformAnimationDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan WaveformFrameInterval = TimeSpan.FromMilliseconds(16);

    private readonly Dictionary<Polyline, WaveformAnimationState> _waveformAnimations = [];
    private IReadOnlyList<double> _displayedCopyByteSpeedSamples = EmptyWaveformSamples;
    private IReadOnlyList<double> _displayedCopyItemSpeedSamples = EmptyWaveformSamples;
    private IReadOnlyList<double> _displayedCopyThroughputProgressSamples = EmptyWaveformSamples;
    private IReadOnlyList<double> _displayedVerifyByteSpeedSamples = EmptyWaveformSamples;
    private IReadOnlyList<double> _displayedVerifyItemSpeedSamples = EmptyWaveformSamples;
    private IReadOnlyList<double> _displayedVerifyThroughputProgressSamples = EmptyWaveformSamples;
    private bool _areCopyThroughputChartsStacked;
    private bool _areVerifyThroughputChartsStacked;

    private void CopyThroughputChartsLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        _areCopyThroughputChartsStacked = !_areCopyThroughputChartsStacked;
        ApplyThroughputChartLayouts();
    }

    private void VerifyThroughputChartsLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        _areVerifyThroughputChartsStacked = !_areVerifyThroughputChartsStacked;
        ApplyThroughputChartLayouts();
    }

    private void ApplyThroughputChartLayouts()
    {
        ApplyThroughputChartLayout(
            CopyByteRateChartCard,
            CopyItemRateChartCard,
            CopyThroughputChartsLayoutButton,
            CopyThroughputChartsLayoutIcon,
            _areCopyThroughputChartsStacked);
        ApplyThroughputChartLayout(
            VerifyByteRateChartCard,
            VerifyItemRateChartCard,
            VerifyThroughputChartsLayoutButton,
            VerifyThroughputChartsLayoutIcon,
            _areVerifyThroughputChartsStacked);
    }

    private static void ApplyThroughputChartLayout(
        Border byteRateCard,
        Border itemRateCard,
        Button layoutButton,
        FontIcon layoutIcon,
        bool isStacked)
    {
        Grid.SetRow(byteRateCard, 0);
        Grid.SetColumn(byteRateCard, 0);
        Grid.SetColumnSpan(byteRateCard, isStacked ? 2 : 1);

        Grid.SetRow(itemRateCard, isStacked ? 1 : 0);
        Grid.SetColumn(itemRateCard, isStacked ? 0 : 1);
        Grid.SetColumnSpan(itemRateCard, isStacked ? 2 : 1);

        string accessibilityText = ResourceService.GetString(
            isStacked
                ? "ThroughputChartsLayout.ShowSideBySide"
                : "ThroughputChartsLayout.ShowStacked");
        layoutIcon.Glyph = isStacked
            ? StackedChartsGlyph
            : SideBySideChartsGlyph;
        ToolTipService.SetToolTip(layoutButton, accessibilityText);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            layoutButton,
            accessibilityText);
    }

    private void UpdateThroughputCharts(
        IReadOnlyList<double>? copyByteSpeedSamples,
        IReadOnlyList<double>? copyItemSpeedSamples,
        IReadOnlyList<double>? copyThroughputProgressSamples,
        IReadOnlyList<double>? verifyByteSpeedSamples,
        IReadOnlyList<double>? verifyItemSpeedSamples,
        IReadOnlyList<double>? verifyThroughputProgressSamples,
        bool animate = true)
    {
        _displayedCopyByteSpeedSamples = copyByteSpeedSamples ?? EmptyWaveformSamples;
        _displayedCopyItemSpeedSamples = copyItemSpeedSamples ?? EmptyWaveformSamples;
        _displayedCopyThroughputProgressSamples = copyThroughputProgressSamples ?? EmptyWaveformSamples;
        _displayedVerifyByteSpeedSamples = verifyByteSpeedSamples ?? EmptyWaveformSamples;
        _displayedVerifyItemSpeedSamples = verifyItemSpeedSamples ?? EmptyWaveformSamples;
        _displayedVerifyThroughputProgressSamples = verifyThroughputProgressSamples ?? EmptyWaveformSamples;

        UpdateByteRateChart(
            CopyByteRateCurrentText,
            CopyByteRateMaximumText,
            CopyByteRateMinimumText,
            CopyByteRateUnitText,
            CopyByteRateCanvas,
            CopyByteRateScaleLabels,
            CopyByteRateFill,
            CopyByteRateLine,
            CopyByteRateGlow,
            _displayedCopyByteSpeedSamples,
            _displayedCopyThroughputProgressSamples,
            animate);
        UpdateItemRateChart(
            CopyItemRateCurrentText,
            CopyItemRateMaximumText,
            CopyItemRateMinimumText,
            CopyItemRateUnitText,
            CopyItemRateCanvas,
            CopyItemRateScaleLabels,
            CopyItemRateFill,
            CopyItemRateLine,
            CopyItemRateGlow,
            _displayedCopyItemSpeedSamples,
            _displayedCopyThroughputProgressSamples,
            animate);
        UpdateByteRateChart(
            VerifyByteRateCurrentText,
            VerifyByteRateMaximumText,
            VerifyByteRateMinimumText,
            VerifyByteRateUnitText,
            VerifyByteRateCanvas,
            VerifyByteRateScaleLabels,
            VerifyByteRateFill,
            VerifyByteRateLine,
            VerifyByteRateGlow,
            _displayedVerifyByteSpeedSamples,
            _displayedVerifyThroughputProgressSamples,
            animate);
        UpdateItemRateChart(
            VerifyItemRateCurrentText,
            VerifyItemRateMaximumText,
            VerifyItemRateMinimumText,
            VerifyItemRateUnitText,
            VerifyItemRateCanvas,
            VerifyItemRateScaleLabels,
            VerifyItemRateFill,
            VerifyItemRateLine,
            VerifyItemRateGlow,
            _displayedVerifyItemSpeedSamples,
            _displayedVerifyThroughputProgressSamples,
            animate);
    }

    private void ThroughputChart_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateThroughputCharts(
            _displayedCopyByteSpeedSamples,
            _displayedCopyItemSpeedSamples,
            _displayedCopyThroughputProgressSamples,
            _displayedVerifyByteSpeedSamples,
            _displayedVerifyItemSpeedSamples,
            _displayedVerifyThroughputProgressSamples,
            animate: false);

    private void UpdateByteRateChart(
        TextBlock currentText,
        TextBlock maximumText,
        TextBlock minimumText,
        TextBlock unitText,
        Canvas canvas,
        Grid scaleLabels,
        Polygon fill,
        Polyline line,
        Polyline glow,
        IReadOnlyList<double> samples,
        IReadOnlyList<double> progressPositions,
        bool animate)
    {
        double peak = GetPeak(samples);
        double minimumRate = GetMinimumPositive(samples);
        double currentRate = GetCurrent(samples);
        (double scale, string unit) = peak >= 1024d * 1024 * 1024
            ? (1024d * 1024 * 1024, "GB/s")
            : (1024d * 1024, "MB/s");
        double divisionStep = DisplayFormatting.GetWaveformDivisionStep(peak / scale);
        double chartMaximum = divisionStep * 4 * scale;
        currentText.Text = FormatRate(currentRate, scale, unit);
        maximumText.Text = $"↑ {FormatRate(peak, scale, unit)}";
        minimumText.Text = $"↓ {FormatRate(minimumRate, scale, unit)}";
        unitText.Text = unit;
        UpdateScaleLabels(scaleLabels, divisionStep);
        DrawWaveform(
            canvas,
            fill,
            line,
            glow,
            samples,
            progressPositions,
            chartMaximum,
            animate);
    }

    private void UpdateItemRateChart(
        TextBlock currentText,
        TextBlock maximumText,
        TextBlock minimumText,
        TextBlock unitText,
        Canvas canvas,
        Grid scaleLabels,
        Polygon fill,
        Polyline line,
        Polyline glow,
        IReadOnlyList<double> samples,
        IReadOnlyList<double> progressPositions,
        bool animate)
    {
        double peak = GetPeak(samples);
        double minimumRate = GetMinimumPositive(samples);
        double divisionStep = DisplayFormatting.GetWaveformDivisionStep(peak);
        currentText.Text = FormatItemRate(GetCurrent(samples));
        maximumText.Text = $"↑ {FormatItemRate(peak)}";
        minimumText.Text = $"↓ {FormatItemRate(minimumRate)}";
        unitText.Text = ResourceService.GetString("Unit.ItemsPerSecond");
        UpdateScaleLabels(scaleLabels, divisionStep);
        DrawWaveform(
            canvas,
            fill,
            line,
            glow,
            samples,
            progressPositions,
            divisionStep * 4,
            animate);
    }

    private static void UpdateScaleLabels(
        Grid scaleLabels,
        double divisionStep)
    {
        TextBlock[] labels = scaleLabels.Children
            .OfType<TextBlock>()
            .OrderBy(Grid.GetRow)
            .ToArray();
        for (int index = 0;
             index < labels.Length && index < WaveformScaleMultipliers.Length;
             index++)
        {
            labels[index].Text = (divisionStep * WaveformScaleMultipliers[index])
                .ToString("0.#", CultureInfo.CurrentCulture);
        }
    }

    private void DrawWaveform(
        Canvas canvas,
        Polygon fill,
        Polyline line,
        Polyline glow,
        IReadOnlyList<double> samples,
        IReadOnlyList<double> progressPositions,
        double chartMaximum,
        bool animate)
    {
        double width = canvas.ActualWidth;
        double height = canvas.ActualHeight;
        if (width <= 0 || height <= 0 || samples.Count == 0)
        {
            SetWaveformGeometry(fill, line, glow, [], [], animate: false);
            return;
        }

        double verticalPadding = Math.Min(3, height / 4);
        double drawableHeight = Math.Max(1, height - verticalPadding * 2);
        double bottomY = verticalPadding + drawableHeight;
        double scaleMaximum = Math.Max(chartMaximum, double.Epsilon);
        var linePoints = new List<Point>(samples.Count + 1);
        double firstX = GetWaveformX(width, samples.Count, progressPositions, 0);
        if (firstX > 0)
        {
            linePoints.Add(new Point(0, bottomY));
        }

        for (int index = 0; index < samples.Count; index++)
        {
            double x = GetWaveformX(width, samples.Count, progressPositions, index);
            double normalized = Math.Clamp(samples[index] / scaleMaximum, 0, 1);
            double y = verticalPadding + drawableHeight * (1 - normalized);
            linePoints.Add(new Point(x, y));
        }

        List<Point> fillPoints = BuildFillPoints(linePoints, bottomY);
        SetWaveformGeometry(fill, line, glow, linePoints, fillPoints, animate);
    }

    private static double GetWaveformX(
        double width,
        int sampleCount,
        IReadOnlyList<double> progressPositions,
        int index)
    {
        if (progressPositions.Count == sampleCount)
        {
            return width * Math.Clamp(progressPositions[index], 0, 1);
        }

        // Histories written before progress positions were introduced retain
        // their original full-width presentation.
        return sampleCount <= 1 ? width : width * index / (sampleCount - 1);
    }

    private void SetWaveformGeometry(
        Polygon fill,
        Polyline line,
        Polyline glow,
        IReadOnlyList<Point> targetLinePoints,
        IReadOnlyList<Point> targetFillPoints,
        bool animate)
    {
        if (_waveformAnimations.TryGetValue(line, out WaveformAnimationState? runningAnimation) &&
            PointsEqual(runningAnimation.TargetLinePoints, targetLinePoints))
        {
            return;
        }

        if (_waveformAnimations.Remove(line, out WaveformAnimationState? previousAnimation))
        {
            previousAnimation.Timer.Stop();
        }

        List<Point> currentLinePoints = line.Points.ToList();
        if (!animate || targetLinePoints.Count == 0 ||
            PointsEqual(currentLinePoints, targetLinePoints))
        {
            ReplacePoints(line.Points, targetLinePoints);
            ReplacePoints(glow.Points, targetLinePoints);
            ReplacePoints(fill.Points, targetFillPoints);
            return;
        }

        List<Point> startLinePoints = AlignStartPoints(
            currentLinePoints,
            targetLinePoints,
            targetFillPoints[^1].Y);
        List<Point> startFillPoints = BuildFillPoints(
            startLinePoints,
            targetFillPoints[^1].Y);
        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = WaveformFrameInterval;
        var animation = new WaveformAnimationState(
            timer,
            fill,
            line,
            glow,
            startLinePoints,
            targetLinePoints.ToList(),
            startFillPoints,
            targetFillPoints.ToList(),
            Stopwatch.GetTimestamp());
        timer.Tick += (_, _) => AdvanceWaveformAnimation(line);
        _waveformAnimations[line] = animation;
        timer.Start();
        AdvanceWaveformAnimation(line);
    }

    private void AdvanceWaveformAnimation(Polyline animationKey)
    {
        if (!_waveformAnimations.TryGetValue(animationKey, out WaveformAnimationState? animation))
        {
            return;
        }

        double elapsedSeconds = (Stopwatch.GetTimestamp() - animation.StartTimestamp) /
            (double)Stopwatch.Frequency;
        double linearProgress = Math.Clamp(
            elapsedSeconds / WaveformAnimationDuration.TotalSeconds,
            0,
            1);
        // Cubic ease-out is quick to respond while settling gently at the new point.
        double easedProgress = 1 - Math.Pow(1 - linearProgress, 3);
        InterpolatePoints(
            animation.Line.Points,
            animation.StartLinePoints,
            animation.TargetLinePoints,
            easedProgress);
        InterpolatePoints(
            animation.Glow.Points,
            animation.StartLinePoints,
            animation.TargetLinePoints,
            easedProgress);
        InterpolatePoints(
            animation.Fill.Points,
            animation.StartFillPoints,
            animation.TargetFillPoints,
            easedProgress);

        if (linearProgress < 1)
        {
            return;
        }

        animation.Timer.Stop();
        _waveformAnimations.Remove(animationKey);
        ReplacePoints(animation.Line.Points, animation.TargetLinePoints);
        ReplacePoints(animation.Glow.Points, animation.TargetLinePoints);
        ReplacePoints(animation.Fill.Points, animation.TargetFillPoints);
    }

    private static List<Point> AlignStartPoints(
        IReadOnlyList<Point> currentPoints,
        IReadOnlyList<Point> targetPoints,
        double bottomY)
    {
        if (currentPoints.Count == 0)
        {
            return Enumerable.Repeat(new Point(0, bottomY), targetPoints.Count).ToList();
        }

        var aligned = new List<Point>(targetPoints.Count);
        if (currentPoints.Count <= targetPoints.Count)
        {
            aligned.AddRange(currentPoints);
            while (aligned.Count < targetPoints.Count)
            {
                aligned.Add(currentPoints[^1]);
            }
            return aligned;
        }

        for (int index = 0; index < targetPoints.Count; index++)
        {
            int sourceIndex = targetPoints.Count == 1
                ? currentPoints.Count - 1
                : (int)Math.Round(index * (currentPoints.Count - 1d) / (targetPoints.Count - 1d));
            aligned.Add(currentPoints[sourceIndex]);
        }
        return aligned;
    }

    private static List<Point> BuildFillPoints(
        IReadOnlyList<Point> linePoints,
        double bottomY)
    {
        if (linePoints.Count == 0)
        {
            return [];
        }

        var fillPoints = new List<Point>(linePoints.Count + 2);
        fillPoints.AddRange(linePoints);
        fillPoints.Add(new Point(linePoints[^1].X, bottomY));
        fillPoints.Add(new Point(linePoints[0].X, bottomY));
        return fillPoints;
    }

    private static void InterpolatePoints(
        PointCollection destination,
        IReadOnlyList<Point> startPoints,
        IReadOnlyList<Point> targetPoints,
        double progress)
    {
        destination.Clear();
        for (int index = 0; index < targetPoints.Count; index++)
        {
            Point start = startPoints[index];
            Point target = targetPoints[index];
            destination.Add(new Point(
                start.X + (target.X - start.X) * progress,
                start.Y + (target.Y - start.Y) * progress));
        }
    }

    private static void ReplacePoints(
        PointCollection destination,
        IReadOnlyList<Point> source)
    {
        destination.Clear();
        foreach (Point point in source)
        {
            destination.Add(point);
        }
    }

    private static bool PointsEqual(
        IReadOnlyList<Point> left,
        IReadOnlyList<Point> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (Math.Abs(left[index].X - right[index].X) > 0.01 ||
                Math.Abs(left[index].Y - right[index].Y) > 0.01)
            {
                return false;
            }
        }
        return true;
    }

    private static double GetPeak(IReadOnlyList<double> samples) =>
        samples.Count == 0 ? 0 : samples.Max();

    private static double GetMinimumPositive(IReadOnlyList<double> samples) =>
        samples
            .Where(sample => double.IsFinite(sample) && sample > 0)
            .DefaultIfEmpty(0)
            .Min();

    private static double GetCurrent(IReadOnlyList<double> samples) =>
        samples.Count == 0 ? 0 : samples[^1];

    private static string FormatRate(double value, double scale, string unit) =>
        $"{value / scale:F2} {unit}";

    private static string FormatItemRate(double value) =>
        ResourceService.Format(
            "Format.ItemsPerSecond",
            value.ToString("F2", CultureInfo.CurrentCulture));

    private sealed record WaveformAnimationState(
        DispatcherQueueTimer Timer,
        Polygon Fill,
        Polyline Line,
        Polyline Glow,
        IReadOnlyList<Point> StartLinePoints,
        IReadOnlyList<Point> TargetLinePoints,
        IReadOnlyList<Point> StartFillPoints,
        IReadOnlyList<Point> TargetFillPoints,
        long StartTimestamp);
}
