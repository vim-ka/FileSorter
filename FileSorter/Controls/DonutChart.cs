using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FileSorter.Models;

namespace FileSorter.Controls;

public class DonutChart : Control
{
    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(nameof(Items), typeof(ObservableCollection<CategoryStats>),
            typeof(DonutChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsChanged));

    public static readonly DependencyProperty TotalTextProperty =
        DependencyProperty.Register(nameof(TotalText), typeof(string),
            typeof(DonutChart), new FrameworkPropertyMetadata("0", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SubTextProperty =
        DependencyProperty.Register(nameof(SubText), typeof(string),
            typeof(DonutChart), new FrameworkPropertyMetadata("TOTAL ITEMS", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RulesActiveTextProperty =
        DependencyProperty.Register(nameof(RulesActiveText), typeof(string),
            typeof(DonutChart), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public ObservableCollection<CategoryStats>? Items
    {
        get => (ObservableCollection<CategoryStats>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public string TotalText
    {
        get => (string)GetValue(TotalTextProperty);
        set => SetValue(TotalTextProperty, value);
    }

    public string SubText
    {
        get => (string)GetValue(SubTextProperty);
        set => SetValue(SubTextProperty, value);
    }

    public string RulesActiveText
    {
        get => (string)GetValue(RulesActiveTextProperty);
        set => SetValue(RulesActiveTextProperty, value);
    }

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DonutChart chart)
        {
            if (e.OldValue is ObservableCollection<CategoryStats> oldCollection)
                oldCollection.CollectionChanged -= chart.OnCollectionChanged;
            if (e.NewValue is ObservableCollection<CategoryStats> newCollection)
                newCollection.CollectionChanged += chart.OnCollectionChanged;
            chart.InvalidateVisual();
        }
    }

    private void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }

    private Color GetThemeColor(string key, Color fallback)
    {
        try { return (Color)FindResource(key); }
        catch { return fallback; }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        var bgColor = GetThemeColor("PrimaryBackgroundColor", Color.FromRgb(30, 30, 46));
        var ringBgColor = GetThemeColor("TertiaryBackgroundColor", Color.FromRgb(60, 60, 80));
        var textColor = GetThemeColor("PrimaryTextColor", Color.FromRgb(224, 224, 224));
        var subTextColor = GetThemeColor("SecondaryTextColor", Color.FromRgb(160, 160, 184));
        var accentColor = GetThemeColor("AccentColor", Color.FromRgb(124, 111, 235));

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var outerRadius = size / 2 - 10;
        var innerRadius = outerRadius * 0.62;

        // Background ring
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(ringBgColor), outerRadius - innerRadius),
            center, (outerRadius + innerRadius) / 2, (outerRadius + innerRadius) / 2);

        if (Items == null || Items.Count == 0)
        {
            DrawCenterText(dc, center, textColor, subTextColor, accentColor);
            return;
        }

        double startAngle = -90;
        foreach (var item in Items)
        {
            if (item.SweepAngle <= 0) continue;

            var brush = new SolidColorBrush(item.Color);
            brush.Freeze();

            DrawArcSegment(dc, center, outerRadius, innerRadius, startAngle, item.SweepAngle, brush, bgColor);
            startAngle += item.SweepAngle;
        }

        // Inner circle (hole)
        dc.DrawEllipse(new SolidColorBrush(bgColor), null,
            center, innerRadius - 2, innerRadius - 2);

        DrawCenterText(dc, center, textColor, subTextColor, accentColor);
    }

    private void DrawCenterText(DrawingContext dc, Point center, Color textColor, Color subTextColor, Color accentColor)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var totalFt = new FormattedText(
            TotalText,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            32,
            new SolidColorBrush(textColor),
            dpi);

        dc.DrawText(totalFt, new Point(center.X - totalFt.Width / 2, center.Y - totalFt.Height / 2 - 14));

        var subFt = new FormattedText(
            SubText,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            11,
            new SolidColorBrush(subTextColor),
            dpi);

        dc.DrawText(subFt, new Point(center.X - subFt.Width / 2, center.Y - subFt.Height / 2 + 12));

        if (!string.IsNullOrEmpty(RulesActiveText))
        {
            var rulesFt = new FormattedText(
                RulesActiveText,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                10,
                new SolidColorBrush(accentColor),
                dpi);

            dc.DrawText(rulesFt, new Point(center.X - rulesFt.Width / 2, center.Y - rulesFt.Height / 2 + 28));
        }
    }

    private static void DrawArcSegment(DrawingContext dc, Point center,
        double outerRadius, double innerRadius, double startAngle, double sweepAngle, Brush brush, Color gapColor)
    {
        if (sweepAngle >= 360)
            sweepAngle = 359.99;

        var startRad = startAngle * Math.PI / 180;
        var endRad = (startAngle + sweepAngle) * Math.PI / 180;

        var outerStart = new Point(center.X + outerRadius * Math.Cos(startRad), center.Y + outerRadius * Math.Sin(startRad));
        var outerEnd = new Point(center.X + outerRadius * Math.Cos(endRad), center.Y + outerRadius * Math.Sin(endRad));
        var innerStart = new Point(center.X + innerRadius * Math.Cos(endRad), center.Y + innerRadius * Math.Sin(endRad));
        var innerEnd = new Point(center.X + innerRadius * Math.Cos(startRad), center.Y + innerRadius * Math.Sin(startRad));

        var isLargeArc = sweepAngle > 180;

        var figure = new PathFigure { StartPoint = outerStart, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment(outerEnd, new Size(outerRadius, outerRadius), 0, isLargeArc, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(innerStart, true));
        figure.Segments.Add(new ArcSegment(innerEnd, new Size(innerRadius, innerRadius), 0, isLargeArc, SweepDirection.Counterclockwise, true));

        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        geo.Freeze();

        dc.DrawGeometry(brush, new Pen(new SolidColorBrush(gapColor), 2), geo);
    }
}
