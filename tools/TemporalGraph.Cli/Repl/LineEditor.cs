namespace TemporalGraph.Cli.Repl;

/// <summary>Where a console (the query and the chat REPL) reads its lines from: a terminal, or a list of lines in the tests.</summary>
public interface ILineReader
{
    /// <summary>One line, without its end; null at the end of the input. A cancelled token (Ctrl+C) ends the wait with an
    /// <see cref="OperationCanceledException"/>.</summary>
    string? Read(string prompt, CancellationToken ct = default);

    /// <summary>Puts a line into the history without it having been typed.</summary>
    void Remember(string line);
}

/// <summary>
/// A small line editor over <see cref="Console.ReadKey(bool)"/>: left/right, home/end, backspace/delete, escape to clear, and the
/// session's history on up/down. No third-party readline; nothing is persisted. It waits for a key by asking whether one is
/// there (a blocking read never sees Ctrl+C), so a cancelled token leaves the line at once.
/// </summary>
public sealed class LineEditor : ILineReader
{
    private readonly List<string> _history = [];

    public void Remember(string line)
    {
        if (line.Length > 0 && (_history.Count == 0 || _history[^1] != line)) _history.Add(line);
    }

    public string? Read(string prompt, CancellationToken ct = default)
    {
        using var prompting = Interrupts.Prompting();
        var buffer = new System.Text.StringBuilder();
        var cursor = 0;
        var at = _history.Count;   // past the newest: the line being typed
        var typed = "";
        var shown = 0;
        Console.Write(prompt);

        void Redraw()
        {
            // Back to the start of the input, write it, blank what the earlier text left, and put the cursor back.
            Console.Write('\r' + prompt + buffer + new string(' ', Math.Max(0, shown - buffer.Length)));
            Console.Write('\r' + prompt + buffer.ToString(0, cursor));
            shown = buffer.Length;
        }

        void Replace(string text)
        {
            buffer.Clear().Append(text);
            cursor = buffer.Length;
            Redraw();
        }

        while (true)
        {
            while (!Console.KeyAvailable)
            {
                if (ct.IsCancellationRequested)
                {
                    Console.WriteLine();
                    throw new OperationCanceledException(ct);
                }
                Thread.Sleep(15);
            }
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    Remember(buffer.ToString());
                    return buffer.ToString();
                case ConsoleKey.Z or ConsoleKey.D when key.Modifiers.HasFlag(ConsoleModifiers.Control) && buffer.Length == 0:
                    Console.WriteLine();
                    return null;
                case ConsoleKey.Backspace when cursor > 0:
                    buffer.Remove(--cursor, 1);
                    Redraw();
                    break;
                case ConsoleKey.Delete when cursor < buffer.Length:
                    buffer.Remove(cursor, 1);
                    Redraw();
                    break;
                case ConsoleKey.LeftArrow when cursor > 0:
                    cursor--;
                    Redraw();
                    break;
                case ConsoleKey.RightArrow when cursor < buffer.Length:
                    cursor++;
                    Redraw();
                    break;
                case ConsoleKey.Home:
                    cursor = 0;
                    Redraw();
                    break;
                case ConsoleKey.End:
                    cursor = buffer.Length;
                    Redraw();
                    break;
                case ConsoleKey.Escape:
                    Replace("");
                    break;
                case ConsoleKey.UpArrow when at > 0:
                    if (at == _history.Count) typed = buffer.ToString();
                    Replace(_history[--at]);
                    break;
                case ConsoleKey.DownArrow when at < _history.Count:
                    at++;
                    Replace(at == _history.Count ? typed : _history[at]);
                    break;
                default:
                    if (key.KeyChar >= ' ')
                    {
                        buffer.Insert(cursor++, key.KeyChar);
                        Redraw();
                    }
                    break;
            }
        }
    }
}

/// <summary>Lines given ahead (the tests): each <see cref="Read"/> takes the next; null when they are used up.</summary>
public sealed class ScriptedLines(IEnumerable<string> lines) : ILineReader
{
    private readonly Queue<string> _lines = new(lines);

    /// <summary>The prompts shown, in order.</summary>
    public List<string> Prompts { get; } = [];

    public string? Read(string prompt, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Prompts.Add(prompt);
        return _lines.Count > 0 ? _lines.Dequeue() : null;
    }

    public void Remember(string line) { }
}
