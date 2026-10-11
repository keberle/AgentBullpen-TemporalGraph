namespace TemporalGraph.Query;

// The supported openCypher subset as a small AST (built by AstBuilder from the ANTLR parse tree).

/// <summary>A syntax error, or a feature outside the supported subset; <see cref="Offset"/> is 0-based into the query text.</summary>
public sealed class CypherException(string message, int line, int column, int offset) : Exception($"{message} (line {line}, column {column + 1})")
{
    public string Reason { get; } = message;
    /// <summary>1-based line.</summary>
    public int Line { get; } = line;
    /// <summary>0-based column within the line.</summary>
    public int Column { get; } = column;
    public int Offset { get; } = offset;
}

public sealed record QueryAst(IReadOnlyList<Clause> Clauses);

public abstract record Clause;

/// <param name="Where">Applies inside the match: for OPTIONAL MATCH, a row with no passing match keeps nulls.</param>
public sealed record MatchClause(bool Optional, IReadOnlyList<PatternPart> Patterns, Expr? Where) : Clause;

public sealed record WithClause(Projection Projection, Expr? Where) : Clause;

public sealed record ReturnClause(Projection Projection) : Clause;

public sealed record ProjectionItem(Expr Expr, string Name);

public sealed record SortItem(Expr Expr, bool Descending);

public sealed record Projection(bool Distinct, IReadOnlyList<ProjectionItem> Items, IReadOnlyList<SortItem> Order, Expr? Skip, Expr? Limit);

/// <summary>Nodes[0] Rels[0] Nodes[1] Rels[1] … Nodes[n].</summary>
public sealed record PatternPart(IReadOnlyList<NodePattern> Nodes, IReadOnlyList<RelPattern> Rels);

public sealed record NodePattern(string? Variable, string? Label, IReadOnlyList<(string Key, Expr Value)> Properties);

public enum Direction { Right, Left, Both }

public sealed record RelPattern(string? Variable, IReadOnlyList<string> Types, Direction Direction, IReadOnlyList<(string Key, Expr Value)> Properties);

/// <summary>An expression. <see cref="Offset"/> is where it starts in the query text (for evaluation errors), or -1.</summary>
public abstract record Expr
{
    public int Offset { get; init; } = -1;
}

public sealed record LiteralExpr(object? Value) : Expr;

public sealed record ListExpr(IReadOnlyList<Expr> Items) : Expr;

public sealed record ParameterExpr(string Name) : Expr;

public sealed record VariableExpr(string Name) : Expr;

public sealed record PropertyExpr(Expr Target, string Key) : Expr;

public enum BinaryOp { Or, And, Eq, Neq, Lt, Le, Gt, Ge, In, StartsWith, Contains }

public sealed record BinaryExpr(BinaryOp Op, Expr Left, Expr Right) : Expr;

public sealed record NotExpr(Expr Operand) : Expr;

public sealed record IsNullExpr(Expr Operand, bool Negated) : Expr;

/// <summary>count, collect, min, max, id, type, labels, coalesce, tolower, toupper, visible (lower-cased).</summary>
public sealed record FunctionExpr(string Name, IReadOnlyList<Expr> Args) : Expr
{
    public bool IsAggregate => Name is "count" or "collect" or "min" or "max";
}

/// <summary>A label predicate <c>n:Label</c> (or <c>n:A:B</c>: every label) in an expression.</summary>
public sealed record LabelExpr(Expr Target, IReadOnlyList<string> Labels) : Expr;

public sealed record CountStarExpr : Expr;
