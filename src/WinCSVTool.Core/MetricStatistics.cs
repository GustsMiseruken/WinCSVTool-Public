namespace WinCSVTool.Core;

/// <summary>Welford population moments, with a scaled fallback for finite values whose differences or squared deviations overflow.</summary>
internal sealed class MetricStatistics
{
    private double scale, scaledMean, scaledM2, scaledSum, compensation, mean, m2;
    private bool ordinary = true;
    public long Count { get; private set; }
    public double Minimum { get; private set; } = double.PositiveInfinity;
    public double Maximum { get; private set; } = double.NegativeInfinity;
    public void Add(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        Count++; Minimum = Math.Min(Minimum, value); Maximum = Math.Max(Maximum, value);
        if (ordinary)
        {
            var delta = value - mean; var nextMean = mean + delta / Count;
            var nextM2 = m2 + delta * (value - nextMean);
            ordinary = double.IsFinite(nextMean) && double.IsFinite(nextM2);
            if (ordinary) { mean = nextMean; m2 = nextM2; }
        }
        var magnitude = Math.Abs(value);
        if (magnitude > scale)
        {
            var factor = scale / magnitude;
            scaledMean *= factor; scaledM2 *= factor * factor; scaledSum *= factor; compensation *= factor; scale = magnitude;
        }
        var normalized = scale == 0 ? 0 : value / scale;
        var difference = normalized - scaledMean;
        scaledMean += difference / Count; scaledM2 += difference * (normalized - scaledMean);
        var nextSum = scaledSum + normalized;
        compensation += Math.Abs(scaledSum) >= Math.Abs(normalized) ? (scaledSum - nextSum) + normalized : (normalized - nextSum) + scaledSum;
        scaledSum = nextSum;
    }
    public double? Mean => Count == 0 ? null : ordinary ? mean : Math.Clamp(scaledMean, -1, 1) * scale;
    public double? StandardDeviation
    {
        get
        {
            if (Count == 0) return null;
            var variance = m2 / Count;
            return ordinary && (variance >= 2.2250738585072014e-308 || scaledM2 == 0)
                ? Math.Sqrt(Math.Max(0, variance)) : Math.Sqrt(Math.Clamp(scaledM2 / Count, 0, 1)) * scale;
        }
    }
    public double? Sum
    {
        get { if (Count == 0) return null; var value = (scaledSum + compensation) * scale; return double.IsFinite(value) ? value : null; }
    }
    public (double? Low, double? High) Band(int sigma)
    {
        if (Count == 0) return (null, null);
        // Saturating endpoints represent bands extending beyond the finite numeric domain.
        var width = StandardDeviation!.Value * sigma; var center = Mean!.Value;
        if (double.IsFinite(width)) return (Math.Max(-double.MaxValue, center - width), Math.Min(double.MaxValue, center + width));
        var deviation = Math.Sqrt(Math.Clamp(scaledM2 / Count, 0, 1));
        return (SaturatingProduct(scaledMean - sigma * deviation, scale), SaturatingProduct(scaledMean + sigma * deviation, scale));
    }
    private static double SaturatingProduct(double value, double multiplier)
    {
        var result = value * multiplier;
        return double.IsFinite(result) ? result : value < 0 ? -double.MaxValue : double.MaxValue;
    }
}
