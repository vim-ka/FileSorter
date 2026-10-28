namespace FileSorter.Models;

public class RoutingRule
{
    public bool IsEnabled { get; set; } = true;
    public List<FilterRule> Conditions { get; set; } = [];
    public bool MatchAll { get; set; } = true;
    public string Destination { get; set; } = "";
}
