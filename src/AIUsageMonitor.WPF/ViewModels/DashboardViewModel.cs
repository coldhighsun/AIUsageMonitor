using System.Windows.Threading;
using AIUsageMonitor.Core.Analytics;
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

    /// <summary>
    /// The clock that decides what "today" is for the date range.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// The day the date range was last aligned to, used to roll the range forward when midnight passes.
    /// </summary>
    private DateOnly _lastToday;

    /// <summary>
    /// Whether the date properties are being changed by the view model itself, in which case the single reload that follows is done by the caller.
    /// </summary>
    private bool _suppressReload;

    [ObservableProperty]
    private ISeries[] _dailyUsageSeries = [];

    [ObservableProperty]
    private Axis[] _dailyXAxes = [];

    [ObservableProperty]
    private Axis[] _dailyYAxes = [];

    [ObservableProperty]
    private DateTime _dateFrom;

    [ObservableProperty]
    private DateTime _dateTo;

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

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardViewModel"/> class, showing the last 30 days and refreshing every minute.
    /// </summary>
    /// <param name="dataService">The service that supplies the usage data.</param>
    /// <param name="timeProvider">The clock that decides what "today" is; defaults to the system clock.</param>
    public DashboardViewModel(DataService dataService, TimeProvider? timeProvider = null)
    {
        _dataService = dataService;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _lastToday = GetToday();
        _suppressReload = true;
        DateTo = _lastToday.ToDateTime(TimeOnly.MinValue);
        DateFrom = _lastToday.AddDays(-29).ToDateTime(TimeOnly.MinValue);
        _suppressReload = false;

        _timer = new() { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += (_, _) => OnTimerTick();
        _timer.Start();
        LoadData();
    }

    /// <summary>
    /// Handles one refresh tick: rolls the date range forward if a new day has started, then reloads the data once.
    /// </summary>
    internal void OnTimerTick()
    {
        AdvanceDateRange();
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
            DailyUsageSeries = [];
            DailyXAxes = [];
            DailyYAxes = [];

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
            HourlyActivitySeries = [];
            HourlyXAxes = [];
            HourlyYAxes = [];

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
            new() { Labeler = value => FormatTokens((long)value) }
        ];
    }

    private void BuildModelChart(List<ModelDistribution> models)
    {
        if (models.Count == 0)
        {
            ModelDistributionSeries = [];

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

    /// <summary>
    /// Gets today's date in the local time zone, according to the view model's clock.
    /// </summary>
    /// <returns>Today's date.</returns>
    private DateOnly GetToday()
    {
        return DateOnly.FromDateTime(_timeProvider.GetLocalNow().DateTime);
    }

    /// <summary>
    /// Rolls the date range forward when a new day has started and the range was ending on the previous day.
    /// </summary>
    private void AdvanceDateRange()
    {
        var today = GetToday();
        var (from, to) = RollingDateRange.Advance(
            DateOnly.FromDateTime(DateFrom), DateOnly.FromDateTime(DateTo), _lastToday, today);
        _lastToday = today;

        _suppressReload = true;
        try
        {
            DateFrom = from.ToDateTime(TimeOnly.MinValue);
            DateTo = to.ToDateTime(TimeOnly.MinValue);
        }
        finally
        {
            _suppressReload = false;
        }
    }

    partial void OnDateFromChanged(DateTime value)
    {
        if (!_suppressReload)
        {
            LoadData();
        }
    }

    partial void OnDateToChanged(DateTime value)
    {
        if (!_suppressReload)
        {
            LoadData();
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        LoadData();
    }
}
