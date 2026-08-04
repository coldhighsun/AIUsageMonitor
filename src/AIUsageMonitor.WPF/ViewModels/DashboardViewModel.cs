using System.Windows.Threading;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace AIUsageMonitor.WPF.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly DataService _dataService;
    private readonly DispatcherTimer _timer;

    [ObservableProperty]
    private ISeries[] _dailyUsageSeries = [];

    [ObservableProperty]
    private Axis[] _dailyXAxes = [];

    [ObservableProperty]
    private Axis[] _dailyYAxes = [];

    [ObservableProperty]
    private DateTime _dateFrom = DateTime.Today.AddDays(-29);

    [ObservableProperty]
    private DateTime _dateTo = DateTime.Today;

    [ObservableProperty]
    private string _estimatedCost = "$0.00";

    [ObservableProperty]
    private ISeries[] _hourlyActivitySeries = [];

    [ObservableProperty]
    private Axis[] _hourlyXAxes = [];

    [ObservableProperty]
    private Axis[] _hourlyYAxes = [];

    [ObservableProperty]
    private ISeries[] _modelDistributionSeries = [];

    [ObservableProperty]
    private string _totalMessages = "0";

    [ObservableProperty]
    private string _totalSessions = "0";

    [ObservableProperty]
    private string _totalTokens = "0";
    public DashboardViewModel(DataService dataService)
    {
        _dataService = dataService;
        _timer = new() { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += (_, _) => LoadData();
        _timer.Start();
        LoadData();
    }

    private static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000_000 => $"{tokens / 1_000_000_000.0:F2}B",
        >= 1_000_000 => $"{tokens / 1_000_000.0:F2}M",
        >= 1_000 => $"{tokens / 1_000.0:F1}K",
        _ => tokens.ToString("N0")
    };

    private void BuildDailyChart(PeriodSummary period)
    {
        if (period.DailyBreakdown.Count == 0)
        {
            return;
        }

        var tokenValues = period.DailyBreakdown
            .Select(d => new DateTimePoint(d.Date.ToDateTime(TimeOnly.MinValue), d.TotalTokens / 1_000_000.0))
            .ToArray();

        DailyUsageSeries =
        [
            new LineSeries<DateTimePoint>
            {
                Values = tokenValues,
                Name = "Tokens (M)",
                GeometrySize = 6,
                Stroke = new SolidColorPaint(SKColors.OrangeRed) { StrokeThickness = 2 },
                GeometryStroke = new SolidColorPaint(SKColors.OrangeRed) { StrokeThickness = 2 },
                Fill = null,
                DataLabelsPaint = new SolidColorPaint(SKColors.OrangeRed),
                DataLabelsSize = 12,
                DataLabelsPosition = DataLabelsPosition.Top,
                DataLabelsFormatter = point => FormatTokens((long)(point.Coordinate.PrimaryValue * 1_000_000))
            }
        ];

        DailyXAxes =
        [
            new DateTimeAxis(TimeSpan.FromDays(1), date => date.ToString("MM/dd"))
        ];

        DailyYAxes =
        [
            new Axis { Labeler = value => FormatTokens((long)(value * 1_000_000)) }
        ];
    }

    private void BuildHourlyChart(List<HourlyActivity> hours)
    {
        if (hours.Count == 0)
        {
            return;
        }

        HourlyActivitySeries =
        [
            new ColumnSeries<long>
            {
                Values = hours.Select(h => h.TotalTokens).ToArray(),
                Name = "Tokens",
                Fill = new SolidColorPaint(SKColors.DodgerBlue)
            }
        ];

        HourlyXAxes =
        [
            new()
            {
                Labels = hours.Select(h => $"{h.Hour:D2}:00").ToArray(),
                LabelsRotation = 45
            }
        ];

        HourlyYAxes =
        [
            new() { Labeler = value => value.ToString("N0") }
        ];
    }

    private void BuildModelChart(List<ModelDistribution> models)
    {
        if (models.Count == 0)
        {
            return;
        }

        var colors = new[]
        {
            SKColors.DodgerBlue, SKColors.OrangeRed, SKColors.MediumSeaGreen,
            SKColors.MediumPurple, SKColors.Gold, SKColors.Coral, SKColors.Cyan
        };

        var series = new List<ISeries>();
        for (var i = 0; i < models.Count; i++)
        {
            var m = models[i];
            series.Add(new PieSeries<double>
            {
                Values = [m.Percentage],
                Name = m.ModelName,
                Fill = new SolidColorPaint(colors[i % colors.Length]),
                DataLabelsPaint = new SolidColorPaint(SKColors.White),
                DataLabelsSize = 12,
                DataLabelsFormatter = point => $"{point.Coordinate.PrimaryValue:F2}%",
                ToolTipLabelFormatter = point => $"{point.Coordinate.PrimaryValue:F2}%"
            });
        }

        ModelDistributionSeries = series.ToArray();
    }

    private void LoadData()
    {
        try
        {
            var from = DateOnly.FromDateTime(DateFrom);
            var to = DateOnly.FromDateTime(DateTo);
            var period = _dataService.GetPeriodSummary(from, to);
            var models = _dataService.GetModelDistribution();
            var hours = _dataService.GetHourlyActivity();

            TotalTokens = FormatTokens(period.TotalTokens);
            TotalSessions = $"{period.TotalSessions:N0}";
            TotalMessages = $"{period.TotalMessages:N0}";
            EstimatedCost = $"${period.EstimatedCost:N2}";

            BuildDailyChart(period);
            BuildModelChart(models);
            BuildHourlyChart(hours);
        }
        catch
        {
            // Data may not be available yet
        }
    }

    partial void OnDateFromChanged(DateTime value) => LoadData();

    partial void OnDateToChanged(DateTime value) => LoadData();

    [RelayCommand]
    private void Refresh()
    {
        LoadData();
    }
}
