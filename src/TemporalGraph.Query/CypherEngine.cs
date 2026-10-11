namespace TemporalGraph.Query;

/// <summary>A query result: column names, and rows of values (see <see cref="CypherValues"/> for the value types).</summary>
public sealed record QueryResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows);

/// <summary>
/// Runs a query of the supported openCypher subset over a <see cref="BookGraph"/> in memory: parse (ANTLR) → AST
/// (<see cref="AstBuilder"/>) → evaluation. Rows flow clause by clause; without ORDER BY, rows come in match order (nodes
/// and relationships in id order). Errors (syntax, unsupported feature, unknown variable, missing parameter, type error)
/// throw <see cref="CypherException"/> with the position in the query text.
/// </summary>
public static class CypherEngine
{
    /// <param name="cancellationToken">Checked while matching and projecting rows (a timeout cancels the evaluation:
    /// <see cref="OperationCanceledException"/>).</param>
    public static QueryResult Execute(BookGraph graph, string query, IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var ast = AstBuilder.Parse(query);   // refuses a query over the limits (CypherLimits) before parsing it
        var normalized = (parameters ?? new Dictionary<string, object?>()).ToDictionary(kv => kv.Key, kv => CypherValues.FromClr(kv.Value));
        try { return new Evaluator(graph, query, normalized, cancellationToken).Run(ast); }
        catch (InsufficientExecutionStackException) { throw CypherLimits.ChainTooLong(query); }
    }

    private sealed class Evaluator(BookGraph graph, string query, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct)
    {
        private sealed class Row : Dictionary<string, object?>
        {
            public Row() { }
            public Row(IDictionary<string, object?> other) : base(other) { }
        }

        private CypherException Error(Expr? at, string message)
        {
            var offset = at?.Offset is >= 0 and var o ? o : 0;
            var line = 1 + query[..Math.Min(offset, query.Length)].Count(c => c == '\n');
            var lineStart = query.LastIndexOf('\n', Math.Max(0, Math.Min(offset, query.Length) - 1)) + 1;
            if (offset == 0) lineStart = 0;
            return new CypherException(message, line, offset - lineStart, offset);
        }

        public QueryResult Run(QueryAst ast)
        {
            List<Row> rows = [new Row()];
            foreach (var clause in ast.Clauses)
                switch (clause)
                {
                    case MatchClause m: rows = Match(rows, m); break;
                    case WithClause w:
                        rows = Project(rows, w.Projection);
                        if (w.Where is { } where) rows = rows.Where(r => Holds(where, r)).ToList();
                        break;
                    case ReturnClause r:
                        var projected = Project(rows, r.Projection);
                        var columns = r.Projection.Items.Select(i => i.Name).ToList();
                        return new QueryResult(columns, projected.Select(p => (IReadOnlyList<object?>)columns.Select(c => p[c]).ToList()).ToList());
                }
            throw new CypherException("a query must end with RETURN", 1, 0, 0);
        }

        // ---- MATCH ------------------------------------------------------------------------------------

        private List<Row> Match(List<Row> rows, MatchClause m)
        {
            var newVariables = m.Patterns.SelectMany(p => p.Nodes.Select(n => n.Variable).Concat(p.Rels.Select(r => r.Variable)))
                .OfType<string>().Distinct().ToList();
            var result = new List<Row>();
            var conjuncts = Conjuncts(m.Where).ToList();
            foreach (var row in rows)
            {
                var matches = MatchParts(row, m.Patterns, 0, [], conjuncts).Where(r => m.Where is null || Holds(m.Where, r)).ToList();
                if (matches.Count == 0 && m.Optional)
                {
                    var empty = new Row(row);
                    foreach (var v in newVariables) empty.TryAdd(v, null);
                    result.Add(empty);
                }
                else result.AddRange(matches);
            }
            return result;
        }

        /// <summary>All extensions of <paramref name="row"/> matching the comma-separated parts; a relationship is used at most once per MATCH.</summary>
        private IEnumerable<Row> MatchParts(Row row, IReadOnlyList<PatternPart> parts, int index, HashSet<long> used, IReadOnlyList<Expr> conjuncts)
        {
            if (index == parts.Count) { yield return row; yield break; }
            foreach (var (r, u) in MatchPart(row, parts[index], used, conjuncts))
                foreach (var full in MatchParts(r, parts, index + 1, u, conjuncts))
                    yield return full;
        }

        private IEnumerable<(Row, HashSet<long>)> MatchPart(Row row, PatternPart part, HashSet<long> used, IReadOnlyList<Expr> conjuncts)
        {
            var first = part.Nodes[0];
            IEnumerable<NodeValue> starts = first.Variable is { } v && row.TryGetValue(v, out var bound)
                ? bound is NodeValue n ? [n] : []
                : first.Variable is { } unbound && ById(unbound, row, conjuncts) is { } named ? named : graph.Nodes;
            foreach (var start in starts)
            {
                ct.ThrowIfCancellationRequested();
                if (!NodeFits(start, first, row, out var withStart)) continue;
                foreach (var result in Extend(withStart, part, 0, start, used))
                    yield return result;
            }
        }

        /// <summary>The top-level AND parts of a WHERE: a row that passes the WHERE passes each of them.</summary>
        private static IEnumerable<Expr> Conjuncts(Expr? where) => where switch
        {
            null => [],
            BinaryExpr { Op: BinaryOp.And } and => Conjuncts(and.Left).Concat(Conjuncts(and.Right)),
            _ => [where],
        };

        private static bool Bound(Expr e, Row row) => e switch
        {
            LiteralExpr or ParameterExpr => true,
            VariableExpr v => row.ContainsKey(v.Name),
            PropertyExpr p => Bound(p.Target, row),
            ListExpr l => l.Items.All(x => Bound(x, row)),
            _ => false,
        };

        /// <summary>
        /// The start nodes of a pattern whose WHERE names them by id, <c>id(v) IN x.subject</c> or <c>id(v) = x</c>, with the other
        /// side already known: those nodes, in the graph's order, instead of every node of the graph (a Fact's subjects are node ids
        /// in a property, and a book of 3,000 facts and 200 characters is 700,000 pairs otherwise). The WHERE is still applied to
        /// what matches, so this only leaves out rows it would have dropped. Null: no such condition, scan the graph.
        /// </summary>
        private IEnumerable<NodeValue>? ById(string variable, Row row, IReadOnlyList<Expr> conjuncts)
        {
            static bool IdOf(Expr e, string variable) => e is FunctionExpr { Name: "id", Args: [VariableExpr v] } && v.Name == variable;
            foreach (var c in conjuncts.OfType<BinaryExpr>())
            {
                var other = c.Op switch
                {
                    BinaryOp.In when IdOf(c.Left, variable) => c.Right,
                    BinaryOp.Eq when IdOf(c.Left, variable) => c.Right,
                    BinaryOp.Eq when IdOf(c.Right, variable) => c.Left,
                    _ => null,
                };
                if (other is null || !Bound(other, row)) continue;
                var value = Eval(other, row);
                var ids = (c.Op == BinaryOp.In ? value as IReadOnlyList<object?> : [value])?.Select(x => x switch
                {
                    long l => (long?)l, int i => i, double d when d == Math.Floor(d) => (long)d, _ => null,
                }).ToList();
                if (ids is null || ids.Any(id => id is null)) continue;
                return ids.Select(id => id!.Value).Distinct().Order().Select(graph.NodeOrNull).OfType<NodeValue>().ToList();
            }
            return null;
        }

        private IEnumerable<(Row, HashSet<long>)> Extend(Row row, PatternPart part, int step, NodeValue current, HashSet<long> used)
        {
            if (step == part.Rels.Count) { yield return (row, used); yield break; }
            var rp = part.Rels[step];
            var np = part.Nodes[step + 1];
            IEnumerable<(RelationshipValue Rel, long Other)> candidates = rp.Direction switch
            {
                Direction.Right => graph.Outgoing(current.Id).Select(r => (r, r.EndId)),
                Direction.Left => graph.Incoming(current.Id).Select(r => (r, r.StartId)),
                _ => graph.Outgoing(current.Id).Select(r => (r, r.EndId))
                    .Concat(graph.Incoming(current.Id).Where(r => r.StartId != r.EndId).Select(r => (r, r.StartId)))
                    .OrderBy(x => x.r.Id),
            };
            foreach (var (rel, other) in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (used.Contains(rel.Id)) continue;
                if (rp.Types.Count > 0 && !rp.Types.Contains(rel.Type)) continue;
                if (!PropertiesFit(rel.Properties, rp.Properties, row)) continue;
                var next = new Row(row);
                if (rp.Variable is { } rv)
                {
                    if (row.TryGetValue(rv, out var b)) { if (b is not RelationshipValue br || br.Id != rel.Id) continue; }
                    else next[rv] = rel;
                }
                var node = graph.Node(other);
                if (!NodeFits(node, np, next, out var withNode)) continue;
                var nowUsed = new HashSet<long>(used) { rel.Id };
                foreach (var result in Extend(withNode, part, step + 1, node, nowUsed))
                    yield return result;
            }
        }

        private bool NodeFits(NodeValue node, NodePattern p, Row row, out Row extended)
        {
            extended = row;
            if (p.Label is { } label && !node.Labels.Contains(label)) return false;
            if (!PropertiesFit(node.Properties, p.Properties, row)) return false;
            if (p.Variable is { } v)
            {
                if (row.TryGetValue(v, out var bound)) return bound is NodeValue b && b.Id == node.Id;
                extended = new Row(row) { [v] = node };
            }
            return true;
        }

        private bool PropertiesFit(IReadOnlyDictionary<string, object?> props, IReadOnlyList<(string Key, Expr Value)> wanted, Row row) =>
            wanted.All(w => CypherValues.Equal(props.GetValueOrDefault(w.Key), Eval(w.Value, row)) == true);

        // ---- WITH / RETURN ----------------------------------------------------------------------------

        private List<Row> Project(List<Row> rows, Projection p)
        {
            var aggregated = p.Items.Any(i => AstBuilder.ContainsAggregate(i.Expr));
            var outRows = new List<(Row Out, Row Source)>();
            if (!aggregated)
            {
                foreach (var r in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    var o = new Row();
                    foreach (var item in p.Items) o[item.Name] = Eval(item.Expr, r);
                    outRows.Add((o, r));
                }
            }
            else
            {
                var keys = p.Items.Where(i => !AstBuilder.ContainsAggregate(i.Expr)).ToList();
                var groups = new Dictionary<IReadOnlyList<object?>, List<Row>>(CypherValues.RowComparer.Instance);
                var order = new List<IReadOnlyList<object?>>();
                foreach (var r in rows)
                {
                    var key = keys.Select(k => Eval(k.Expr, r)).ToList();
                    if (!groups.TryGetValue(key, out var list)) { groups[key] = list = []; order.Add(key); }
                    list.Add(r);
                }
                if (keys.Count == 0 && rows.Count == 0) { var none = new List<object?>(); groups[none] = []; order.Add(none); }
                foreach (var key in order)
                {
                    var members = groups[key];
                    var sample = members.FirstOrDefault() ?? new Row();
                    var o = new Row();
                    var k = 0;
                    foreach (var item in p.Items)
                        o[item.Name] = AstBuilder.ContainsAggregate(item.Expr) ? EvalAggregate(item.Expr, members, sample) : key[k++];
                    outRows.Add((o, sample));
                }
            }

            if (p.Distinct)
            {
                var seen = new HashSet<IReadOnlyList<object?>>(CypherValues.RowComparer.Instance);
                outRows = outRows.Where(x => seen.Add(p.Items.Select(i => x.Out[i.Name]).ToList())).ToList();
            }

            if (p.Order.Count > 0)
            {
                foreach (var s in p.Order)
                    if (AstBuilder.ContainsAggregate(s.Expr)) throw Error(s.Expr, "not supported: an aggregate in ORDER BY (order by its alias)");
                // Sort keys see the projected names, plus (without aggregation or DISTINCT) the variables before the projection.
                var keyed = outRows.Select(x =>
                {
                    var scope = !aggregated && !p.Distinct ? new Row(x.Source) : new Row();
                    foreach (var (k, v) in x.Out) scope[k] = v;
                    return (x.Out, Keys: p.Order.Select(s => Eval(s.Expr, scope)).ToList());
                }).ToList();
                var comparer = Comparer<List<object?>>.Create((a, b) =>
                {
                    for (var i = 0; i < p.Order.Count; i++)
                    {
                        var c = CypherValues.Order(a[i], b[i]);
                        if (c != 0) return p.Order[i].Descending ? -c : c;
                    }
                    return 0;
                });
                outRows = keyed.OrderBy(x => x.Keys, comparer).Select(x => (x.Out, x.Out)).ToList();
            }

            IEnumerable<Row> result = outRows.Select(x => x.Out);
            if (p.Skip is { } skip) result = result.Skip(Count(skip, "SKIP"));
            if (p.Limit is { } limit) result = result.Take(Count(limit, "LIMIT"));
            return result.ToList();
        }

        private int Count(Expr e, string clause) => Eval(e, new Row()) switch
        {
            long n when n >= 0 && n <= int.MaxValue => (int)n,
            var v => throw Error(e, $"{clause} needs a non-negative integer, got {CypherValues.Display(v)}"),
        };

        private object? EvalAggregate(Expr e, List<Row> members, Row sample) => e switch
        {
            CountStarExpr => (long)members.Count,
            FunctionExpr { Name: "count" } f => (long)members.Count(r => Eval(f.Args[0], r) is not null),
            FunctionExpr { Name: "collect" } f => members.Select(r => Eval(f.Args[0], r)).Where(v => v is not null).ToList(),
            // min / max: nulls are skipped; none left → null. Mixed types order as ORDER BY does.
            FunctionExpr { Name: "min" or "max" } f => MinMax(f, members.Select(r => Eval(f.Args[0], r)).Where(v => v is not null).ToList()),
            FunctionExpr f => CallScalar(f, f.Args.Select(a => AstBuilder.ContainsAggregate(a) ? EvalAggregate(a, members, sample) : Eval(a, sample)).ToList()),
            BinaryExpr b => Binary(b, EvalAggregate(b.Left, members, sample), EvalAggregate(b.Right, members, sample)),
            NotExpr n => Not(n, EvalAggregate(n.Operand, members, sample)),
            IsNullExpr i => (EvalAggregate(i.Operand, members, sample) is null) != i.Negated,
            PropertyExpr p => Property(p, EvalAggregate(p.Target, members, sample)),
            LabelExpr l => HasLabels(l, EvalAggregate(l.Target, members, sample)),
            ListExpr l => l.Items.Select(x => EvalAggregate(x, members, sample)).ToList(),
            _ => Eval(e, sample),
        };

        // ---- Expressions ------------------------------------------------------------------------------

        /// <summary>A WHERE keeps the row only when the predicate is true; null drops it; a non-boolean is a type error.</summary>
        private bool Holds(Expr where, Row row) => Bool(where, Eval(where, row)) == true;

        private object? Eval(Expr e, Row row)
        {
            // A long chain of operators is a deep tree with no bracket in it: stop before the stack runs out (see CypherLimits).
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            return Value(e, row);
        }

        private object? Value(Expr e, Row row) => e switch
        {
            LiteralExpr l => l.Value,
            ListExpr l => l.Items.Select(x => Eval(x, row)).ToList(),
            ParameterExpr p => parameters.TryGetValue(p.Name, out var v) ? v : throw Error(p, $"missing parameter ${p.Name}"),
            VariableExpr v => row.TryGetValue(v.Name, out var val) ? val : throw Error(v, $"variable '{v.Name}' is not defined"),
            PropertyExpr p => Property(p, Eval(p.Target, row)),
            LabelExpr l => HasLabels(l, Eval(l.Target, row)),
            BinaryExpr { Op: BinaryOp.And } b => And(b, Eval(b.Left, row), () => Eval(b.Right, row)),
            BinaryExpr { Op: BinaryOp.Or } b => Or(b, Eval(b.Left, row), () => Eval(b.Right, row)),
            BinaryExpr b => Binary(b, Eval(b.Left, row), Eval(b.Right, row)),
            NotExpr n => Not(n, Eval(n.Operand, row)),
            IsNullExpr i => (Eval(i.Operand, row) is null) != i.Negated,
            FunctionExpr { IsAggregate: true } f => throw Error(f, $"{f.Name}() is only allowed in WITH or RETURN"),
            CountStarExpr => throw Error(e, "count(*) is only allowed in WITH or RETURN"),
            FunctionExpr f => CallScalar(f, f.Args.Select(a => Eval(a, row)).ToList()),
            _ => throw Error(e, "not supported: this expression"),
        };

        private object? Property(PropertyExpr p, object? target) => target switch
        {
            null => null,
            NodeValue n => n.Properties.GetValueOrDefault(p.Key),
            RelationshipValue r => r.Properties.GetValueOrDefault(p.Key),
            IReadOnlyDictionary<string, object?> m => m.GetValueOrDefault(p.Key),
            _ => throw Error(p, $"type error: can't read property '{p.Key}' of {TypeName(target)}"),
        };

        /// <summary><c>n:Label</c>: true when the node has every label; null for null; a type error otherwise.</summary>
        private object? HasLabels(LabelExpr l, object? target) => target switch
        {
            null => null,
            NodeValue n => l.Labels.All(n.Labels.Contains),
            _ => throw Error(l, $"type error: a label predicate needs a node, got {TypeName(target)}"),
        };

        private static object? MinMax(FunctionExpr f, List<object?> values)
        {
            if (values.Count == 0) return null;
            var best = values[0];
            foreach (var v in values.Skip(1))
            {
                var c = CypherValues.Order(v, best);
                if (f.Name == "min" ? c < 0 : c > 0) best = v;
            }
            return best;
        }

        private bool? Bool(Expr at, object? v) => v switch
        {
            null => null,
            bool b => b,
            _ => throw Error(at, $"type error: expected a boolean, got {TypeName(v)}"),
        };

        // Three-valued logic (openCypher): false AND null = false, true OR null = true.
        private object? And(BinaryExpr b, object? left, Func<object?> right)
        {
            var l = Bool(b.Left, left);
            if (l == false) return false;
            var r = Bool(b.Right, right());
            if (r == false) return false;
            return l is null || r is null ? null : true;
        }

        private object? Or(BinaryExpr b, object? left, Func<object?> right)
        {
            var l = Bool(b.Left, left);
            if (l == true) return true;
            var r = Bool(b.Right, right());
            if (r == true) return true;
            return l is null || r is null ? null : false;
        }

        private object? Not(NotExpr n, object? v) => Bool(n.Operand, v) is { } b ? !b : null;

        private object? Binary(BinaryExpr b, object? l, object? r)
        {
            switch (b.Op)
            {
                case BinaryOp.And: return And(b, l, () => r);
                case BinaryOp.Or: return Or(b, l, () => r);
                case BinaryOp.Eq: return CypherValues.Equal(l, r);
                case BinaryOp.Neq: return CypherValues.Equal(l, r) is { } eq ? !eq : null;
                case BinaryOp.Lt: return CypherValues.Compare(l, r) is { } c1 ? c1 < 0 : null;
                case BinaryOp.Le: return CypherValues.Compare(l, r) is { } c2 ? c2 <= 0 : null;
                case BinaryOp.Gt: return CypherValues.Compare(l, r) is { } c3 ? c3 > 0 : null;
                case BinaryOp.Ge: return CypherValues.Compare(l, r) is { } c4 ? c4 >= 0 : null;
                case BinaryOp.StartsWith: return l is string s1 && r is string p1 ? s1.StartsWith(p1, StringComparison.Ordinal) : null;
                case BinaryOp.Contains: return l is string s2 && r is string p2 ? s2.Contains(p2, StringComparison.Ordinal) : null;
                case BinaryOp.In:
                    if (r is null) return null;
                    if (r is not IReadOnlyList<object?> list) throw Error(b.Right, $"type error: IN needs a list, got {TypeName(r)}");
                    var unknown = false;
                    foreach (var x in list)
                    {
                        var e = CypherValues.Equal(l, x);
                        if (e == true) return true;
                        if (e is null) unknown = true;
                    }
                    return unknown ? null : false;
                default: throw Error(b, $"not supported: {b.Op}");
            }
        }

        private object? CallScalar(FunctionExpr f, IReadOnlyList<object?> args) => f.Name switch
        {
            "id" => args[0] switch { null => null, NodeValue n => n.Id, RelationshipValue r => r.Id, var v => throw Error(f.Args[0], $"type error: id() needs a node or relationship, got {TypeName(v)}") },
            "type" => args[0] switch { null => null, RelationshipValue r => r.Type, var v => throw Error(f.Args[0], $"type error: type() needs a relationship, got {TypeName(v)}") },
            "labels" => args[0] switch { null => null, NodeValue n => n.Labels.Cast<object?>().ToList(), var v => throw Error(f.Args[0], $"type error: labels() needs a node, got {TypeName(v)}") },
            "coalesce" => args.FirstOrDefault(a => a is not null),
            "tolower" => args[0] switch { null => null, string s => s.ToLowerInvariant(), var v => throw Error(f.Args[0], $"type error: toLower() needs a string, got {TypeName(v)}") },
            "toupper" => args[0] switch { null => null, string s => s.ToUpperInvariant(), var v => throw Error(f.Args[0], $"type error: toUpper() needs a string, got {TypeName(v)}") },
            // visible(x) is the spoiler rule for a node or a relationship, from $pos and $spoilers (SpoilerGate).
            "visible" => args[0] switch
            {
                null => null,
                NodeValue n => Visible(f, n.Properties),
                RelationshipValue r => Visible(f, r.Properties),
                var v => throw Error(f.Args[0], $"type error: visible() needs a node or relationship, got {TypeName(v)}"),
            },
            _ => throw Error(f, $"not supported: the function {f.Name}()"),
        };

        private (long Pos, bool Spoilers, string? Story)? _gate;

        /// <summary>The spoiler rule on one element. <c>$pos</c> (an integer) and <c>$spoilers</c> (a boolean) must be set;
        /// <c>story(pos)</c> comes from the book's timeline, once per query.</summary>
        private bool Visible(FunctionExpr f, IReadOnlyDictionary<string, object?> properties)
        {
            if (_gate is null)
            {
                if (!parameters.TryGetValue("pos", out var p) || p is not long pos) throw Error(f, "visible() needs the parameter $pos (an integer)");
                if (!parameters.TryGetValue("spoilers", out var s) || s is not bool spoilers) throw Error(f, "visible() needs the parameter $spoilers (a boolean)");
                _gate = (pos, spoilers, spoilers ? graph.StoryAt(pos) : null);
            }
            var (at, on, story) = _gate.Value;
            return SpoilerGate.Visible(properties, at, on, story);
        }

        private static string TypeName(object? v) => v switch
        {
            null => "null",
            string => "a string",
            bool => "a boolean",
            long => "an integer",
            double => "a float",
            NodeValue => "a node",
            RelationshipValue => "a relationship",
            IReadOnlyList<object?> => "a list",
            IReadOnlyDictionary<string, object?> => "a map",
            _ => v.GetType().Name,
        };
    }
}
