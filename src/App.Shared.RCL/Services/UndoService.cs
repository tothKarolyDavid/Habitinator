using App.Shared.RCL.Components;

using Microsoft.Extensions.Logging;

using MudBlazor;

namespace App.Shared.RCL.Services;

public sealed class UndoService(
    ISnackbar snackbar,
    INotificationSettingsService settingsService,
    INotificationSettingsRules notificationRules,
    ILogger<UndoService> logger) : IUndoService, IDisposable
{
    private const string UndoSnackbarKeyPrefix = "habitinator-undo";

    private readonly List<UndoAction> _undoStack = [];
    private readonly ISnackbar _snackbar = snackbar;
    private readonly INotificationSettingsService _settingsService = settingsService;
    private readonly INotificationSettingsRules _notificationRules = notificationRules;
    private readonly ILogger<UndoService> _logger = logger;

    private List<Func<Task>>? _currentBatch;
    private string? _currentBatchDescription;
    private int _undoingCount;
    private readonly SemaphoreSlim _undoLock = new(1, 1);

    public bool IsUndoing => _undoingCount > 0;
    public bool CanUndo => _undoStack.Count > 0;
    public string? LastActionDescription => _undoStack.Count > 0 ? _undoStack[^1].Description : null;

    public event EventHandler? OnStateChanged;
    public event EventHandler? OnUndoPerformed;

    public Guid RegisterUndo(string description, Func<Task> undoFunc)
    {
        return RegisterUndo(description, undoFunc, []);
    }

    public Guid RegisterUndo(string description, Func<Task> undoFunc, IReadOnlyCollection<string> conflictKeys)
    {
        if (IsUndoing)
        {
            return Guid.Empty;
        }

        if (_currentBatch is { } batch)
        {
            batch.Add(undoFunc);
            return Guid.Empty;
        }

        var action = new UndoAction(description, undoFunc);
        action.SnackbarKey = $"{UndoSnackbarKeyPrefix}-{action.Id:N}";

        _undoStack.Add(action);
        OnStateChanged?.Invoke(this, EventArgs.Empty);
        _ = ShowUndoSnackbarAsync(action);
        return action.Id;
    }

    public IDisposable BeginBatch(string description)
    {
        return new UndoBatch(this, description);
    }

    private void StartBatch(string description)
    {
        _currentBatch = [];
        _currentBatchDescription = description;
    }

    private void EndBatch()
    {
        if (_currentBatch is null)
        {
            return;
        }

        var batch = _currentBatch;
        var desc = _currentBatchDescription ?? "Multiple actions";
        _currentBatch = null;
        _currentBatchDescription = null;

        if (batch.Count > 0)
        {
            batch.Reverse();
            RegisterUndo(desc, async () =>
            {
                foreach (var batchAction in batch)
                {
                    await batchAction().ConfigureAwait(false);
                }
            });
        }
    }

    public Task UndoAsync() => UndoAsync(null);

    public Task UndoAsync(Guid actionId) => UndoAsync((Guid?)actionId);

    private async Task UndoAsync(Guid? actionId)
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        await _undoLock.WaitAsync().ConfigureAwait(false);
        List<UndoAction>? undone = null;
        try
        {
            var toUndo = SelectActionsToUndo(actionId);
            if (toUndo is null)
            {
                return;
            }

            undone = await ExecuteUndoAsync(toUndo);
        }
        finally
        {
            _undoLock.Release();
            if (undone is not null)
            {
                foreach (var action in undone)
                {
                    DismissSnackbar(action);
                }
            }

            OnStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private List<UndoAction>? SelectActionsToUndo(Guid? actionId)
    {
        var index = actionId is null
            ? _undoStack.Count - 1
            : _undoStack.FindIndex(a => a.Id == actionId);

        if (index < 0)
        {
            return null;
        }

        return [_undoStack[index]];
    }

    private async Task<List<UndoAction>?> ExecuteUndoAsync(List<UndoAction> toUndo)
    {
        Interlocked.Increment(ref _undoingCount);
        var executed = new List<UndoAction>();
        try
        {
            foreach (var action in toUndo)
            {
                await action.UndoFunc().ConfigureAwait(false);
                executed.Add(action);
            }

            foreach (var action in toUndo)
            {
                _undoStack.Remove(action);
            }

            OnUndoPerformed?.Invoke(this, EventArgs.Empty);
            return toUndo;
        }
        catch (Exception ex)
        {
            // The already-undone prefix must not be re-applied on retry, or the same change
            // would be reverted twice. Drop it from the stack. Only the failed action stays.
            foreach (var action in executed)
            {
                _undoStack.Remove(action);
            }

            _logger.LogWarning(ex, "Undo failed after {ExecutedCount} action(s). The failed action remains on the undo stack.", executed.Count);
            return executed.Count > 0 ? executed : null;
        }
        finally
        {
            Interlocked.Decrement(ref _undoingCount);
        }
    }

    private async Task ShowUndoSnackbarAsync(UndoAction action)
    {
        try
        {
            var settings = await _settingsService.GetAsync(CancellationToken.None).ConfigureAwait(false);
            var ms = _notificationRules.UndoVisibleStateDurationMs(settings.ToastDuration);

            Snackbar? toast = null;
            toast = _snackbar.Add<UndoToastContent>(
                new Dictionary<string, object>
                {
                    [nameof(UndoToastContent.Description)] = action.Description,
                    [nameof(UndoToastContent.OnUndo)] = new Func<Task>(async () =>
                    {
                        await UndoAsync(action.Id).ConfigureAwait(false);
                        toast?.ForceClose();
                    }),
                    [nameof(UndoToastContent.OnDismiss)] = new Func<Task>(() =>
                    {
                        toast?.ForceClose();
                        return Task.CompletedTask;
                    }),
                },
                Severity.Normal,
                config =>
                {
                    AppSnackbar.Configure(config, ms);
                    config.SnackbarTypeClass = $"{AppSnackbar.ToastTypeClass} undo-toast";
                },
                action.SnackbarKey);
        }
        catch (Exception ex)
        {
            // Best-effort snackbar. The action is still on the undo stack
            _logger.LogDebug(ex, "Failed to show the undo snackbar.");
        }
    }

    private void DismissSnackbar(UndoAction action)
    {
        if (action.SnackbarKey is null)
        {
            return;
        }

        try
        {
            _snackbar.RemoveByKey(action.SnackbarKey);
        }
        catch (Exception ex)
        {
            // Best-effort dismissal
            _logger.LogDebug(ex, "Failed to dismiss the undo snackbar.");
        }
    }

    private sealed class UndoAction(string description, Func<Task> undoFunc)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Description => description;
        public Func<Task> UndoFunc => undoFunc;
        public string? SnackbarKey { get; set; }
    }

    public void Dispose()
    {
        _undoLock.Dispose();
    }

    private sealed class UndoBatch : IDisposable
    {
        private readonly UndoService _service;

        public UndoBatch(UndoService service, string description)
        {
            _service = service;
            _service.StartBatch(description);
        }

        public void Dispose() => _service.EndBatch();
    }
}
