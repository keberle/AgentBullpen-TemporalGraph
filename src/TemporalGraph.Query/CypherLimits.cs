namespace TemporalGraph.Query;

/// <summary>
/// What a query may be before it is parsed. The parser is recursive, so a deeply nested query overflows the stack, and a stack
/// overflow cannot be caught: it ends the process, whatever timeout or row cap applies afterwards. A query over either limit is
/// refused with a <see cref="CypherException"/> before any of it is parsed.
/// <para>The limits are measured, not guessed. The longest query file the repo ships is 1,261 characters with its header comments
/// and nests two deep; no code builds a longer or deeper one. On a 1 MB thread stack the parser survives 220 nested brackets and
/// dies at 240. So: 4,000 characters, about three times the longest, and 32 levels, sixteen times the deepest and a seventh of what
/// overflows.</para>
/// <para>A chain of operators (<c>NOT NOT …</c>, <c>a AND b AND …</c>) makes a deep tree with no bracket in it. The length limit
/// bounds it at about 1,000 links, and the code that walks the tree checks the stack as it goes
/// (<see cref="System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack"/>), which becomes the same error.</para>
/// </summary>
public static class CypherLimits
{
    /// <summary>The most characters a query may have.</summary>
    public const int MaxLength = 4000;

    /// <summary>The most levels of <c>(</c>, <c>[</c> and <c>{</c> a query may have open at once, outside string literals.</summary>
    public const int MaxDepth = 32;

    /// <summary>Throws when the query is longer than <see cref="MaxLength"/> or nests deeper than <see cref="MaxDepth"/>.</summary>
    public static void Check(string query)
    {
        if (query.Length > MaxLength)
            throw At(query, MaxLength, $"the query is too long: {query.Length:N0} characters, and the limit is {MaxLength:N0}");
        if (DeepestAt(query) is var (depth, offset) && depth > MaxDepth) throw TooDeep(query, offset);
    }

    /// <summary>The error for a query with more than <see cref="MaxDepth"/> brackets open at <paramref name="offset"/>.</summary>
    public static CypherException TooDeep(string query, int offset) =>
        At(query, offset, $"the query is nested too deeply: the limit is {MaxDepth} levels of brackets");

    /// <summary>The error for a chain of operators too long to walk on the stack there is.</summary>
    public static CypherException ChainTooLong(string query) =>
        At(query, 0, "the query is nested too deeply: it chains too many operators (NOT, AND, OR, …) in one expression");

    /// <summary>
    /// The running depth of <c>(</c>, <c>[</c> and <c>{</c> outside string literals (<c>'…'</c>, <c>"…"</c>, with <c>\</c> escapes) and
    /// back-quoted names: the deepest it gets, and where it first goes past <see cref="MaxDepth"/> (else where it is deepest).
    /// </summary>
    public static (int Depth, int Offset) DeepestAt(string query)
    {
        var (depth, deepest, at) = (0, 0, 0);
        char quote = '\0';
        for (var i = 0; i < query.Length; i++)
        {
            var c = query[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote != '`') i++;
                else if (c == quote) quote = '\0';
            }
            else if (c is '\'' or '"' or '`') quote = c;
            else if (c is '(' or '[' or '{')
            {
                depth++;
                if (depth > deepest && deepest <= MaxDepth) (deepest, at) = (depth, i);
                else if (depth > deepest) deepest = depth;
            }
            else if (c is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
        }
        return (deepest, at);
    }

    private static CypherException At(string query, int offset, string message)
    {
        offset = Math.Clamp(offset, 0, query.Length);
        var line = 1 + query.AsSpan(0, offset).Count('\n');
        var lineStart = offset == 0 ? 0 : query.LastIndexOf('\n', offset - 1) + 1;
        return new CypherException(message, line, offset - lineStart, offset);
    }
}
