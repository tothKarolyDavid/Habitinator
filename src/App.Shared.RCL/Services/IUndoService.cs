namespace App.Shared.RCL.Services;

public interface IUndoService
{
    bool CanUndo { get; }
    bool IsUndoing { get; }
    string? LastActionDescription { get; }
    Guid RegisterUndo(string description, Func<Task> undoFunc);

    /// <summary>
    ///     Registers an undo entry with associated conflict keys describing touched state.
    /// </summary>
    Guid RegisterUndo(string description, Func<Task> undoFunc, IReadOnlyCollection<string> conflictKeys);

    IDisposable BeginBatch(string description);
    Task UndoAsync();
    Task UndoAsync(Guid actionId);
    event EventHandler? OnStateChanged;
    event EventHandler? OnUndoPerformed;
}
