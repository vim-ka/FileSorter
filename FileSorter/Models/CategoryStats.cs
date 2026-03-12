using System.Windows.Media;

namespace FileSorter.Models;

public class CategoryStats
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public Color Color { get; set; }
    public double Percentage { get; set; }
    public double StartAngle { get; set; }
    public double SweepAngle { get; set; }
    public bool IsSelected { get; set; } = true;
}
