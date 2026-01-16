using AiSubtitlePro.Core.Models;

namespace AiSubtitlePro.Services;

/// <summary>
/// Command interface for undo/redo support
/// </summary>
public interface IUndoableCommand
{
    string Description { get; }
    void Execute();
    void Undo();
}

public sealed class DelegateCommand : IUndoableCommand
{
    private readonly Action _execute;
    private readonly Action _undo;
    public string Description { get; }

    public DelegateCommand(string description, Action execute, Action undo)
    {
        Description = description;
        _execute = execute;
        _undo = undo;
    }

    public void Execute() => _execute();
    public void Undo() => _undo();
}

public sealed class CompositeCommand : IUndoableCommand
{
    private readonly IReadOnlyList<IUndoableCommand> _commands;
    public string Description { get; }

    public CompositeCommand(string description, IReadOnlyList<IUndoableCommand> commands)
    {
        Description = description;
        _commands = commands;
    }

    public void Execute()
    {
        foreach (var c in _commands)
            c.Execute();
    }

    public void Undo()
    {
        for (int i = _commands.Count - 1; i >= 0; i--)
            _commands[i].Undo();
    }
}

/// <summary>
/// Manages undo/redo operations with unlimited history
/// </summary>
public class UndoRedoManager
{
    private readonly Stack<IUndoableCommand> _undoStack = new();
    private readonly Stack<IUndoableCommand> _redoStack = new();
    private readonly int _maxHistory;

    public event EventHandler? StateChanged;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    
    public string UndoDescription => CanUndo ? _undoStack.Peek().Description : string.Empty;
    public string RedoDescription => CanRedo ? _redoStack.Peek().Description : string.Empty;

    public UndoRedoManager(int maxHistory = 1000)
    {
        _maxHistory = maxHistory;
    }

    public void Execute(IUndoableCommand command)
    {
        command.Execute();
        _undoStack.Push(command);
        _redoStack.Clear();

        // Limit history size
        while (_undoStack.Count > _maxHistory)
        {
            var items = _undoStack.ToArray();
            _undoStack.Clear();
            for (int i = 0; i < items.Length - 1; i++)
            {
                _undoStack.Push(items[i]);
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!CanUndo) return;

        var command = _undoStack.Pop();
        command.Undo();
        _redoStack.Push(command);

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!CanRedo) return;

        var command = _redoStack.Pop();
        command.Execute();
        _undoStack.Push(command);

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}

#region Command Implementations

/// <summary>
/// Command for editing subtitle line text
/// </summary>
public class EditTextCommand : IUndoableCommand
{
    private readonly SubtitleLine _line;
    private readonly string _oldText;
    private readonly string _newText;

    public string Description => "Edit Text";

    public EditTextCommand(SubtitleLine line, string newText)
    {
        _line = line;
        _oldText = line.Text;
        _newText = newText;
    }

    public void Execute() => _line.Text = _newText;
    public void Undo() => _line.Text = _oldText;
}

/// <summary>
/// Command for editing subtitle timing
/// </summary>
public class EditTimingCommand : IUndoableCommand
{
    private readonly SubtitleLine _line;
    private readonly TimeSpan _oldStart, _oldEnd;
    private readonly TimeSpan _newStart, _newEnd;

    public string Description => "Edit Timing";

    public EditTimingCommand(SubtitleLine line, TimeSpan newStart, TimeSpan newEnd)
    {
        _line = line;
        _oldStart = line.Start;
        _oldEnd = line.End;
        _newStart = newStart;
        _newEnd = newEnd;
    }

    public void Execute()
    {
        _line.Start = _newStart;
        _line.End = _newEnd;
    }

    public void Undo()
    {
        _line.Start = _oldStart;
        _line.End = _oldEnd;
    }
}

/// <summary>
/// Command for adding a subtitle line
/// </summary>
public class AddLineCommand : IUndoableCommand
{
    private readonly SubtitleDocument _document;
    private readonly SubtitleLine _line;
    private readonly int _index;

    public string Description => "Add Line";

    public AddLineCommand(SubtitleDocument document, SubtitleLine line, int index)
    {
        _document = document;
        _line = line;
        _index = index;
    }

    public void Execute()
    {
        _document.Lines.Insert(_index, _line);
        _document.ReindexLines();
    }

    public void Undo()
    {
        _document.Lines.Remove(_line);
        _document.ReindexLines();
    }
}

/// <summary>
/// Command for deleting subtitle lines
/// </summary>
public class DeleteLinesCommand : IUndoableCommand
{
    private readonly SubtitleDocument _document;
    private readonly List<(int index, SubtitleLine line)> _deletedLines;

    public string Description => _deletedLines.Count == 1 ? "Delete Line" : $"Delete {_deletedLines.Count} Lines";

    public DeleteLinesCommand(SubtitleDocument document, IEnumerable<SubtitleLine> lines)
    {
        _document = document;
        _deletedLines = lines.Select(l => (_document.Lines.IndexOf(l), l)).OrderByDescending(x => x.Item1).ToList();
    }

    public void Execute()
    {
        foreach (var (_, line) in _deletedLines)
        {
            _document.Lines.Remove(line);
        }
        _document.ReindexLines();
    }

    public void Undo()
    {
        foreach (var (index, line) in _deletedLines.OrderBy(x => x.index))
        {
            _document.Lines.Insert(Math.Min(index, _document.Lines.Count), line);
        }
        _document.ReindexLines();
    }
}

/// <summary>
/// Command for shifting multiple lines' timing
/// </summary>
public class ShiftTimingCommand : IUndoableCommand
{
    private readonly List<(SubtitleLine line, TimeSpan oldStart, TimeSpan oldEnd)> _states;
    private readonly TimeSpan _offset;

    public string Description => "Shift Timing";

    public ShiftTimingCommand(IEnumerable<SubtitleLine> lines, TimeSpan offset)
    {
        _offset = offset;
        _states = lines.Select(l => (l, l.Start, l.End)).ToList();
    }

    public void Execute()
    {
        foreach (var (line, _, _) in _states)
        {
            line.Start += _offset;
            line.End += _offset;
        }
    }

    public void Undo()
    {
        foreach (var (line, oldStart, oldEnd) in _states)
        {
            line.Start = oldStart;
            line.End = oldEnd;
        }
    }
}

#endregion
