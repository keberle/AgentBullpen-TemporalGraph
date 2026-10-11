using System.Globalization;

namespace TemporalGraph.Query;

// Query values: null, bool, long, double, string, IReadOnlyList<object?> (list), IReadOnlyDictionary<string, object?> (map,
// only as a property value), NodeValue and RelationshipValue.

/// <summary>A node of the graph, as a query value.</summary>
public sealed record NodeValue(long Id, IReadOnlyList<string> Labels, IReadOnlyDictionary<string, object?> Properties)
{
    public bool Equals(NodeValue? other) => other is not null && other.Id == Id;
    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>A relationship (edge) of the graph, as a query value. <paramref name="StartId"/> → <paramref name="EndId"/>.</summary>
public sealed record RelationshipValue(long Id, string Type, long StartId, long EndId, IReadOnlyDictionary<string, object?> Properties)
{
    public bool Equals(RelationshipValue? other) => other is not null && other.Id == Id;
    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>
/// Comparison, equality, orderability and equivalence of query values, after the openCypher CIP
/// "Define comparability and equality as well as orderability and equivalence" (CIP2016-06-14), which the openCypher 9
/// reference adopts:
/// <list type="bullet">
/// <item><b>Equality</b> (<c>=</c>, <c>&lt;&gt;</c>, <c>IN</c>): null if either side is null; values of different types are
/// not equal (false); numbers compare by value (1 = 1.0); lists compare element by element, null if an element comparison
/// is null and none is false; nodes and relationships by id.</item>
/// <item><b>Comparability</b> (<c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>): defined for two numbers, two strings
/// (ordinal) or two booleans (false &lt; true); anything else, or null, gives null.</item>
/// <item><b>Orderability</b> (ORDER BY): a total order: map &lt; node &lt; relationship &lt; list &lt; string &lt; boolean
/// &lt; number &lt; null, so nulls sort last ascending and first descending.</item>
/// <item><b>Equivalence</b> (DISTINCT, grouping keys): like equality, but null is equivalent to null.</item>
/// </list>
/// </summary>
public static class CypherValues
{
    public static bool IsNumber(object? v) => v is long or double;

    private static double D(object v) => v is long l ? l : (double)v;

    /// <summary>True, false, or null (unknown).</summary>
    public static bool? Equal(object? a, object? b)
    {
        if (a is null || b is null) return null;
        if (IsNumber(a) && IsNumber(b)) return a is long la && b is long lb ? la == lb : D(a) == D(b);
        switch (a)
        {
            case string sa when b is string sb: return sa == sb;
            case bool ba when b is bool bb: return ba == bb;
            case NodeValue na when b is NodeValue nb: return na.Id == nb.Id;
            case RelationshipValue ra when b is RelationshipValue rb: return ra.Id == rb.Id;
            case IReadOnlyList<object?> xa when b is IReadOnlyList<object?> xb:
                if (xa.Count != xb.Count) return false;
                var unknown = false;
                for (var i = 0; i < xa.Count; i++)
                {
                    var e = Equal(xa[i], xb[i]);
                    if (e == false) return false;
                    if (e is null) unknown = true;
                }
                return unknown ? null : true;
            case IReadOnlyDictionary<string, object?> ma when b is IReadOnlyDictionary<string, object?> mb:
                if (ma.Count != mb.Count || ma.Keys.Any(k => !mb.ContainsKey(k))) return false;
                var unknownMap = false;
                foreach (var (k, v) in ma)
                {
                    var e = Equal(v, mb[k]);
                    if (e == false) return false;
                    if (e is null) unknownMap = true;
                }
                return unknownMap ? null : true;
            default: return false;
        }
    }

    /// <summary>Negative, zero or positive for comparable values; null when they aren't comparable (or either is null).</summary>
    public static int? Compare(object? a, object? b)
    {
        if (a is null || b is null) return null;
        if (IsNumber(a) && IsNumber(b))
        {
            if (a is long la && b is long lb) return la.CompareTo(lb);
            var (da, db) = (D(a), D(b));
            if (double.IsNaN(da) || double.IsNaN(db)) return null;
            return da.CompareTo(db);
        }
        if (a is string sa && b is string sb) return Math.Sign(string.CompareOrdinal(sa, sb));
        if (a is bool ba && b is bool bb) return ba.CompareTo(bb);
        return null;
    }

    private static int Rank(object? v) => v switch
    {
        IReadOnlyDictionary<string, object?> => 0,
        NodeValue => 1,
        RelationshipValue => 2,
        IReadOnlyList<object?> => 3,
        string => 5,
        bool => 6,
        long or double => 7,
        null => 9,
        _ => 8,
    };

    /// <summary>The total order used by ORDER BY (ascending).</summary>
    public static int Order(object? a, object? b)
    {
        var (ra, rb) = (Rank(a), Rank(b));
        if (ra != rb) return ra.CompareTo(rb);
        switch (a)
        {
            case null: return 0;
            case NodeValue na: return na.Id.CompareTo(((NodeValue)b!).Id);
            case RelationshipValue rel: return rel.Id.CompareTo(((RelationshipValue)b!).Id);
            case IReadOnlyList<object?> la:
                var lb = (IReadOnlyList<object?>)b!;
                for (var i = 0; i < Math.Min(la.Count, lb.Count); i++)
                {
                    var c = Order(la[i], lb[i]);
                    if (c != 0) return c;
                }
                return la.Count.CompareTo(lb.Count);
            case IReadOnlyDictionary<string, object?> ma:
                var mb = (IReadOnlyDictionary<string, object?>)b!;
                return ma.Count.CompareTo(mb.Count);
            default:
                if (IsNumber(a))
                {
                    var (da, db) = (D(a), D(b!));
                    // NaN sorts after every other number.
                    if (double.IsNaN(da) || double.IsNaN(db)) return double.IsNaN(da).CompareTo(double.IsNaN(db));
                    return da.CompareTo(db);
                }
                return Compare(a, b) ?? 0;
        }
    }

    /// <summary>Equivalence (DISTINCT, grouping): equality where null ≡ null.</summary>
    public static bool Equivalent(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is IReadOnlyList<object?> la && b is IReadOnlyList<object?> lb)
            return la.Count == lb.Count && la.Zip(lb).All(p => Equivalent(p.First, p.Second));
        return Equal(a, b) == true;
    }

    /// <summary>Equivalence over a whole row of values, for DISTINCT and grouping.</summary>
    public sealed class RowComparer : IEqualityComparer<IReadOnlyList<object?>>
    {
        public static readonly RowComparer Instance = new();
        public bool Equals(IReadOnlyList<object?>? x, IReadOnlyList<object?>? y) =>
            x is not null && y is not null && x.Count == y.Count && x.Zip(y).All(p => Equivalent(p.First, p.Second));
        public int GetHashCode(IReadOnlyList<object?> row) => row.Aggregate(17, (h, v) => h * 31 + Hash(v));
        private static int Hash(object? v) => v switch
        {
            null => 0,
            long l => ((double)l).GetHashCode(),
            double d => d.GetHashCode(),
            IReadOnlyList<object?> list => list.Aggregate(19, (h, x) => h * 31 + Hash(x)),
            IReadOnlyDictionary<string, object?> m => m.Count,
            _ => v.GetHashCode(),
        };
    }

    /// <summary>A parameter or property value converted to a query value (int → long, float → double, arrays → lists).</summary>
    public static object? FromClr(object? v) => v switch
    {
        null => null,
        string or bool or long or double => v,
        int i => (long)i,
        short s => (long)s,
        byte b => (long)b,
        float f => (double)f,
        decimal m => (double)m,
        NodeValue or RelationshipValue => v,
        IReadOnlyDictionary<string, object?> map => map,
        System.Collections.IEnumerable e => e.Cast<object?>().Select(FromClr).ToList(),
        _ => throw new ArgumentException($"Unsupported parameter value type {v.GetType().Name}."),
    };

    /// <summary>A short text form for tables: lists joined with ", ", nodes as (:Label {name}), relationships as [:TYPE].</summary>
    public static string Display(object? v) => v switch
    {
        null => "null",
        string s => s,
        bool b => b ? "true" : "false",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        NodeValue n => $"(:{string.Join(":", n.Labels)} {{{(n.Properties.TryGetValue("name", out var name) && name is not null ? Display(name) : n.Properties.TryGetValue("text", out var text) && text is not null ? Display(text) : $"id {n.Id}")}}})",
        RelationshipValue r => $"[:{r.Type} {r.StartId}→{r.EndId}]",
        IReadOnlyList<object?> list => string.Join(", ", list.Select(Display)),
        IReadOnlyDictionary<string, object?> m => "{" + string.Join(", ", m.Select(kv => $"{kv.Key}: {Display(kv.Value)}")) + "}",
        _ => v.ToString() ?? "",
    };
}
