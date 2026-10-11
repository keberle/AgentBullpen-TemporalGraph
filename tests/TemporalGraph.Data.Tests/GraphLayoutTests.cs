using Microsoft.Data.Sqlite;
using TemporalGraph.Cli;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Cli.Setup;
using TemporalGraph.Data;
using TemporalGraph.Query;

namespace TemporalGraph.Data.Tests;

// The graph's layout is computed once by `index layout` (a Node script, web/scripts/layout.mjs) and stored on the nodes.
public class GraphLayoutTests
{
    private static List<(long Id, double? X, double? Y, double? Z, double? X2, double? Y2)> Positions(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, layout_x, layout_y, layout_z, layout2_x, layout2_y FROM nodes ORDER BY id";
        using var r = cmd.ExecuteReader();
        var rows = new List<(long, double?, double?, double?, double?, double?)>();
        double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
        while (r.Read()) rows.Add((r.GetInt64(0), D(1), D(2), D(3), D(4), D(5)));
        return rows;
    }

    private static bool NodeIsInstalled => Prerequisites.Probe().Single(p => p.Name == "node").Ok;

    [Fact]
    public void Index_layout_writes_a_position_for_every_node_and_the_same_ones_when_run_again()
    {
        var root = RepoPaths.FindRoot();
        using var temp = new FullSchemaPipelineTests.TempFolder();
        var path = FullSchemaPipelineTests.BuildSmallRun(temp.Path, out var counts);
        // A file as build-db writes it has the columns, empty: no layout yet.
        Assert.True(BookDatabase.HasLayoutColumns(path));
        Assert.All(Positions(path), p => Assert.Null(p.X));

        var exit = IndexLayoutCommand.Run(root, book: null, db: path);
        if (!NodeIsInstalled)
        {
            Assert.Equal(Exit.NotReady, exit);   // without Node.js the step says so and the file is as it was
            Assert.All(Positions(path), p => Assert.Null(p.X));
            return;
        }
        Assert.Equal(Exit.Ok, exit);
        var first = Positions(path);
        Assert.Equal(counts.Rows["nodes"], first.Count);
        Assert.All(first, p => Assert.True(p.X is not null && p.Y is not null && p.Z is not null && p.X2 is not null && p.Y2 is not null));
        Assert.True(first.Select(p => (p.X, p.Y, p.Z)).Distinct().Count() == first.Count);   // no two nodes on one spot

        // Deterministic: the same seed and tick count on the same graph give the same numbers.
        Assert.Equal(Exit.Ok, IndexLayoutCommand.Run(root, book: null, db: path));
        Assert.Equal(first, Positions(path));

        // The graph a query or the API reads has them, under layoutX … layout2Y.
        var holmes = BookGraph.Load(path).Nodes.Single(n => n.Properties.GetValueOrDefault("name") as string == "Sherlock Holmes");
        var stored = first.Single(p => p.Id == holmes.Id);
        Assert.Equal((stored.X, stored.Y, stored.Z, stored.X2, stored.Y2),
            ((double?)Convert.ToDouble(holmes.Properties["layoutX"]), (double?)Convert.ToDouble(holmes.Properties["layoutY"]), (double?)Convert.ToDouble(holmes.Properties["layoutZ"]),
                (double?)Convert.ToDouble(holmes.Properties["layout2X"]), (double?)Convert.ToDouble(holmes.Properties["layout2Y"])));
    }

    [Fact]
    public void The_step_says_what_is_missing_and_never_fails_the_build()
    {
        using var temp = new FullSchemaPipelineTests.TempFolder();
        var path = FullSchemaPipelineTests.BuildSmallRun(temp.Path, out _);
        // A root without the script (or without the web packages): no layout, a reason, nothing thrown, the file untouched.
        var outcome = GraphLayoutStep.Run(temp.Path, path);
        Assert.False(outcome.Ok);
        Assert.Null(outcome.Error);
        Assert.Contains("layout script is not there", outcome.Missing);
        Assert.StartsWith("layout missing: ", GraphLayoutStep.Line(outcome));
        Assert.All(Positions(path), p => Assert.Null(p.X));
        // The command reports it as not ready (exit 3); a file that is not there, or neither --book nor --db, is bad input (2).
        Assert.Equal(Exit.NotReady, IndexLayoutCommand.Run(temp.Path, book: null, db: path));
        Assert.Equal(Exit.BadInput, IndexLayoutCommand.Run(temp.Path, book: null, db: Path.Combine(temp.Path, "none.sqlite")));
        Assert.Equal(Exit.BadInput, IndexLayoutCommand.Run(temp.Path, book: null, db: null));
        Assert.Equal(Exit.BadInput, IndexLayoutCommand.Run(temp.Path, book: "a", db: "b"));
    }

    [Fact]
    public void A_file_built_before_the_layout_columns_reads_as_one_without_a_layout_and_is_not_changed()
    {
        using var temp = new FullSchemaPipelineTests.TempFolder();
        var path = FullSchemaPipelineTests.BuildSmallRun(temp.Path, out _);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            db.Open();
            foreach (var column in new[] { "layout_x", "layout_y", "layout_z", "layout2_x", "layout2_y" })
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = $"ALTER TABLE nodes DROP COLUMN {column}";
                cmd.ExecuteNonQuery();
            }
        }
        var size = new FileInfo(path).Length;
        Assert.True(BookDatabase.HasCurrentSchema(path));     // still the full schema
        Assert.False(BookDatabase.HasLayoutColumns(path));
        Assert.NotEqual(path, BookDatabase.Readable(path));   // read through an upgraded copy
        var graph = BookGraph.Load(path);
        Assert.All(graph.Nodes, n => Assert.False(n.Properties.ContainsKey("layoutX")));
        Assert.Equal(size, new FileInfo(path).Length);
        Assert.False(BookDatabase.HasLayoutColumns(path));
    }
}
