using App.Shared.RCL.Components.Dialogs;
using App.Shared.RCL.Models;
using App.Shared.RCL.Services;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

using MudBlazor;

namespace App.Shared.RCL.Components;

public partial class MainBoard : IAsyncDisposable
{
    private List<BoardItem> Habits { get; set; } = [];
    private List<BoardItem> Dailies { get; set; } = [];
    private List<BoardItem> Todos { get; set; } = [];
    private List<BoardItem> _filteredHabits = [];
    private List<BoardItem> _filteredDailies = [];
    private List<BoardItem> _filteredTodos = [];
    private bool _isLoading;
    private bool _initialLoadComplete;
    private string? _loadError;
    private string _searchText = string.Empty;
    private readonly HashSet<string> _selectedFilterTags = [];
    private bool _dailyRetroSessionOffered;
    private bool _dailyRetroClientReady;
    private bool _onboardingOffered;
    private bool _boardClientScriptsStarted;
    private bool _shortcutsEnabled = true;
    private DotNetObjectReference<BoardRemoteNotifyBridge>? _visibilityRef;
    private DotNetObjectReference<MainBoard>? _selfRef;
    private DateTimeOffset _lastLocalMutationTime = DateTimeOffset.MinValue;
    private PersistingComponentStateSubscription _subscription;

    private List<BoardItem> FilteredHabits => _filteredHabits;
    private List<BoardItem> FilteredDailies => _filteredDailies;

    private int DailiesDueOpenCount =>
        _filteredDailies.Count(d => DailySchedule.IsDueOnDate(d, DailySchedule.LocalToday(TimeZoneService)));

    private List<BoardItem> FilteredTodos => _filteredTodos;

    private int OpenTodoCount => _filteredTodos.Count(t => !t.IsCompleted);

    private int _mobileSectionIndex;

    private sealed record ColumnSpec(BoardSection Section, IReadOnlyList<BoardItem> Items, bool IsExcluded);

    private ColumnSpec[] BoardColumns => new[]
    {
        new ColumnSpec(BoardSection.Habit, FilteredHabits, IsFilteredOut(BoardSection.Habit)),
        new ColumnSpec(BoardSection.Daily, FilteredDailies, IsFilteredOut(BoardSection.Daily)),
        new ColumnSpec(BoardSection.Todo, FilteredTodos, IsFilteredOut(BoardSection.Todo))
    };

    private bool IsFilteredOut(BoardSection section) => section switch
    {
        BoardSection.Habit => Habits.Count > 0 && FilteredHabits.Count == 0,
        BoardSection.Daily => Dailies.Count > 0 && FilteredDailies.Count == 0,
        _ => Todos.Count > 0 && FilteredTodos.Count == 0
    };

    private string GetSectionSwitcherClass(int index) =>
        index == _mobileSectionIndex
            ? "board-section-switcher__btn board-section-switcher__btn--active"
            : "board-section-switcher__btn";

    private Dictionary<string, object> GetTabAttributes(int index) => new()
    {
        ["aria-selected"] = _mobileSectionIndex == index ? "true" : "false",
        ["tabindex"] = _mobileSectionIndex == index ? 0 : -1,
    };

    private string GetTagsTriggerClass() =>
        SelectedFilterTagCount > 0
            ? "board-trigger-btn board-trigger-btn--active"
            : "board-trigger-btn";

    private void OnMobileSectionKeyDown(KeyboardEventArgs e)
    {
        if (e.Key is not ("ArrowLeft" or "ArrowRight" or "Home" or "End"))
        {
            return;
        }

        var next = e.Key switch
        {
            "ArrowLeft" => (_mobileSectionIndex + 2) % 3,
            "ArrowRight" => (_mobileSectionIndex + 1) % 3,
            "Home" => 0,
            _ => 2
        };
        _mobileSectionIndex = next;
        StateHasChanged();
    }

    private List<string> GetAllTagNamesForMenu() =>
        Habits.Concat(Dailies).Concat(Todos)
            .SelectMany(i => BoardTagUtil.ParseTags(i.Tags))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private int SelectedFilterTagCount => _selectedFilterTags.Count;

    protected override async Task OnInitializedAsync()
    {
        _subscription = ApplicationState.RegisterOnPersisting(PersistBoardData);
        if (ApplicationState.TryTakeFromJson<BoardSnapshot>("board_snapshot", out var restored) && restored is not null)
        {
            Habits = restored.Habits.ToList();
            Dailies = restored.Dailies.ToList();
            Todos = restored.Todos.ToList();
            PruneStaleFilterTags();
            RecomputeFilters();
            _initialLoadComplete = true;
            InitialBoardLoad.MarkComplete();
        }
        else if (BoardDataService.TryGetCachedSnapshot(out var cached) && cached is not null)
        {
            Habits = cached.Habits.ToList();
            Dailies = cached.Dailies.ToList();
            Todos = cached.Todos.ToList();
            PruneStaleFilterTags();
            RecomputeFilters();
            _initialLoadComplete = true;
            InitialBoardLoad.MarkComplete();
        }
        await DateFormatService.InitializeAsync();
        BoardSync.Changed += OnBoardSyncStatusChanged;
        RemoteBoardRefresh.RegisterForRemoteRefresh(HandleRemoteBoardRefreshedAsync);
        UndoService.OnStateChanged += HandleUndoStateChanged;
        UndoService.OnUndoPerformed += HandleUndoPerformed;
        PreferencesService.Changed += OnPreferencesChanged;
        await LoadShortcutsPreferenceAsync();
        if (!_initialLoadComplete)
        {
            await InitialLoadAsync();
        }
    }

    private Task PersistBoardData()
    {
        var snapshot = new BoardSnapshot(
            Habits,
            Dailies,
            Todos
        );
        ApplicationState.PersistAsJson("board_snapshot", snapshot);
        return Task.CompletedTask;
    }

    private void OnBoardSyncStatusChanged(object? sender, EventArgs e)
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnPreferencesChanged(object? sender, EventArgs e)
    {
        _ = InvokeAsync(async () =>
        {
            await LoadShortcutsPreferenceAsync();
            StateHasChanged();
        });
    }

    private async Task LoadShortcutsPreferenceAsync()
    {
        try
        {
            var prefs = await PreferencesService.GetAsync();
            _shortcutsEnabled = prefs.EnableKeyboardShortcuts;
        }
        catch
        {
            // Ignore if settings storage fails
        }
    }

    /// <summary>
    ///     Marshals server-pushed refreshes onto the Blazor dispatcher (SignalR/JS callbacks are not on the renderer sync
    ///     context).
    /// </summary>
    private Task HandleRemoteBoardRefreshedAsync()
    {
        return InvokeAsync(OnRemoteBoardRefreshedAsync);
    }

    private async Task OnRemoteBoardRefreshedAsync()
    {
        // Skip reloading from remote SignalR notifications if we recently performed a local mutation,
        // as the local action has already fetched the latest snapshot.
        if (DateTimeOffset.UtcNow - _lastLocalMutationTime < TimeSpan.FromSeconds(2.0))
        {
            return;
        }

        try
        {
            await LoadBoardAsync();
            await RefreshStreaksAsync();
        }
        catch
        {
            // keep existing UI. User can use Retry
        }

        await InvokeAsync(StateHasChanged);
    }

    public async ValueTask DisposeAsync()
    {
        BoardSync.Changed -= OnBoardSyncStatusChanged;
        RemoteBoardRefresh.UnregisterForRemoteRefresh(HandleRemoteBoardRefreshedAsync);
        UndoService.OnStateChanged -= HandleUndoStateChanged;
        UndoService.OnUndoPerformed -= HandleUndoPerformed;
        PreferencesService.Changed -= OnPreferencesChanged;
        if (_boardClientScriptsStarted)
        {
            await SafeInvokeVoidAsync("HabitinatorBoardVisibility.stop");
            await SafeInvokeVoidAsync("HabitinatorKeyboardShortcuts.stop");
        }

        _visibilityRef?.Dispose();
        _visibilityRef = null;
        _selfRef?.Dispose();
        _selfRef = null;
        _subscription.Dispose();
        GC.SuppressFinalize(this);
    }

    private async ValueTask SafeInvokeVoidAsync(string identifier, params object?[]? args)
    {
        try
        {
            await JS.InvokeVoidAsync(identifier, args);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or JSException or TaskCanceledException or InvalidOperationException)
        {
            // Ignored during page navigation/disposal
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Eagerly preload SortableJS and board reordering scripts during initial data load
            _ = PreloadSortableScriptsAsync();
        }

        if (_initialLoadComplete && _loadError is null && !_boardClientScriptsStarted)
        {
            await StartBoardClientScriptsAsync();
        }
    }

    private async Task PreloadSortableScriptsAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("habitinatorLoadScript", "_content/App.Shared.RCL/js/sortable.min.js");
            await JS.InvokeVoidAsync("habitinatorLoadScript", "_content/App.Shared.RCL/js/boardSortable.js");
        }
        catch (Exception)
        {
            // Ignored during background preload
        }
    }

    private async Task StartBoardClientScriptsAsync()
    {
        _boardClientScriptsStarted = true;
        try
        {
            await JS.InvokeVoidAsync(
                "habitinatorLoadScript",
                "_content/App.Shared.RCL/js/boardVisibility.js");

            _visibilityRef = DotNetObjectReference.Create(BoardNotifyBridge);
            await JS.InvokeVoidAsync("HabitinatorBoardVisibility.start", _visibilityRef);

            _selfRef = DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("HabitinatorKeyboardShortcuts.start", _selfRef);

            _dailyRetroClientReady = true;
            _ = RefreshStreaksAsync();
            await TryOpenDailyYesterdayRetroIfNeededAsync();
            await TryOpenOnboardingIfNeededAsync();
        }
        catch (JSDisconnectedException)
        {
            // Ignored during page navigation
        }
        catch (TaskCanceledException)
        {
            // Ignored during page navigation
        }
        catch (InvalidOperationException)
        {
            // Ignored during page navigation
        }
        catch (JSException)
        {
            _boardClientScriptsStarted = false;
        }
    }

    private async Task OnBoardChanged()
    {
        _lastLocalMutationTime = DateTimeOffset.UtcNow;
        await LoadBoardAsync();
    }

    private async Task InitialLoadAsync()
    {
        await LoadAsync(LoadBoardAsync);
        _initialLoadComplete = true;
        InitialBoardLoad.MarkComplete();
    }

    private async Task RetryLoadAsync()
    {
        await LoadAsync(LoadBoardAsync);
        if (_loadError is null && _dailyRetroClientReady)
        {
            await TryOpenDailyYesterdayRetroIfNeededAsync();
        }
    }

    private async Task LoadAsync(Func<Task> load)
    {
        _isLoading = true;
        _loadError = null;
        try
        {
            await load();
        }
        catch (Exception ex)
        {
            _loadError = ex is InvalidOperationException
                ? ex.Message
                : "Could not load your board. Please try again.";
            await Notifier.NotifyAsync(_loadError, MudBlazor.Severity.Error);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task LoadBoardAsync()
    {
        if (ApplicationState.TryTakeFromJson<BoardSnapshot>("board_snapshot", out var restored) && restored is not null)
        {
            Habits = restored.Habits.ToList();
            Dailies = restored.Dailies.ToList();
            Todos = restored.Todos.ToList();
        }
        else
        {
            var snapshot = await BoardDataService.GetSnapshotAsync();
            Habits = snapshot.Habits.ToList();
            Dailies = snapshot.Dailies.ToList();
            Todos = snapshot.Todos.ToList();
        }
        PruneStaleFilterTags();
        RecomputeFilters();
    }

    private void RecomputeFilters()
    {
        _filteredHabits = FilterItems(Habits);
        _filteredDailies = FilterItems(Dailies);
        _filteredTodos = FilterItems(Todos);
    }

    private List<BoardItem> FilterItems(IReadOnlyList<BoardItem> source)
    {
        return source.Where(MatchesGlobalFilters).ToList();
    }

    private bool MatchesGlobalFilters(BoardItem item)
    {
        return MatchesTagFilter(item) && MatchesSearchQuery(item);
    }

    private bool MatchesTagFilter(BoardItem item)
    {
        if (_selectedFilterTags.Count == 0)
        {
            return true;
        }

        return BoardTagUtil.ParseTags(item.Tags).Any(_selectedFilterTags.Contains);
    }

    private bool MatchesSearchQuery(BoardItem item)
    {
        var q = _searchText.Trim();
        if (q.Length == 0)
        {
            return true;
        }

        if (item.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (item.Notes is not null && item.Notes.Contains(q, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(item.ChecklistJson)
            && item.ChecklistJson.Contains(q, StringComparison.OrdinalIgnoreCase)
            && DailyChecklistJson.Parse(item.ChecklistJson).Any(line => line.Text.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (BoardTagUtil.ParseTags(item.Tags).Any(tag => tag.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private bool IsFilterTagSelected(string tag)
    {
        return _selectedFilterTags.Contains(tag);
    }

    private void OnFilterTagChanged(string tag, bool isChecked)
    {
        if (isChecked)
        {
            _selectedFilterTags.Add(tag);
        }
        else
        {
            _ = _selectedFilterTags.Remove(tag);
        }

        RecomputeFilters();
    }

    private void PruneStaleFilterTags()
    {
        if (_selectedFilterTags.Count == 0)
        {
            return;
        }

        var available = new HashSet<string>(GetAllTagNamesForMenu(), StringComparer.OrdinalIgnoreCase);
        _selectedFilterTags.RemoveWhere(f => !available.Contains(f));
    }

    private void ClearBoardFilters()
    {
        _searchText = string.Empty;
        _selectedFilterTags.Clear();
        RecomputeFilters();
    }

    private void OnSearchTextChanged()
    {
        RecomputeFilters();
    }

    private async Task HandleTimerLogSavedAsync(TimeSpan duration)
    {
        _lastLocalMutationTime = DateTimeOffset.UtcNow;
        var result = await TimerSessionLog.LogStoppedSessionAsync(duration);
        // When board progress was made, the undo toast for that action is the notification.
        if (result.BoardUpdateFailed || !result.BoardProgressed)
        {
            await Notifier.NotifyAsync(result.UserMessage, result.BoardUpdateFailed ? Severity.Error : Severity.Success);
        }

        await LoadBoardAsync();
    }

    private async Task TryOpenDailyYesterdayRetroIfNeededAsync()
    {
        if (_dailyRetroSessionOffered)
        {
            return;
        }

        _dailyRetroSessionOffered = true;
        try
        {
            var prefs = await PreferencesService.GetAsync();
            var today = DailySchedule.LocalToday(TimeZoneService, prefs.DayStartLocalTime);
            var lastResolved = await DailyRetroStore.GetLastPromptResolvedLocalDateAsync();

            if (lastResolved == today)
            {
                return;
            }

            var missed = DailySchedule.GetYesterdayUncompletedDailies(Dailies, today, TimeZoneService);
            if (missed.Count == 0)
            {
                return;
            }

            var yesterdayDate = today.AddDays(-1);
            var parameters = new DialogParameters<DailyYesterdayRetroDialog>
            {
                { x => x.DueOn, yesterdayDate },
                { x => x.Items, missed }
            };

            var dialog = await DialogService.ShowAsync<DailyYesterdayRetroDialog>(
                string.Empty, parameters, DialogDefaults.SmallEditor);
            await dialog.Result;

            try
            {
                await DailyRetroStore.SetPromptResolvedForTodayAsync();
            }
            catch (Exception)
            {
                // Ignore error when saving daily retro status as it's non-critical local UI state
            }

            await LoadBoardAsync();
            await RefreshStreaksAsync();
        }
        catch (Exception)
        {
            _dailyRetroSessionOffered = false;
        }
    }

    private async Task TryOpenOnboardingIfNeededAsync()
    {
        if (_onboardingOffered)
        {
            return;
        }

        if (Habits.Count > 0 || Dailies.Count > 0 || Todos.Count > 0)
        {
            _onboardingOffered = true;
            return;
        }

        _onboardingOffered = true;
        try
        {
            if (await OnboardingStore.IsCompletedAsync())
            {
                return;
            }

            var options = DialogDefaults.SmallEditor;
            await DialogService.ShowAsync<OnboardingDialog>(string.Empty, options);
        }
        catch (Exception)
        {
            _onboardingOffered = false;
        }
    }

    private async Task RefreshStreaksAsync()
    {
        try
        {
            var streaks = await BoardDataService.GetStreakMapAsync();
            var anyChanged = false;
            for (var i = 0; i < Dailies.Count; i++)
            {
                var daily = Dailies[i];
                if (streaks.TryGetValue(daily.Id, out var s) && s != daily.Counter)
                {
                    Dailies[i] = daily with { Counter = s };
                    anyChanged = true;
                }
            }

            if (anyChanged)
            {
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (Exception)
        {
            // Silently ignore. Board will reflect updated streaks on next full load
        }
    }

    private void HandleUndoStateChanged(object? sender, EventArgs e)
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void HandleUndoPerformed(object? sender, EventArgs e)
    {
        _ = InvokeAsync(async () =>
        {
            _lastLocalMutationTime = DateTimeOffset.UtcNow;
            await LoadBoardAsync();
            await RefreshStreaksAsync();
            StateHasChanged();
        });
    }

    private async Task HandleUndoAsync()
    {
        await UndoService.UndoAsync();
    }

    [JSInvokable]
    public async Task OnCtrlZPressed()
    {
        if (UndoService.CanUndo)
        {
            await HandleUndoAsync();
        }
    }

    public async Task CreateItemPublicAsync(BoardSection section)
    {
        var defaultTitle = section switch
        {
            BoardSection.Habit => "New Habit",
            BoardSection.Daily => "New Daily",
            BoardSection.Todo => "New To Do",
            _ => "New Item"
        };
        try
        {
            var item = await BoardDataService.CreateItemAsync(section, defaultTitle);
            if (item is not null)
            {
                await OnBoardChanged();
                var result = await OpenItemEditorForCreatedAsync(section, item);
                if (result is { Canceled: false, Data: not null })
                {
                    await HandleCreatedItemDialogActionAsync(section, item.Id, result.Data);
                }

                // The dialog autosaves every change, so an item that is still identical to the
                // freshly created one was never edited. Drop it to avoid cluttering the board.
                var current = await BoardDataService.GetItemAsync(item.Id);
                if (current is not null && current == item)
                {
                    await BoardDataService.DeleteItemAsync(section, item.Id);
                }

                await OnBoardChanged();
            }
        }
        catch (Exception)
        {
            await Notifier.NotifyAsync("Could not create item.", Severity.Error);
        }
    }

    private async Task HandleCreatedItemDialogActionAsync(BoardSection section, Guid itemId, object data)
    {
        EditDialogAction? action = data switch
        {
            EditHabitDialogResult h => h.Action,
            EditDailyDialogResult d => d.Action,
            EditTodoDialogResult t => t.Action,
            _ => null
        };

        if (action == EditDialogAction.Archive)
        {
            await BoardDataService.ArchiveItemAsync(section, itemId);
        }
        else if (action == EditDialogAction.Delete)
        {
            await BoardDataService.DeleteItemAsync(section, itemId);
        }
    }

    private async Task<DialogResult?> OpenItemEditorForCreatedAsync(BoardSection section, BoardItem item)
    {
        var options = DialogDefaults.SmallEditor;

        var dialog = section switch
        {
            BoardSection.Habit => await DialogService.ShowAsync<EditHabitDialog>(
                string.Empty, new DialogParameters<EditHabitDialog> { { x => x.Item, item } }, options),
            BoardSection.Daily => await DialogService.ShowAsync<EditDailyDialog>(
                string.Empty, new DialogParameters<EditDailyDialog> { { x => x.Item, item } }, options),
            _ => await DialogService.ShowAsync<EditTodoDialog>(
                string.Empty, new DialogParameters<EditTodoDialog> { { x => x.Item, item } }, options)
        };

        return await dialog.Result;
    }

    private async Task OpenArchivedItemsDialogAsync()
    {
        var options = new DialogOptions
        {
            CloseButton = false,
            CloseOnEscapeKey = true,
            NoHeader = false,
            Position = DialogPosition.TopCenter
        };

        var dialog = await DialogService.ShowAsync<ArchivedItemsDialog>(string.Empty, options);
        await dialog.Result;

        _lastLocalMutationTime = DateTimeOffset.UtcNow;
        await LoadBoardAsync();
    }
}
