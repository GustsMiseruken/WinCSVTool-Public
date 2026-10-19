using System.Drawing.Drawing2D;
using System.Globalization;
using WinCSVTool.Core;

namespace WinCSVTool;

/// <summary>Paints cached data only; full distributions retain their original denominator.</summary>
internal sealed class LogSeriesChart : Control
{
    private MetricAnalysisResult? _result;
    private IReadOnlyList<MetricPoint> _points = [];
    private int _shape, _visibleAxes = 2;
    private bool _bands, _outliers;
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 20000, InitialDelay = 350, ReshowDelay = 100 };
    private readonly List<ChartHit> _hits = [];
    private string _hover = "";
    private sealed record ChartHit(RectangleF Bounds, string Description, float Start = 0, float Sweep = 0);
    private sealed record Slice(string Label, long Count, Color Color, bool Hidden = false);
    private static readonly Color[] Palette =
    [
        Color.FromArgb(74, 111, 211), Color.FromArgb(206, 120, 51), Color.FromArgb(39, 151, 127), Color.FromArgb(145, 104, 196),
        Color.FromArgb(199, 96, 129), Color.FromArgb(51, 146, 181), Color.FromArgb(148, 144, 54), Color.FromArgb(167, 112, 82),
        Color.FromArgb(91, 143, 73), Color.FromArgb(191, 93, 69), Color.FromArgb(94, 122, 161), Color.FromArgb(168, 125, 174),
        Color.FromArgb(68, 142, 141), Color.FromArgb(177, 143, 79), Color.FromArgb(133, 136, 196), Color.FromArgb(146, 111, 133),
        Color.FromArgb(110, 152, 174), Color.FromArgb(164, 151, 114), Color.FromArgb(120, 153, 118), Color.FromArgb(188, 119, 151),
        Color.FromArgb(119, 131, 145), Color.FromArgb(192, 142, 108), Color.FromArgb(101, 145, 161), Color.FromArgb(152, 126, 163)
    ];
    internal static Color SeriesColor(MetricAnalysisResult result, int axis, string label)
    {
        for (var i = 0; i < result.SeriesSummaries.Count; i++)
            if (result.SeriesSummaries[i].Axis == axis && result.SeriesSummaries[i].Label == label) return Palette[i % Palette.Length];
        return Palette[Math.Abs(axis % Palette.Length)];
    }
    internal LogSeriesChart()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
        AccessibleName = "ログ調査グラフ";
    }
    internal void SetMetrics(MetricAnalysisResult? result, IReadOnlyList<MetricPoint> points, int shape, bool bands, bool outliers, int visibleAxes = 2)
    {
        _result = result; _points = points; _shape = shape; _bands = bands; _outliers = outliers; _visibleAxes = Math.Clamp(visibleAxes, 1, 2);
        _hits.Clear(); _hover = ""; _toolTip.SetToolTip(this, null);
        if (result?.Axes.FirstOrDefault()?.Mode == MetricMode.Distribution)
            AccessibleDescription = "全対象を集計した分類別の構成。" + string.Join(" / ", points.Select(point => $"{point.Label}: {point.MatchedCount:N0}件 ({Percentage(point.MatchedCount, point.Count)})"));
        else
        {
            AccessibleDescription = $"{Math.Min(result?.Axes.Count ?? 0, _visibleAxes)}軸・{points.Count:N0}点。軸Aは左、軸Bは右の目盛り。集計間隔を超えた欠測期間は線で結びません。";
            if (result is not null) AccessibleDescription += string.Join(" / ", result.Axes.Take(_visibleAxes).Select((axis, index) => $"{(index == 0 ? "A" : "B")}: {axis.Name ?? "系列"} ({Unit(index)})"));
            if (points.Count > 0) AccessibleDescription += $" 表示期間: {points.MinBy(point => point.UnixMilliseconds)!.Time} ～ {points.MaxBy(point => point.UnixMilliseconds)!.Time}";
        }
        Invalidate();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose(); base.Dispose(disposing);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); var description = "";
        for (var i = _hits.Count - 1; i >= 0; i--)
        {
            var hit = _hits[i]; if (!hit.Bounds.Contains(e.Location)) continue;
            if (hit.Sweep > 0)
            {
                var x = (e.X - hit.Bounds.X - hit.Bounds.Width / 2) / (hit.Bounds.Width / 2);
                var y = (e.Y - hit.Bounds.Y - hit.Bounds.Height / 2) / (hit.Bounds.Height / 2);
                if (x * x + y * y > 1 || _shape == 5 && x * x + y * y < .40) continue;
                var angle = (Math.Atan2(y, x) * 180 / Math.PI + 720 - hit.Start) % 360;
                if (angle > hit.Sweep) continue;
            }
            description = hit.Description; break;
        }
        if (description == _hover) return; _hover = description; _toolTip.SetToolTip(this, description.Length == 0 ? null : description);
    }
    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = ""; _toolTip.SetToolTip(this, null); base.OnMouseLeave(e);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        using var background = new SolidBrush(UiTheme.Surface); e.Graphics.FillRectangle(background, ClientRectangle); _hits.Clear();
        if (_result is null) { Message(e.Graphics, "対象を設定し［グラフを作成］すると、取得済みの結果を表示します。"); return; }
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (_result.Axes.FirstOrDefault()?.Mode == MetricMode.Distribution) PaintDistribution(e.Graphics); else PaintTimeSeries(e.Graphics);
    }
    private void Message(Graphics graphics, string message)
        => TextRenderer.DrawText(graphics, message, Font, Rectangle.Inflate(ClientRectangle, -12, -12), UiTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
    private static string Percentage(long count, long total)
    {
        if (total <= 0) return "0%"; var value = 100m * count / total;
        return value is > 0 and < .1m ? "<0.1%" : value.ToString("0.#", CultureInfo.CurrentCulture) + "%";
    }
    private string SliceDescription(Slice slice, long total) => $"{slice.Label}: {slice.Count:N0}件 · {Percentage(slice.Count, total)}";
    private void PaintDistribution(Graphics graphics)
    {
        if (_result is null) return;
        if (_result.Axes.Count != 1 || _result.PointCount != _points.Count)
        { Message(graphics, "構成比の表示には、集計結果の全分類が必要です。結果を再取得してください。"); return; }
        if (_points.Count == 0) { Message(graphics, "条件に一致するデータがありません。"); return; }
        if (_points.Any(point => point.Axis != 0 || point.Count < 0 || point.MatchedCount < 0 || point.Value is not { } value || !double.IsFinite(value) || value < 0))
        { Message(graphics, "負の値・無効な値を含むため、構成比を描画できません。集計結果を確認してください。"); return; }
        var total = _points[0].Count; var matched = _points.Sum(point => (decimal)point.MatchedCount);
        if (_points.Any(point => point.Count != total) || matched > total)
        { Message(graphics, "分類の件数と全体の件数が一致しません。集計結果を確認してください。"); return; }
        if (total == 0) { Message(graphics, "条件に一致するデータがありません（0件）。"); return; }
        var slices = _points.Select(point => new Slice(point.Label, point.MatchedCount, SeriesColor(_result, 0, point.Label))).ToList();
        if (matched < total) slices.Add(new("表示対象外", total - (long)matched, UiTheme.IsDark ? Color.FromArgb(82, 89, 102) : Color.FromArgb(215, 221, 230), true));
        var scale = DeviceDpi / 96f; var margin = 14 * scale; var titleHeight = Font.Height + 10 * scale;
        var title = (_result.Axes[0].Name ?? "分類別アクセス") + $"   対象 {total:N0}件";
        TextRenderer.DrawText(graphics, title, Font, new Rectangle((int)margin, 5, Math.Max(1, Width - (int)(margin * 2)), Font.Height + 4), UiTheme.Text,
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
        var body = RectangleF.FromLTRB(margin, titleHeight + 5 * scale, Width - margin, Height - margin);
        if (body.Width < 100 * scale || body.Height < Font.Height + 18 * scale) return;
        if (_shape is 4 or 5) PaintPie(graphics, body, slices, total, scale); else PaintHorizontalBars(graphics, body, slices, total, scale);
    }
    private void PaintPie(Graphics graphics, RectangleF body, IReadOnlyList<Slice> slices, long total, float scale)
    {
        var compact = body.Height < 150 * scale && body.Width >= 300 * scale;
        var rowHeight = compact ? Font.Height + 2 * scale : Math.Max(Font.Height + 9 * scale, 25 * scale);
        var sideBySide = body.Width >= 660 * scale || compact;
        var columns = slices.Count > 10 ? 2 : 1; RectangleF circleArea, legend;
        if (sideBySide)
        {
            var chartWidth = Math.Min(body.Width * .43f, body.Height); circleArea = new(body.X, body.Y, chartWidth, body.Height);
            legend = RectangleF.FromLTRB(circleArea.Right + 16 * scale, body.Y, body.Right, body.Bottom);
            if (compact) columns = slices.Count > 4 && legend.Width >= 260 * scale ? 3 : legend.Width >= 190 * scale ? 2 : 1;
            else if (legend.Width < 360 * scale) columns = 1;
        }
        else
        {
            columns = body.Width >= 380 * scale || slices.Count > 4 && body.Width >= 280 * scale ? 2 : 1;
            var wantedLegend = MathF.Ceiling(slices.Count / (float)columns) * rowHeight + 2 * scale;
            var chartHeight = Math.Min(body.Width * .64f, Math.Max(90 * scale, body.Height - Math.Min(wantedLegend, body.Height * .57f) - 10 * scale));
            circleArea = new(body.X, body.Y, body.Width, chartHeight); legend = RectangleF.FromLTRB(body.X, circleArea.Bottom + 10 * scale, body.Right, body.Bottom);
        }
        var diameter = Math.Min(circleArea.Width, circleArea.Height);
        var circle = new RectangleF(circleArea.X + (circleArea.Width - diameter) / 2, circleArea.Y + (circleArea.Height - diameter) / 2, diameter, diameter); var angle = -90f;
        using var outline = new Pen(UiTheme.Surface, Math.Max(1, 1.2f * scale));
        foreach (var slice in slices)
        {
            if (slice.Count == 0) continue; var sweep = (float)(360m * slice.Count / total); using var brush = new SolidBrush(slice.Color);
            graphics.FillPie(brush, circle.X, circle.Y, circle.Width, circle.Height, angle, sweep); graphics.DrawPie(outline, circle.X, circle.Y, circle.Width, circle.Height, angle, sweep);
            _hits.Add(new(circle, SliceDescription(slice, total), (angle + 360) % 360, sweep)); angle += sweep;
        }
        if (_shape == 5)
        {
            var inner = RectangleF.Inflate(circle, -circle.Width * .185f, -circle.Height * .185f); using var surface = new SolidBrush(UiTheme.Surface); graphics.FillEllipse(surface, inner);
            if (!compact) TextRenderer.DrawText(graphics, $"{total:N0}\n件", Font, Rectangle.Round(inner), UiTheme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping);
        }
        PaintLegend(graphics, legend, slices, total, columns, rowHeight, scale, compact);
    }
    private void PaintLegend(Graphics graphics, RectangleF bounds, IReadOnlyList<Slice> slices, long total, int columns, float rowHeight, float scale, bool compact = false)
    {
        var rows = Math.Max(1, (int)(bounds.Height / rowHeight)); var capacity = rows * columns; var shown = Math.Min(slices.Count, capacity);
        if (shown < slices.Count) shown = Math.Max(0, shown - 1); var width = bounds.Width / columns;
        for (var i = 0; i < shown; i++)
        {
            var column = i / rows; var row = i % rows; var slice = slices[i]; var rect = new RectangleF(bounds.X + column * width, bounds.Y + row * rowHeight, width - 8 * scale, rowHeight);
            using var brush = new SolidBrush(slice.Color); graphics.FillRectangle(brush, rect.X, rect.Y + (rowHeight - 8 * scale) / 2, 8 * scale, 8 * scale); var description = SliceDescription(slice, total);
            var stats = compact ? Percentage(slice.Count, total) : $"{slice.Count:N0}件 · {Percentage(slice.Count, total)}";
            var statWidth = Math.Min(rect.Width * .56f, TextRenderer.MeasureText(stats, Font).Width + 4 * scale);
            var labelRect = RectangleF.FromLTRB(rect.X + 15 * scale, rect.Y, rect.Right - statWidth - 8 * scale, rect.Bottom);
            var statRect = RectangleF.FromLTRB(rect.Right - statWidth, rect.Y, rect.Right, rect.Bottom);
            TextRenderer.DrawText(graphics, slice.Label, Font, Rectangle.Round(labelRect), slice.Hidden ? UiTheme.Muted : UiTheme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
            TextRenderer.DrawText(graphics, stats, Font, Rectangle.Round(statRect), slice.Hidden ? UiTheme.Muted : UiTheme.Text,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping); _hits.Add(new(rect, description));
        }
        if (shown < slices.Count)
        {
            var slot = Math.Min(shown, Math.Max(0, capacity - 1)); var column = slot / rows; var row = slot % rows;
            var rect = new RectangleF(bounds.X + column * width, bounds.Y + row * rowHeight, width - 8 * scale, rowHeight); var description = string.Join("\n", slices.Skip(shown).Select(slice => SliceDescription(slice, total)));
            TextRenderer.DrawText(graphics, $"ほか {slices.Count - shown}分類 · 詳細は結果表", Font, Rectangle.Round(rect), UiTheme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping); _hits.Add(new(rect, description));
        }
    }
    private void PaintHorizontalBars(Graphics graphics, RectangleF body, IReadOnlyList<Slice> slices, long total, float scale)
    {
        var compact = body.Height < 150 * scale;
        var rowHeight = compact ? Font.Height + 6 * scale : Math.Max(Font.Height + 9 * scale, 25 * scale);
        var capacity = Math.Max(1, (int)((body.Height - Font.Height - (compact ? 6 : 12) * scale) / rowHeight));
        var shown = Math.Min(slices.Count, capacity); var showRemainder = shown < slices.Count && capacity > 1;
        if (showRemainder) shown--;
        var labelWidth = Math.Min(190 * scale, body.Width * .28f); var valueWidth = Math.Min(185 * scale, body.Width * .32f);
        var plotLeft = body.X + labelWidth + 10 * scale; var plotRight = body.Right - valueWidth; if (plotRight - plotLeft < 20 * scale) return;
        var baselineBottom = body.Y + (shown + (showRemainder ? 1 : 0)) * rowHeight; using var grid = new Pen(UiTheme.GridLine);
        for (var tick = 0; tick <= 2; tick++)
        {
            var x = plotLeft + (plotRight - plotLeft) * tick / 2; graphics.DrawLine(grid, x, body.Y, x, baselineBottom);
            var value = _result!.Axes[0].Measure == MetricMeasure.Rate ? $"{tick * 50}%" : ((decimal)total * tick / 2).ToString("0.#", CultureInfo.CurrentCulture) + "件";
            TextRenderer.DrawText(graphics, value, Font, new Rectangle((int)(x - 58 * scale), (int)(baselineBottom + 6 * scale), (int)(116 * scale), Font.Height + 2), UiTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping);
        }
        for (var i = 0; i < shown; i++)
        {
            var slice = slices[i]; var row = new RectangleF(body.X, body.Y + i * rowHeight, body.Width, rowHeight);
            TextRenderer.DrawText(graphics, slice.Label, Font, Rectangle.Round(new RectangleF(row.X, row.Y, labelWidth, rowHeight)), UiTheme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
            using var brush = new SolidBrush(slice.Color); var barHeight = Math.Max(4 * scale, rowHeight * .58f);
            graphics.FillRectangle(brush, plotLeft, row.Y + (rowHeight - barHeight) / 2, (float)((decimal)slice.Count / total) * (plotRight - plotLeft), barHeight);
            TextRenderer.DrawText(graphics, $"{slice.Count:N0}件 · {Percentage(slice.Count, total)}", Font, Rectangle.Round(RectangleF.FromLTRB(plotRight + 10 * scale, row.Y, body.Right, row.Bottom)), UiTheme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping); _hits.Add(new(row, SliceDescription(slice, total)));
        }
        if (showRemainder)
        {
            var rect = new RectangleF(body.X, body.Y + shown * rowHeight, body.Width, rowHeight);
            TextRenderer.DrawText(graphics, $"ほか {slices.Count - shown}分類 · 詳細は結果表", Font, Rectangle.Round(rect), UiTheme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping);
            _hits.Add(new(rect, string.Join("\n", slices.Skip(shown).Select(slice => SliceDescription(slice, total)))));
        }
    }
    private bool IsRate(int axis) => _result is not null && _result.Axes[axis].Mode != MetricMode.Raw && _result.Axes[axis].Measure == MetricMeasure.Rate;
    private double Scale(int axis, double value) => value * (IsRate(axis) ? 100 : 1);
    private string Unit(int axis) => IsRate(axis) ? "%" : _result!.Axes[axis].Mode != MetricMode.Raw || _result.Axes[axis].Aggregate == MetricAggregate.Count ? "件" : "値";
    private void PaintTimeSeries(Graphics graphics)
    {
        if (_result is null) return; var axisCount = Math.Min(_result.Axes.Count, _visibleAxes); var rows = _points.Where(point => point.Axis >= 0 && point.Axis < axisCount).ToArray();
        if (rows.Length == 0) { Message(graphics, "条件に一致するデータがありません。"); return; } var scale = DeviceDpi / 96f;
        var compact = Height < 175 * scale; var divisions = compact ? 2 : 4;
        var plot = RectangleF.FromLTRB((compact ? 64 : 80) * scale, compact ? Font.Height + 7 * scale : 43 * scale,
            Width - (axisCount > 1 ? compact ? 64 : 80 : 20) * scale, Height - (compact ? Font.Height + 10 * scale : 53 * scale));
        if (plot.Width < 50 || plot.Height < (compact ? 25 : 40)) return;
        var ranges = new (double Minimum, double Maximum)[axisCount];
        for (var axis = 0; axis < axisCount; axis++)
        {
            var data = rows.Where(point => point.Axis == axis && point.Value.HasValue).Select(point => Scale(axis, point.Value!.Value)).Where(double.IsFinite).ToList();
            if (_bands) foreach (var series in _result.SeriesSummaries.Where(series => series.Axis == axis))
            { if (series.BandLow is { } low && double.IsFinite(Scale(axis, low))) data.Add(Scale(axis, low)); if (series.BandHigh is { } high && double.IsFinite(Scale(axis, high))) data.Add(Scale(axis, high)); }
            var minimum = Math.Min(0, data.Count == 0 ? 0 : data.Min()); var maximum = Math.Max(0, data.Count == 0 ? 1 : data.Max()); if (maximum == minimum) maximum = minimum + 1; ranges[axis] = (minimum, maximum);
            var title = (axis == 0 ? "A · " : "B · ") + (_result.Axes[axis].Name ?? "系列") + $" ({Unit(axis)})";
            var rect = axis == 0 ? new Rectangle((int)plot.Left, 4, (int)plot.Width / axisCount, Font.Height * 2) : new Rectangle((int)(plot.Left + plot.Width / 2), 4, (int)plot.Width / 2, Font.Height * 2);
            var firstSeries = _result.SeriesSummaries.FirstOrDefault(series => series.Axis == axis);
            TextRenderer.DrawText(graphics, title, Font, rect, firstSeries is null ? UiTheme.Text : SeriesColor(_result, axis, firstSeries.Label), (axis == 0 ? TextFormatFlags.Left : TextFormatFlags.Right) | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping); _hits.Add(new(rect, title));
        }
        using var grid = new Pen(UiTheme.GridLine);
        for (var i = 0; i <= divisions; i++)
        {
            var y = plot.Bottom - plot.Height * i / divisions; graphics.DrawLine(grid, plot.Left, y, plot.Right, y);
            for (var axis = 0; axis < axisCount; axis++)
            {
                var value = ranges[axis].Minimum / divisions * (divisions - i) + ranges[axis].Maximum / divisions * i; var magnitude = Math.Abs(value);
                var text = value.ToString(magnitude >= 1e7 || magnitude is > 0 and < .001 ? "0.##E+0" : "0.###", CultureInfo.CurrentCulture) + (IsRate(axis) ? "%" : "");
                var rect = axis == 0 ? new Rectangle(0, (int)(y - Font.Height / 2f), (int)(plot.Left - 7 * scale), Font.Height + 2) : new Rectangle((int)(plot.Right + 7 * scale), (int)(y - Font.Height / 2f), Math.Max(1, Width - (int)(plot.Right + 7 * scale)), Font.Height + 2);
                TextRenderer.DrawText(graphics, text, Font, rect, UiTheme.Muted, (axis == 0 ? TextFormatFlags.Right : TextFormatFlags.Left) | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);
            }
        }
        var start = rows.Min(row => row.UnixMilliseconds); var end = rows.Max(row => row.UnixMilliseconds); var span = Math.Max(1, (double)end - start);
        var first = rows.First(row => row.UnixMilliseconds == start).Time; var last = rows.First(row => row.UnixMilliseconds == end).Time;
        void DrawTime(string text, Rectangle rectangle, TextFormatFlags alignment)
        {
            var parts = DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? compact ? new[] { date.ToString("HH:mm:ss", CultureInfo.InvariantCulture) } : new[] { date.ToString("HH:mm:ss", CultureInfo.InvariantCulture), date.ToString("yyyy-MM-dd zzz", CultureInfo.InvariantCulture) } : new[] { text };
            var clip = graphics.Save(); graphics.SetClip(new Rectangle(rectangle.X, rectangle.Y, rectangle.Width, Font.Height * parts.Length));
            try { foreach (var part in parts) { TextRenderer.DrawText(graphics, part, Font, rectangle, UiTheme.Muted, alignment | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping); rectangle.Y += Font.Height; } }
            finally { graphics.Restore(clip); } _hits.Add(new(new Rectangle(rectangle.X, rectangle.Y - Font.Height * parts.Length, rectangle.Width, Font.Height * parts.Length), text));
        }
        DrawTime(first, new Rectangle((int)plot.Left, (int)(plot.Bottom + 8 * scale), (int)plot.Width / 2 - 4, Font.Height), TextFormatFlags.Left);
        DrawTime(last, new Rectangle((int)(plot.Left + plot.Width / 2 + 4), (int)(plot.Bottom + 8 * scale), (int)plot.Width / 2 - 4, Font.Height), TextFormatFlags.Right);
        var groups = rows.GroupBy(point => (point.Axis, point.Label)).ToArray(); var distinctTimes = Math.Max(1, rows.Select(point => point.UnixMilliseconds).Distinct().Count());
        var barWidth = Math.Max(1, Math.Min(22 * scale, plot.Width / (distinctTimes * groups.Length + 1) * .8f)); var saved = graphics.Save(); graphics.SetClip(RectangleF.Inflate(plot, 2, 2));
        try
        {
            for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
            {
                var group = groups[groupIndex]; var axis = group.Key.Axis; var range = ranges[axis]; var color = SeriesColor(_result, axis, group.Key.Label);
                using var line = new Pen(color, Math.Max(1.5f, 1.6f * scale)) { DashStyle = axis == 0 ? DashStyle.Solid : DashStyle.Dash }; using var fill = new SolidBrush(Color.FromArgb(_shape == 2 ? 40 : 175, color));
                var magnitude = Math.Max(Math.Abs(range.Minimum), Math.Abs(range.Maximum)); var normalizedMinimum = range.Minimum / magnitude; var normalizedMaximum = range.Maximum / magnitude;
                float Y(double value) => plot.Bottom - (float)(Math.Clamp((value / magnitude - normalizedMinimum) / (normalizedMaximum - normalizedMinimum), 0, 1) * plot.Height);
                if (_bands)
                {
                    var series = _result.SeriesSummaries.FirstOrDefault(series => series.Axis == axis && series.Label == group.Key.Label);
                    if (series?.BandLow is { } low && series.BandHigh is { } high && double.IsFinite(Scale(axis, low)) && double.IsFinite(Scale(axis, high)))
                    {
                        using var band = new SolidBrush(Color.FromArgb(18, color)); var top = Y(Scale(axis, high)); var bottom = Y(Scale(axis, low)); graphics.FillRectangle(band, plot.Left, top, plot.Width, Math.Max(0, bottom - top));
                        using var boundary = new Pen(Color.FromArgb(140, color), 1) { DashStyle = DashStyle.Dot }; graphics.DrawLine(boundary, plot.Left, top, plot.Right, top); graphics.DrawLine(boundary, plot.Left, bottom, plot.Right, bottom);
                    }
                }
                var segment = new List<PointF>(); long? previousTime = null;
                void FinishSegment()
                {
                    if (segment.Count == 0) return; var points = segment.ToArray();
                    if (_shape == 2 && points.Length > 1)
                    {
                        using var path = new GraphicsPath(); path.AddLines(points); var zero = Y(0); path.AddLine(points[^1], new PointF(points[^1].X, zero)); path.AddLine(new PointF(points[^1].X, zero), new PointF(points[0].X, zero)); path.CloseFigure(); graphics.FillPath(fill, path);
                    }
                    if (_shape != 3 && points.Length > 1) graphics.DrawLines(line, points);
                    if (_shape == 3 || points.Length == 1) foreach (var point in points) graphics.FillEllipse(fill, point.X - 2 * scale, point.Y - 2 * scale, 4 * scale, 4 * scale); segment.Clear();
                }
                foreach (var point in group.OrderBy(point => point.UnixMilliseconds).ThenBy(point => point.RowKey))
                {
                    if (point.Value is not { } value || !double.IsFinite(Scale(axis, value))) { FinishSegment(); previousTime = null; continue; }
                    var bucketMilliseconds = (long)_result.Axes[axis].Bucket * 1000;
                    if (previousTime is { } previous && (_result.Axes[axis].Mode != MetricMode.Raw || _result.Axes[axis].Aggregate != MetricAggregate.Samples) && (double)point.UnixMilliseconds - previous > bucketMilliseconds) FinishSegment(); previousTime = point.UnixMilliseconds;
                    var inset = _shape == 1 ? Math.Min(plot.Width / 4, barWidth * groups.Length / 2) : 0;
                    var x = start == end ? plot.Left + plot.Width / 2 : plot.Left + inset + (float)((point.UnixMilliseconds - (double)start) / span * (plot.Width - inset * 2)); var y = Y(Scale(axis, value));
                    if (_shape == 1) { var zero = Y(0); graphics.FillRectangle(fill, x + (groupIndex - (groups.Length - 1) / 2f) * barWidth - barWidth / 2, Math.Min(y, zero), barWidth, Math.Abs(zero - y)); } else segment.Add(new(x, y));
                    _hits.Add(new(new RectangleF(x - 5 * scale, y - 5 * scale, 10 * scale, 10 * scale), $"{group.Key.Label}\n{point.Time}\n{value.ToString("G6", CultureInfo.CurrentCulture)}{(IsRate(axis) ? $" ({Scale(axis, value):0.##}%)" : "")} · 対象 {point.Count:N0}件"));
                    if (_outliers && point.OutlierCount > 0) { using var marker = new Pen(Color.FromArgb(210, 80, 80), Math.Max(1, scale)); graphics.DrawEllipse(marker, x - 4 * scale, y - 4 * scale, 8 * scale, 8 * scale); }
                }
                FinishSegment();
            }
        }
        finally { graphics.Restore(saved); }
    }
}
