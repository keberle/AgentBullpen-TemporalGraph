using System.Globalization;
using System.Text;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using TemporalGraph.Query.Parsing;
using P = TemporalGraph.Query.Parsing.CypherParser;

namespace TemporalGraph.Query;

/// <summary>
/// Parses openCypher text with the generated ANTLR parser (grammar/Cypher.g4, openCypher M23) and builds the AST of the
/// supported subset. Anything the grammar accepts but the subset doesn't is refused with "not supported: …" and the
/// position of that part of the query; nothing is silently ignored.
/// </summary>
public static class AstBuilder
{
    public static QueryAst Parse(string query)
    {
        // The parser is recursive: a query nested a few hundred levels deep overflows the stack, which ends the process. The limits
        // are checked on the text first, so such a query is refused before any of it is parsed.
        CypherLimits.Check(query);
        try { return ParseChecked(query); }
        catch (InsufficientExecutionStackException) { throw CypherLimits.ChainTooLong(query); }
    }

    private static QueryAst ParseChecked(string query)
    {
        var lexer = new CypherLexer(new AntlrInputStream(query));
        var errors = new ThrowingErrorListener(query);
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(errors);
        var parser = new CypherParser(new CommonTokenStream(lexer));
        parser.RemoveErrorListeners();
        parser.AddErrorListener(errors);
        var tree = parser.oC_Cypher();
        return new Builder(query).Cypher(tree);
    }

    /// <summary>Whether <paramref name="e"/> contains count(…), count(*) or collect(…).</summary>
    public static bool ContainsAggregate(Expr e) => Builder.ContainsAggregate(e);

    private sealed class ThrowingErrorListener(string query) : IAntlrErrorListener<int>, IAntlrErrorListener<IToken>
    {
        public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e) =>
            throw new CypherException("syntax error: " + msg, line, charPositionInLine, Offset(query, line, charPositionInLine));

        public void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e) =>
            throw new CypherException("syntax error: " + msg, line, charPositionInLine, offendingSymbol?.StartIndex >= 0 ? offendingSymbol.StartIndex : Offset(query, line, charPositionInLine));
    }

    internal static int Offset(string query, int line, int column)
    {
        var offset = 0;
        for (var l = 1; l < line && offset < query.Length; l++)
        {
            var nl = query.IndexOf('\n', offset);
            if (nl < 0) break;
            offset = nl + 1;
        }
        return Math.Min(offset + column, query.Length);
    }

    private sealed class Builder(string query)
    {
        private static CypherException Unsupported(ParserRuleContext ctx, string what) =>
            new($"not supported: {what}", ctx.Start.Line, ctx.Start.Column, ctx.Start.StartIndex);

        private string Text(ParserRuleContext ctx) =>
            ctx.Stop is null || ctx.Stop.StopIndex < ctx.Start.StartIndex ? "" : query.Substring(ctx.Start.StartIndex, ctx.Stop.StopIndex - ctx.Start.StartIndex + 1);

        public QueryAst Cypher(P.OC_CypherContext ctx)
        {
            var q = ctx.oC_Statement().oC_Query();
            if (q.oC_StandaloneCall() is { } call) throw Unsupported(call, "CALL");
            var regular = q.oC_RegularQuery();
            if (regular.oC_Union().Length > 0) throw Unsupported(regular.oC_Union(0), "UNION");
            var single = regular.oC_SingleQuery();
            var clauses = new List<Clause>();
            Collect((ParserRuleContext?)single.oC_SinglePartQuery() ?? single.oC_MultiPartQuery(), clauses);
            return new QueryAst(clauses);
        }

        /// <summary>Clauses in query order (a multi-part query's children are reading clauses, WITHs and a final single-part query).</summary>
        private void Collect(ParserRuleContext ctx, List<Clause> clauses)
        {
            foreach (var child in ctx.children ?? [])
                switch (child)
                {
                    case P.OC_ReadingClauseContext r: clauses.Add(Reading(r)); break;
                    case P.OC_UpdatingClauseContext u: throw Unsupported(u, Updating(u));
                    case P.OC_WithContext w: clauses.Add(new WithClause(Projection(w.oC_ProjectionBody(), isWith: true), w.oC_Where() is { } wh ? Where(wh) : null)); break;
                    case P.OC_ReturnContext ret: clauses.Add(new ReturnClause(Projection(ret.oC_ProjectionBody(), isWith: false))); break;
                    case P.OC_SinglePartQueryContext s: Collect(s, clauses); break;
                }
        }

        private static string Updating(P.OC_UpdatingClauseContext u) =>
            u.oC_Create() is not null ? "CREATE" : u.oC_Merge() is not null ? "MERGE" : u.oC_Set() is not null ? "SET"
            : u.oC_Delete() is not null ? "DELETE" : "REMOVE";

        private Clause Reading(P.OC_ReadingClauseContext r)
        {
            if (r.oC_Unwind() is { } unwind) throw Unsupported(unwind, "UNWIND");
            if (r.oC_InQueryCall() is { } call) throw Unsupported(call, "CALL");
            var m = r.oC_Match();
            var patterns = m.oC_Pattern().oC_PatternPart().Select(PatternPart).ToList();
            return new MatchClause(m.OPTIONAL() is not null, patterns, m.oC_Where() is { } w ? Where(w) : null);
        }

        private Expr Where(P.OC_WhereContext w)
        {
            var e = Expression(w.oC_Expression());
            if (ContainsAggregate(e)) throw Unsupported(w, "aggregate functions in WHERE");
            return e;
        }

        // ---- Patterns -----------------------------------------------------------------------------------

        private PatternPart PatternPart(P.OC_PatternPartContext p)
        {
            if (p.oC_Variable() is not null) throw Unsupported(p, "path variables (p = …)");
            var element = p.oC_AnonymousPatternPart().oC_PatternElement();
            while (element.oC_PatternElement() is { } inner) element = inner;
            var nodes = new List<NodePattern> { Node(element.oC_NodePattern()) };
            var rels = new List<RelPattern>();
            foreach (var chain in element.oC_PatternElementChain())
            {
                rels.Add(Relationship(chain.oC_RelationshipPattern()));
                nodes.Add(Node(chain.oC_NodePattern()));
            }
            return new PatternPart(nodes, rels);
        }

        private NodePattern Node(P.OC_NodePatternContext n)
        {
            string? label = null;
            if (n.oC_NodeLabels() is { } labels)
            {
                if (labels.oC_NodeLabel().Length > 1) throw Unsupported(labels, "more than one label in a node pattern");
                label = Name(labels.oC_NodeLabel(0).oC_LabelName().oC_SchemaName());
            }
            return new NodePattern(n.oC_Variable() is { } v ? Variable(v) : null, label, Properties(n.oC_Properties()));
        }

        private RelPattern Relationship(P.OC_RelationshipPatternContext r)
        {
            var left = r.oC_LeftArrowHead() is not null;
            var right = r.oC_RightArrowHead() is not null;
            if (left && right) throw Unsupported(r, "a relationship pattern with two arrowheads (<-->)");
            var direction = right ? Direction.Right : left ? Direction.Left : Direction.Both;
            var d = r.oC_RelationshipDetail();
            if (d is null) return new RelPattern(null, [], direction, []);
            if (d.oC_RangeLiteral() is { } range) throw Unsupported(range, "variable-length paths (*)");
            var types = d.oC_RelationshipTypes()?.oC_RelTypeName().Select(t => Name(t.oC_SchemaName())).ToList() ?? [];
            return new RelPattern(d.oC_Variable() is { } v ? Variable(v) : null, types, direction, Properties(d.oC_Properties()));
        }

        private IReadOnlyList<(string, Expr)> Properties(P.OC_PropertiesContext? p)
        {
            if (p is null) return [];
            if (p.oC_Parameter() is { } param) throw Unsupported(param, "a parameter as a property map");
            var map = p.oC_MapLiteral();
            var keys = map.oC_PropertyKeyName();
            var values = map.oC_Expression();
            return keys.Select((k, i) => (Name(k.oC_SchemaName()), Expression(values[i]))).ToList();
        }

        // ---- Projections ----------------------------------------------------------------------------------

        private Projection Projection(P.OC_ProjectionBodyContext body, bool isWith)
        {
            var items = body.oC_ProjectionItems();
            if (items.children.Any(c => c is ITerminalNode t && t.GetText() == "*")) throw Unsupported(items, "* in a projection");
            var list = items.oC_ProjectionItem().Select(item =>
            {
                var expr = Expression(item.oC_Expression());
                if (item.oC_Variable() is { } alias) return new ProjectionItem(expr, Variable(alias));
                if (expr is VariableExpr v) return new ProjectionItem(expr, v.Name);
                if (isWith) throw Unsupported(item, "an expression in WITH without AS (alias it)");
                return new ProjectionItem(expr, Text(item.oC_Expression()));
            }).ToList();
            foreach (var g in list.GroupBy(i => i.Name).Where(g => g.Count() > 1))
                throw Unsupported(items, $"the column name '{g.Key}' twice");
            var order = body.oC_Order()?.oC_SortItem().Select(s =>
                new SortItem(Expression(s.oC_Expression()), s.DESC() is not null || s.DESCENDING() is not null)).ToList() ?? [];
            return new Projection(body.DISTINCT() is not null, list, order,
                body.oC_Skip() is { } skip ? Expression(skip.oC_Expression()) : null,
                body.oC_Limit() is { } limit ? Expression(limit.oC_Expression()) : null);
        }

        // ---- Expressions ----------------------------------------------------------------------------------

        private Expr Expression(P.OC_ExpressionContext e)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            return Or(e.oC_OrExpression()) with { Offset = e.Start.StartIndex };
        }

        private Expr Or(P.OC_OrExpressionContext e) =>
            e.oC_XorExpression().Select(Xor).Aggregate((a, b) => new BinaryExpr(BinaryOp.Or, a, b));

        private Expr Xor(P.OC_XorExpressionContext e)
        {
            if (e.oC_AndExpression().Length > 1) throw Unsupported(e, "XOR");
            return e.oC_AndExpression(0).oC_NotExpression().Select(Not).Aggregate((a, b) => new BinaryExpr(BinaryOp.And, a, b));
        }

        private Expr Not(P.OC_NotExpressionContext e)
        {
            var inner = Comparison(e.oC_ComparisonExpression());
            for (var i = 0; i < e.NOT().Length; i++) inner = new NotExpr(inner);
            return inner;
        }

        private Expr Comparison(P.OC_ComparisonExpressionContext e)
        {
            var first = Predicates(e.oC_StringListNullPredicateExpression());
            var partials = e.oC_PartialComparisonExpression();
            if (partials.Length == 0) return first;
            // a < b < c means a < b AND b < c.
            Expr? result = null;
            var left = first;
            foreach (var p in partials)
            {
                var op = p.GetChild(0).GetText() switch
                {
                    "=" => BinaryOp.Eq, "<>" => BinaryOp.Neq, "<" => BinaryOp.Lt, ">" => BinaryOp.Gt, "<=" => BinaryOp.Le, ">=" => BinaryOp.Ge,
                    var other => throw Unsupported(p, $"operator {other}"),
                };
                var right = Predicates(p.oC_StringListNullPredicateExpression());
                var cmp = new BinaryExpr(op, left, right);
                result = result is null ? cmp : new BinaryExpr(BinaryOp.And, result, cmp);
                left = right;
            }
            return result!;
        }

        private Expr Predicates(P.OC_StringListNullPredicateExpressionContext e)
        {
            var expr = Additive(e.oC_AddOrSubtractExpression());
            foreach (var child in e.children.Skip(1))
                switch (child)
                {
                    case P.OC_StringPredicateExpressionContext s:
                        var right = Additive(s.oC_AddOrSubtractExpression());
                        if (s.STARTS() is not null) expr = new BinaryExpr(BinaryOp.StartsWith, expr, right);
                        else if (s.CONTAINS() is not null) expr = new BinaryExpr(BinaryOp.Contains, expr, right);
                        else throw Unsupported(s, "ENDS WITH");
                        break;
                    case P.OC_ListPredicateExpressionContext l:
                        expr = new BinaryExpr(BinaryOp.In, expr, Additive(l.oC_AddOrSubtractExpression()));
                        break;
                    case P.OC_NullPredicateExpressionContext n:
                        expr = new IsNullExpr(expr, n.NOT() is not null);
                        break;
                }
            return expr;
        }

        private Expr Additive(P.OC_AddOrSubtractExpressionContext e)
        {
            if (e.oC_MultiplyDivideModuloExpression().Length > 1) throw Unsupported(e, "arithmetic (+, -)");
            var m = e.oC_MultiplyDivideModuloExpression(0);
            if (m.oC_PowerOfExpression().Length > 1) throw Unsupported(m, "arithmetic (*, /, %)");
            var p = m.oC_PowerOfExpression(0);
            if (p.oC_UnaryAddOrSubtractExpression().Length > 1) throw Unsupported(p, "arithmetic (^)");
            var u = p.oC_UnaryAddOrSubtractExpression(0);
            var operand = NonArithmetic(u.oC_NonArithmeticOperatorExpression());
            var sign = u.children[0] is ITerminalNode t ? t.GetText() : null;
            if (sign is null) return operand;
            // Only a signed number literal (-1, +2.5): other unary arithmetic is outside the subset.
            if (operand is LiteralExpr { Value: long l }) return new LiteralExpr(sign == "-" ? -l : l);
            if (operand is LiteralExpr { Value: double d }) return new LiteralExpr(sign == "-" ? -d : d);
            throw Unsupported(u, "unary + / - on anything but a number literal");
        }

        private Expr NonArithmetic(P.OC_NonArithmeticOperatorExpressionContext e)
        {
            var expr = Atom(e.oC_Atom());
            foreach (var child in e.children.Skip(1))
                switch (child)
                {
                    case P.OC_PropertyLookupContext look: expr = new PropertyExpr(expr, Name(look.oC_PropertyKeyName().oC_SchemaName())) { Offset = e.Start.StartIndex }; break;
                    case P.OC_ListOperatorExpressionContext list: throw Unsupported(list, "list indexing and slicing");
                    case P.OC_NodeLabelsContext labels:
                        expr = new LabelExpr(expr, labels.oC_NodeLabel().Select(l => Name(l.oC_LabelName().oC_SchemaName())).ToList()) { Offset = e.Start.StartIndex };
                        break;
                }
            return expr;
        }

        private Expr Atom(P.OC_AtomContext a)
        {
            if (a.oC_Literal() is { } lit) return Literal(lit);
            if (a.oC_Parameter() is { } param)
            {
                if (param.oC_SymbolicName() is null) throw Unsupported(param, "numbered parameters ($0)");
                return new ParameterExpr(Symbolic(param.oC_SymbolicName())) { Offset = param.Start.StartIndex };
            }
            if (a.COUNT() is not null) return new CountStarExpr();
            if (a.oC_ParenthesizedExpression() is { } par) return Expression(par.oC_Expression());
            if (a.oC_FunctionInvocation() is { } f) return Function(f);
            if (a.oC_Variable() is { } v) return new VariableExpr(Variable(v)) { Offset = v.Start.StartIndex };
            var what = a.oC_CaseExpression() is not null ? "CASE"
                : a.oC_ListComprehension() is not null ? "list comprehensions"
                : a.oC_PatternComprehension() is not null ? "pattern comprehensions"
                : a.oC_Quantifier() is not null ? "ALL / ANY / NONE / SINGLE"
                : a.oC_PatternPredicate() is not null ? "pattern predicates"
                : a.oC_ExistentialSubquery() is not null ? "EXISTS { … }" : "this expression";
            throw Unsupported(a, what);
        }

        private static readonly HashSet<string> Functions = ["count", "collect", "min", "max", "id", "type", "labels", "coalesce", "tolower", "toupper", "visible"];

        private Expr Function(P.OC_FunctionInvocationContext f)
        {
            var name = Text(f.oC_FunctionName()).Replace(" ", "").ToLowerInvariant();
            if (!Functions.Contains(name)) throw Unsupported(f, $"the function {Text(f.oC_FunctionName())}()");
            if (f.DISTINCT() is not null) throw Unsupported(f, $"DISTINCT inside {name}()");
            var args = f.oC_Expression().Select(Expression).ToList();
            var arity = name == "coalesce" ? args.Count >= 1 : args.Count == 1;
            if (!arity) throw new CypherException($"{name}() takes {(name == "coalesce" ? "at least one argument" : "one argument")}", f.Start.Line, f.Start.Column, f.Start.StartIndex);
            var call = new FunctionExpr(name, args) { Offset = f.Start.StartIndex };
            if (call.IsAggregate && args.Any(ContainsAggregate)) throw Unsupported(f, "an aggregate inside an aggregate");
            return call;
        }

        public static bool ContainsAggregate(Expr e)
        {
            // A long chain (NOT NOT …, a AND b AND …) is a deep tree with no bracket in it: stop before the stack runs out.
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            return Aggregates(e);
        }

        private static bool Aggregates(Expr e) => e switch
        {
            FunctionExpr f => f.IsAggregate || f.Args.Any(ContainsAggregate),
            CountStarExpr => true,
            BinaryExpr b => ContainsAggregate(b.Left) || ContainsAggregate(b.Right),
            NotExpr n => ContainsAggregate(n.Operand),
            IsNullExpr i => ContainsAggregate(i.Operand),
            PropertyExpr p => ContainsAggregate(p.Target),
            LabelExpr l => ContainsAggregate(l.Target),
            ListExpr l => l.Items.Any(ContainsAggregate),
            _ => false,
        };

        private Expr Literal(P.OC_LiteralContext lit)
        {
            if (lit.oC_BooleanLiteral() is { } b) return new LiteralExpr(b.TRUE() is not null);
            if (lit.NULL() is not null) return new LiteralExpr(null);
            if (lit.oC_NumberLiteral() is { } n)
            {
                if (n.oC_DoubleLiteral() is { } d) return new LiteralExpr(double.Parse(d.GetText(), CultureInfo.InvariantCulture));
                var i = n.oC_IntegerLiteral();
                var text = i.GetText();
                try
                {
                    return new LiteralExpr(i.HexInteger() is not null ? Convert.ToInt64(text[2..], 16)
                        : i.OctalInteger() is not null ? Convert.ToInt64(text[2..], 8)
                        : long.Parse(text, CultureInfo.InvariantCulture));
                }
                catch (OverflowException) { throw new CypherException($"integer {text} is too large", i.Start.Line, i.Start.Column, i.Start.StartIndex); }
            }
            if (lit.StringLiteral() is { } s) return new LiteralExpr(Unescape(s.GetText()));
            if (lit.oC_ListLiteral() is { } list) return new ListExpr(list.oC_Expression().Select(Expression).ToList());
            throw Unsupported(lit, "map literals in expressions");
        }

        private static string Unescape(string quoted)
        {
            var s = quoted[1..^1];
            var sb = new StringBuilder(s.Length);
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                var c = s[++i];
                switch (c)
                {
                    case 'b' or 'B': sb.Append('\b'); break;
                    case 'f' or 'F': sb.Append('\f'); break;
                    case 'n' or 'N': sb.Append('\n'); break;
                    case 'r' or 'R': sb.Append('\r'); break;
                    case 't' or 'T': sb.Append('\t'); break;
                    case 'u' or 'U':
                        // 4 or 8 hex digits after u or U (the grammar allows both lengths with either case).
                        var len = s.Length - (i + 1) >= 8 && s[(i + 1)..(i + 9)].All(Uri.IsHexDigit) ? 8 : 4;
                        sb.Append(char.ConvertFromUtf32(Convert.ToInt32(s[(i + 1)..(i + 1 + len)], 16)));
                        i += len;
                        break;
                    default: sb.Append(c); break; // \\ \' \"
                }
            }
            return sb.ToString();
        }

        private static string Variable(P.OC_VariableContext v) => Symbolic(v.oC_SymbolicName());

        private static string Symbolic(P.OC_SymbolicNameContext s) => Unquote(s.GetText());

        private static string Name(P.OC_SchemaNameContext s) => Unquote(s.GetText());

        /// <summary>`escaped names`: backticks removed, a doubled backtick is one backtick.</summary>
        private static string Unquote(string name) =>
            name.Length >= 2 && name[0] == '`' && name[^1] == '`' ? name[1..^1].Replace("``", "`") : name;
    }
}
