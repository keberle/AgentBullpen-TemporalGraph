namespace TemporalGraph.Cli.Setup;

public enum MoveKind { Next, Back, Cancel, Jump, Done }

/// <summary>What a step asks the flow to do next. <see cref="To"/> jumps to a named step (the review's "Edit a step").</summary>
public readonly record struct Move(MoveKind Kind, string? Target = null)
{
    public static readonly Move Next = new(MoveKind.Next);
    public static readonly Move Back = new(MoveKind.Back);
    public static readonly Move Cancel = new(MoveKind.Cancel);
    public static readonly Move Done = new(MoveKind.Done);

    public static Move To(string step) => new(MoveKind.Jump, step);
}

public enum FlowEnd { Done, Back, Cancelled }

/// <summary>Where a step sits among the steps that apply to the draft, for the "Step 2 of 4" header.</summary>
public readonly record struct StepPosition(int Number, int Count);

/// <param name="Name">The id <see cref="Move.To"/> names.</param>
/// <param name="Title">What the review's "Edit a step" list shows.</param>
/// <param name="Applies">Whether the step is shown for this draft (the Azure endpoint step only for Azure, say); null = always.</param>
public sealed record Step<TDraft>(string Name, string Title, Func<TDraft, StepPosition, Task<Move>> Run, Func<TDraft, bool>? Applies = null);

/// <summary>
/// The walkthrough's steps as a small state machine. Each step reads and fills the draft and returns a <see cref="Move"/>: Next
/// and Back go to the next or previous step that applies, Back from the first step leaves the flow (<see cref="FlowEnd.Back"/>), Cancel
/// leaves it at once, a jump goes to a named step, and Done (the review's Save) ends it. The flow itself writes nothing.
/// </summary>
public sealed class StepFlow<TDraft>(IReadOnlyList<Step<TDraft>> steps)
{
    /// <summary>The steps run, in order (the tests read it).</summary>
    public List<string> Trail { get; } = [];

    public IReadOnlyList<Step<TDraft>> Steps => steps;

    public List<Step<TDraft>> Applicable(TDraft draft) => steps.Where(s => s.Applies?.Invoke(draft) ?? true).ToList();

    public async Task<FlowEnd> RunAsync(TDraft draft)
    {
        var i = NextFrom(-1, draft);
        while (i >= 0 && i < steps.Count)
        {
            var step = steps[i];
            var shown = Applicable(draft);
            Trail.Add(step.Name);
            var move = await step.Run(draft, new StepPosition(shown.IndexOf(step) + 1, shown.Count));
            switch (move.Kind)
            {
                case MoveKind.Done: return FlowEnd.Done;
                case MoveKind.Cancel: return FlowEnd.Cancelled;
                case MoveKind.Back:
                    i = PreviousFrom(i, draft);
                    if (i < 0) return FlowEnd.Back;
                    break;
                case MoveKind.Jump:
                    i = steps.ToList().FindIndex(s => s.Name == move.Target);
                    if (i < 0) throw new InvalidOperationException($"No step named '{move.Target}'.");
                    break;
                default:
                    i = NextFrom(i, draft);
                    break;
            }
        }
        return FlowEnd.Done;   // ran off the end: only a flow without a review step does
    }

    private int NextFrom(int i, TDraft draft)
    {
        for (var j = i + 1; j < steps.Count; j++)
            if (steps[j].Applies?.Invoke(draft) ?? true) return j;
        return steps.Count;
    }

    private int PreviousFrom(int i, TDraft draft)
    {
        for (var j = i - 1; j >= 0; j--)
            if (steps[j].Applies?.Invoke(draft) ?? true) return j;
        return -1;
    }
}
