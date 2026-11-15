using Avalonia.Controls;
using ScottPlot;
using ScottPlot.Avalonia;

namespace WinCAN.App.Views;

/// <summary>Simple time-series chart of one DBC signal (ScottPlot).</summary>
public sealed class GraphWindow : Window
{
    public GraphWindow(string title, IReadOnlyList<(double X, double Y)> points)
    {
        Title = "WinCAN Graph - " + title;
        Width = 900;
        Height = 520;

        var plot = new AvaPlot();
        var xs = points.Select(p => p.X).ToArray();
        var ys = points.Select(p => p.Y).ToArray();

        var scatter = plot.Plot.Add.Scatter(xs, ys);
        scatter.LegendText = title;
        plot.Plot.Title(title);
        plot.Plot.XLabel("Time (s)");
        plot.Plot.YLabel("Value");
        plot.Plot.ShowLegend();
        plot.Plot.Axes.AutoScale();
        plot.Refresh();

        Content = plot;
    }
}
