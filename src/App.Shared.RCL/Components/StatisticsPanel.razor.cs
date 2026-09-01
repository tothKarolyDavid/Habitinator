#pragma warning disable S3881 // Dispose is implemented in the generated Razor part
#pragma warning disable S1144, S4487, IDE0051, IDE0052
using System.Globalization;

using App.Shared.RCL.Components.Dialogs;
using App.Shared.RCL.Models;
using App.Shared.RCL.Services;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

using MudBlazor;

namespace App.Shared.RCL.Components;

public partial class StatisticsPanel : IDisposable
{
    private readonly Dictionary<Guid, Dictionary<(int Row, int Col), ActivityHeatmapCellDto>> _dailyCellIndices = [];
    private readonly Dictionary<Guid, Dictionary<(int Row, int Col), ActivityHeatmapCellDto>> _habitCellIndices = [];

    private PersistingComponentStateSubscription _subscription;
    private ActivityDashboardDto? _data;
    private IReadOnlyList<DailyGraphPeriodOption>? _periodOptions;
    private Dictionary<(int R, int C), ActivityHeatmapCellDto> _cellIndex = [];

    private int _weekBarMax;
    private bool _loading = true;
    private bool _periodBusy;
    private string? _error;

    private DailyContributionsViewDto? _dailyView;
    private HabitContributionsViewDto? _habitView;
    private string _selectedPeriodKey = DailyGraphPeriods.Rolling370Days;
    private string _tagFilter = "";
    private IReadOnlyCollection<string> _selectedTags = Array.Empty<string>();
    private bool _loadingDailies;
    private string? _dailyError;
    private bool _loadingHabits;
    private string? _habitError;
    private int _bestStreakDays;
    private string? _bestStreakTitle;
    private WeeklyReview? _weeklyReview;
    private bool _shouldScrollToEnd;
    private DateOnly _heatmapToday;

    [Inject] public IServiceProvider ServiceProvider { get; set; } = default!;

    private OfflineActivityStatisticsProvider? OfflineStats => ServiceProvider.GetService<OfflineActivityStatisticsProvider>();

    private sealed record WeeklyReview(
        DateOnly WeekStart,
        DateOnly WeekEnd,
        int Events,
        int FocusMinutes,
        int DailiesCompleted,
        int PercentChange);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_shouldScrollToEnd)
        {
            _shouldScrollToEnd = false;
            try
            {
                await JSRuntime.InvokeVoidAsync("scrollHeatmapsToEnd");
                await JSRuntime.InvokeVoidAsync("initializeHeatmapRovingTabindex");
            }
            catch (Exception)
            {
                // Ignore JS errors if components disappear or JS isn't ready
            }
        }
    }

    protected override async Task OnInitializedAsync()
    {
        _subscription = ApplicationState.RegisterOnPersisting(PersistStatisticsData);
        _heatmapToday = DailySchedule.LocalToday(TimeZoneService);
        if (TryRestoreInitialOverview())
        {
            _loading = false;
            _loadingDailies = false;
            _loadingHabits = false;
        }

        try
        {
            await DateFormatService.InitializeAsync();
            await LoadStatisticsAsync(_selectedPeriodKey);
        }
        catch (Exception ex)
        {
            await HandleInitErrorAsync(ex);
        }
        finally
        {
            _loadingDailies = false;
            _loadingHabits = false;
            _loading = false;
        }
    }

    private bool TryRestoreInitialOverview()
    {
        if (ApplicationState.TryTakeFromJson<ActivityOverviewDto>("stats_overview_data", out var restoredOverview) && restoredOverview is not null)
        {
            ApplyOverview(restoredOverview);
            return true;
        }

        if (Stats.TryGetCachedOverview(_selectedPeriodKey, string.IsNullOrEmpty(_tagFilter) ? null : _tagFilter, out var cached) && cached is not null)
        {
            ApplyOverview(cached);
            return true;
        }

        return false;
    }

    private async Task HandleInitErrorAsync(Exception ex)
    {
        if (_data != null)
        {
            await SafeNotifyAsync("Offline: showing last available stats.", Severity.Warning);
            return;
        }

        if (await TryApplyOfflineOverviewAsync(_selectedPeriodKey, _tagFilter))
        {
            await SafeNotifyAsync("Offline: showing locally computed stats.", Severity.Warning);
            return;
        }

        _error = ex.Message;
        await SafeNotifyAsync("Could not load statistics. Please try again.", Severity.Error);
    }

    private async Task<bool> TryApplyOfflineOverviewAsync(string periodKey, string? tagFilter, Task<Dictionary<Guid, int>>? streaksTask = null)
    {
        if (OfflineStats == null)
        {
            return false;
        }

        try
        {
            var tag = string.IsNullOrEmpty(tagFilter) ? null : tagFilter;
            var fallback = await OfflineStats.BuildOverviewAsync(periodKey, tag);
            ApplyOverview(fallback);
            if (streaksTask != null)
            {
                await ApplyBestStreakAsync(streaksTask);
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task SafeNotifyAsync(string message, Severity severity)
    {
        try
        {
            await Notifier.NotifyAsync(message, severity);
        }
        catch (Exception notifyEx)
        {
            // Ignore - best effort toast, fallback already rendered
            _ = notifyEx;
        }
    }

    private Task PersistStatisticsData()
    {
        if (_data is not null && _dailyView is not null && _habitView is not null)
        {
            var overview = new ActivityOverviewDto(_data, _dailyView, _habitView);
            ApplicationState.PersistAsJson("stats_overview_data", overview);
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ApplyOverview(ActivityOverviewDto overview)
    {
        _data = overview.Dashboard;
        _dailyView = overview.DailyContributions;
        _habitView = overview.HabitContributions;

        _selectedPeriodKey = _data.PeriodKey;
        var tagList = _data.AvailableTags ?? [];

        if (!string.IsNullOrEmpty(_tagFilter))
        {
            var splitFilter = _tagFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var validTags = splitFilter.Where(t => tagList.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
            _selectedTags = validTags;
            _tagFilter = string.Join(",", _selectedTags);
        }
        else
        {
            _selectedTags = Array.Empty<string>();
            _tagFilter = "";
        }

        _periodOptions = _dailyView.PeriodOptions;
        _cellIndex = _data.Heatmap.ToDictionary(x => (x.DayRow, x.WeekCol));
        _dailyCellIndices.Clear();
        _habitCellIndices.Clear();
        _weekBarMax = _data.WeekBars.Count == 0
            ? 0
            : _data.WeekBars.Max(x => x.EventCount);
        _shouldScrollToEnd = true;
        ComputeWeeklyReview();
    }

    private async Task LoadStatisticsAsync(string periodKey)
    {
        _heatmapToday = DailySchedule.LocalToday(TimeZoneService);
        _loadingDailies = true;
        _dailyError = null;
        _loadingHabits = true;
        _habitError = null;
        var tag = string.IsNullOrEmpty(_tagFilter) ? null : _tagFilter;
        var streaksTask = BoardData.GetStreakMapAsync();
        try
        {
            var overview = await ResolveOverviewAsync(periodKey, tag);
            ApplyOverview(overview);
            await ApplyBestStreakAsync(streaksTask);
        }
        catch (Exception dex)
        {
            await HandleLoadErrorAsync(dex, periodKey, tag, streaksTask);
        }
        finally
        {
            _loadingDailies = false;
            _loadingHabits = false;
        }
    }

    private async Task<ActivityOverviewDto> ResolveOverviewAsync(string periodKey, string? tag)
    {
        if (ApplicationState.TryTakeFromJson<ActivityOverviewDto>("stats_overview_data", out var restoredOverview) && restoredOverview is not null)
        {
            return restoredOverview;
        }

        if (ApplicationState.TryTakeFromJson<ActivityDashboardDto>("stats_dashboard_data", out var d) &&
            ApplicationState.TryTakeFromJson<DailyContributionsViewDto>("stats_daily_view_data", out var dv) &&
            ApplicationState.TryTakeFromJson<HabitContributionsViewDto>("stats_habit_view_data", out var hv) &&
            d is not null && dv is not null && hv is not null)
        {
            return new ActivityOverviewDto(d, dv, hv);
        }

        return await Stats.GetOverviewAsync(periodKey, tag);
    }

    private async Task HandleLoadErrorAsync(Exception dex, string periodKey, string? tag, Task<Dictionary<Guid, int>> streaksTask)
    {
        if (await TryApplyOfflineOverviewAsync(periodKey, tag, streaksTask))
        {
            await SafeNotifyAsync("Offline: showing locally computed stats.", Severity.Warning);
            return;
        }

        if (_data != null)
        {
            await SafeNotifyAsync("Could not refresh statistics. Showing last available data.", Severity.Warning);
            return;
        }

        _error = dex.Message;
        _dailyError = dex.Message;
        _habitError = dex.Message;
        _data = null;
        _dailyView = null;
        _habitView = null;
        _periodOptions = null;
        _cellIndex = [];
        _dailyCellIndices.Clear();
        _habitCellIndices.Clear();
        await SafeNotifyAsync("Could not load statistics. Please try again.", Severity.Error);
    }

    private Dictionary<(int Row, int Col), ActivityHeatmapCellDto> GetDailyCellIndex(DailyContributionGraphDto daily)
    {
        if (_dailyCellIndices.TryGetValue(daily.BoardItemId, out var map))
        {
            return map;
        }

        map = daily.Heatmap.ToDictionary(x => (x.DayRow, x.WeekCol));
        _dailyCellIndices[daily.BoardItemId] = map;
        return map;
    }

    private Dictionary<(int Row, int Col), ActivityHeatmapCellDto> GetHabitCellIndex(HabitContributionGraphDto habit)
    {
        if (_habitCellIndices.TryGetValue(habit.BoardItemId, out var map))
        {
            return map;
        }

        map = habit.Heatmap.ToDictionary(x => (x.DayRow, x.WeekCol));
        _habitCellIndices[habit.BoardItemId] = map;
        return map;
    }

    private void ComputeWeeklyReview()
    {
        if (_data is null)
        {
            _weeklyReview = null;
            return;
        }

        var weekStart = _heatmapToday.AddDays(-(((int)_heatmapToday.DayOfWeek + 6) % 7));
        var weekEnd = _heatmapToday;

        var (events, prevEvents) = CountHeatmapEvents(_data.Heatmap, weekStart, weekEnd);

        var focusMinutes = _data.WeekBars
            .Where(w => w.WeekStart == weekStart)
            .Select(w => w.FocusMinutes)
            .DefaultIfEmpty(0)
            .Sum();

        var dailiesCompleted = CountCompletedDailies(_dailyView, weekStart, weekEnd);
        var percentChange = CalculatePercentChange(events, prevEvents);

        _weeklyReview = new WeeklyReview(weekStart, weekEnd, events, focusMinutes, dailiesCompleted, percentChange);
    }

    private static (int Events, int PrevEvents) CountHeatmapEvents(IEnumerable<ActivityHeatmapCellDto> cells, DateOnly weekStart, DateOnly weekEnd)
    {
        var events = 0;
        var prevEvents = 0;
        var prevWeekStart = weekStart.AddDays(-7);

        foreach (var cell in cells)
        {
            if (!cell.InDataRange)
            {
                continue;
            }

            if (cell.Date >= weekStart && cell.Date <= weekEnd)
            {
                events += cell.Count;
            }
            else if (cell.Date >= prevWeekStart && cell.Date < weekStart)
            {
                prevEvents += cell.Count;
            }
        }

        return (events, prevEvents);
    }

    private static int CountCompletedDailies(DailyContributionsViewDto? dailyView, DateOnly weekStart, DateOnly weekEnd)
    {
        if (dailyView is null)
        {
            return 0;
        }

        var dailiesCompleted = 0;
        foreach (var graph in dailyView.Graphs)
        {
            foreach (var cell in graph.Heatmap)
            {
                if (cell.InDataRange && cell.Count > 0 && cell.Date >= weekStart && cell.Date <= weekEnd)
                {
                    dailiesCompleted++;
                }
            }
        }

        return dailiesCompleted;
    }

    private static int CalculatePercentChange(int events, int prevEvents)
    {
        if (prevEvents > 0)
        {
            return (int)Math.Round(100.0 * (events - prevEvents) / prevEvents);
        }

        return events > 0 ? 100 : 0;
    }

    private async Task ApplyBestStreakAsync(Task<Dictionary<Guid, int>> streaksTask)
    {
        try
        {
            var streaks = await streaksTask;
            _bestStreakDays = 0;
            _bestStreakTitle = null;
            if (_dailyView is not null)
            {
                foreach (var graph in _dailyView.Graphs)
                {
                    if (streaks.TryGetValue(graph.BoardItemId, out var streak) && streak > _bestStreakDays)
                    {
                        _bestStreakDays = streak;
                        _bestStreakTitle = graph.Title;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Ignored. The KPI simply stays empty
        }
    }

    private Dictionary<string, object> GetConsistencyTabAttrs(string tab) => new()
    {
        ["aria-selected"] = _consistencyTab == tab ? "true" : "false"
    };

    private static string HabitActiveRatioLabel(int activeDays, int periodDays)
    {
        var percent = periodDays <= 0 ? 0 : (int)Math.Round(100.0 * activeDays / periodDays);
        return $"{activeDays} of {periodDays} days · {percent}%";
    }

    private static string DailyActiveRatioLabel(DailyContributionGraphDto daily)
    {
        var active = daily.Heatmap.Count(c => c.InDataRange && c.Count > 0);
        var total = daily.Heatmap.Count(c => c.InDataRange);
        var percent = total <= 0 ? 0 : (int)Math.Round(100.0 * active / total);
        return $"{active} of {total} days · {percent}%";
    }

    private static string DailyActiveRatioTooltip(DailyContributionGraphDto daily)
    {
        var active = daily.Heatmap.Count(c => c.InDataRange && c.Count > 0);
        var total = daily.Heatmap.Count(c => c.InDataRange);
        var label = active == 1 ? "day" : "days";
        return $"{active} active {label} of {total} in this period";
    }

    private string ActivityHeatmapCellClass(ActivityHeatmapCellDto cell)
    {
        return $"stats-heatmap-day-btn stats-cell stats-lvl-{cell.Intensity}{(IsHeatmapToday(cell.Date) ? " stats-heatmap-day--today" : "")}";
    }

    private string DailyHeatmapCellClass(ActivityHeatmapCellDto cell)
    {
        return $"stats-cell stats-lvl-{cell.Intensity}{(IsHeatmapToday(cell.Date) ? " stats-heatmap-day--today" : "")}";
    }

    private string HabitHeatmapCellClass(ActivityHeatmapCellDto cell)
    {
        return $"stats-cell stats-lvl-{cell.Intensity}{(IsHeatmapToday(cell.Date) ? " stats-heatmap-day--today" : "")}";
    }

    private string TitleWithTodayPrefix(DateOnly date, string baseTitle)
    {
        return IsHeatmapToday(date) ? $"Today · {baseTitle}" : baseTitle;
    }

    private string HabitHeatmapDayTitle(DateOnly date, int count)
    {
        var logs = count == 1 ? "1 log" : $"{count} logs";
        var baseTitle = $"{DateFormatService.Format(date)}: {logs} - view details";
        return TitleWithTodayPrefix(date, baseTitle);
    }

    private async Task OnStatsPeriodChanged(string? periodKey)
    {
        var newPeriod = string.IsNullOrEmpty(periodKey) ? DailyGraphPeriods.Rolling370Days : periodKey;
        if (newPeriod == _selectedPeriodKey)
        {
            return;
        }

        _selectedPeriodKey = newPeriod;
        await RefreshAsync(() => LoadStatisticsAsync(_selectedPeriodKey));
    }

    private string FormatBusiestDayShort(DateOnly day) => DateFormatService.Format(day);

    private static string GetMultiSelectionText(IReadOnlyList<string> selectedValues)
    {
        if (selectedValues is null || selectedValues.Count == 0)
        {
            return "All tags";
        }

        if (selectedValues.Count == 1)
        {
            return selectedValues[0];
        }

        if (selectedValues.Count == 2)
        {
            return $"{selectedValues[0]}, {selectedValues[1]}";
        }

        return $"{selectedValues.Count} tags selected";
    }

    private async Task OnSelectedTagsChanged(IReadOnlyCollection<string> values)
    {
        var newTags = values ?? Array.Empty<string>();
        if (newTags.Count == _selectedTags.Count && newTags.All(_selectedTags.Contains))
        {
            return;
        }

        _selectedTags = newTags;
        _tagFilter = string.Join(",", _selectedTags);
        await RefreshAsync(() => LoadStatisticsAsync(_selectedPeriodKey));
    }

    private async Task OnRemoveTagFilterAsync(string tag)
    {
        var newTags = _selectedTags.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).ToList();
        await OnSelectedTagsChanged(newTags);
    }

    private async Task OnClearTagFiltersAsync()
    {
        await OnSelectedTagsChanged(Array.Empty<string>());
    }

    private async Task RefreshAsync(Func<Task> load)
    {
        if (_loading)
        {
            return;
        }

        _periodBusy = true;
        _error = null;
        _dailyError = null;
        try
        {
            await load();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            try
            {
                await Notifier.NotifyAsync("Could not refresh statistics. Please try again.", Severity.Error);
            }
            catch (Exception notifyEx)
            {
                // Ignore - best effort toast, fallback already rendered
                _ = notifyEx;
            }
        }
        finally
        {
            _periodBusy = false;
        }
    }

    private async Task OpenDayDetailAsync(DateOnly date)
    {
        var options = new DialogOptions
        {
            MaxWidth = MaxWidth.Medium,
            FullWidth = true,
            CloseOnEscapeKey = true,
            CloseButton = true
        };
        var filterTag = string.IsNullOrEmpty(_tagFilter) ? null : _tagFilter;
        var parameters = new DialogParameters<ActivityDayDetailDialog>
        {
            { x => x.Date, date },
            { x => x.TagFilter, filterTag }
        };
        await DialogService.ShowAsync<ActivityDayDetailDialog>(FormatDateWithWeekday(date), parameters, options);
    }

    private static string FormatBusiestDayDetail(DateOnly day, int eventCount)
    {
        var weekday = day.ToString("dddd", CultureInfo.InvariantCulture);
        var eventsLabel = eventCount == 1 ? "1 event" : $"{eventCount} events";
        return $"{weekday} · {eventsLabel}";
    }

    private static string FormatFocus(int totalMinutes)
    {
        if (totalMinutes <= 0)
        {
            return "-";
        }

        if (totalMinutes < 60)
        {
            return $"{totalMinutes} min";
        }

        return $"{totalMinutes / 60}h {totalMinutes % 60}m";
    }

    private string FormatRange(DateOnly from, DateOnly to) => $"{DateFormatService.Format(from)} - {DateFormatService.Format(to)}";

    private static string GetWeeklyDeltaClass(int percentChange) =>
        percentChange >= 0 ? "stats-weekly-tile__delta stats-weekly-tile__delta--up" : "stats-weekly-tile__delta stats-weekly-tile__delta--down";

    private string FormatWeekBarTooltip(ActivityWeekBarDto w) =>
        $"{DateFormatService.Format(w.WeekStart)} week: {w.EventCount} events, {w.FocusMinutes} min focus";

    private static string FormatHabitRatioTooltip(int activeDayCount, int periodDayCount)
    {
        var label = activeDayCount == 1 ? "day" : "days";
        return $"{activeDayCount} active {label} of {periodDayCount} in this period";
    }

    private string FormatDateWithWeekday(DateOnly date)
    {
        var formatted = DateFormatService.Format(date);
        var weekday = date.ToString("dddd", CultureInfo.InvariantCulture);
        return $"{formatted} ({weekday})";
    }

    private bool IsHeatmapToday(DateOnly date) => date == _heatmapToday;

    private string HeatmapDayTitle(DateOnly date, int count)
    {
        var baseTitle = $"{DateFormatService.Format(date)}: {count} events - view details";
        return TitleWithTodayPrefix(date, baseTitle);
    }

    private string DailyHeatmapDayTitle(DateOnly date, int count)
    {
        var status = count > 0 ? $"{count} complete(s)" : "not completed";
        var baseTitle = $"{DateFormatService.Format(date)}: {status}";
        return TitleWithTodayPrefix(date, baseTitle);
    }

    private sealed record KpiDescriptor(
        string StripAccent,
        string CardAccent,
        string Icon,
        string StripLabel,
        string CardLabel,
        Func<string?> StripTitle,
        Func<string> StripValue,
        string StripValueClass,
        Func<string> CardValue,
        Func<string> CardDetail);

    private KpiDescriptor[] Kpis(ActivityDashboardDto data) =>
    [
        CreateEventsKpi(data),
        CreateFocusKpi(data),
        CreatePeakKpi(data),
        CreateStreakKpi()
    ];

    private static KpiDescriptor CreateEventsKpi(ActivityDashboardDto data)
    {
        var count = data.TotalEvents.ToString(CultureInfo.InvariantCulture);
        return new KpiDescriptor(
            "stats-kpi-strip__item--events",
            "stats-kpi-card--events",
            Icons.Material.Filled.ViewTimeline,
            "Events",
            "Total events",
            () => null,
            () => count,
            "",
            () => count,
            () => "Logged in this period");
    }

    private static KpiDescriptor CreateFocusKpi(ActivityDashboardDto data)
    {
        var formatted = FormatFocus(data.TotalFocusMinutes);
        return new KpiDescriptor(
            "stats-kpi-strip__item--focus",
            "stats-kpi-card--focus",
            Icons.Material.Filled.Timer,
            "Focus",
            "Focus time",
            () => null,
            () => formatted,
            "",
            () => formatted,
            () => "From focus timer sessions");
    }

    private KpiDescriptor CreatePeakKpi(ActivityDashboardDto data)
    {
        var busiestDay = data.BusiestDay.GetValueOrDefault();
        var hasPeak = data.BusiestDay.HasValue && data.MaxDayCount > 0;
        return new KpiDescriptor(
            "stats-kpi-strip__item--peak",
            "stats-kpi-card--peak",
            Icons.Material.Filled.TrendingUp,
            "Busiest",
            "Busiest day",
            () => hasPeak ? FormatBusiestDayDetail(busiestDay, data.MaxDayCount) : null,
            () => hasPeak ? FormatBusiestDayShort(busiestDay) : "-",
            "stats-kpi-strip__value--date",
            () => hasPeak ? DateFormatService.Format(busiestDay) : "-",
            () => hasPeak ? FormatBusiestDayDetail(busiestDay, data.MaxDayCount) : "No activity in range");
    }

    private KpiDescriptor CreateStreakKpi()
    {
        var hasStreak = _bestStreakDays > 0;
        var streakDays = _bestStreakDays;
        var streakTitle = _bestStreakTitle;

        return new KpiDescriptor(
            "stats-kpi-strip__item--streak",
            "stats-kpi-card--streak",
            Icons.Material.Filled.Whatshot,
            "Streak",
            "Longest streak",
            () => hasStreak ? streakTitle : null,
            () => hasStreak ? $"{streakDays} d" : "-",
            "",
            () => hasStreak ? $"{streakDays} days" : "-",
            () => hasStreak ? streakTitle ?? "" : "No active streaks yet");
    }

}
