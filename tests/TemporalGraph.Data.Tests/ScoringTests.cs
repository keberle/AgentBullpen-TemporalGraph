using TemporalGraph.Cli.Scoring;

namespace TemporalGraph.Data.Tests;

// The scorer's matching rules on a small hand-made example. No network, no files.
public class ScoringTests
{
    private static readonly GoldItem[] Gold =
    [
        new(1, "Character", "Sherlock Holmes", ["Holmes", "Mr. Holmes"], 1, "q1"),
        new(2, "Character", "Dr. Watson", ["Watson"], 1, "q2"),
        new(3, "Location", "Baskerville Hall", ["the Hall"], 2, "q3"),
        new(4, "Location", "Lafter Hall", [], 2, "q4"),
        new(5, "Character", "Hugo Baskerville", ["Hugo"], 2, "q5"),
    ];

    private static readonly (string, string)[] Rejected = [("Character", "Lancet")];

    private static readonly RunItem[] Run =
    [
        new(1, "Character", "Mr. Sherlock Holmes", ["Holmes", "Sherlock"], 1, "r1"),   // matches 1 via "Holmes"; 2 wrong aliases, 1 missing
        new(2, "Character", "Holmes’s", [], 1, "r2"),                                   // "holmes" too: gold 1 already taken → missed merge
        new(3, "Location", "Baskerville Hall", ["the Hall", "Lafter Hall"], 2, "r3"),  // matches 3 (same canonical) and 4 → wrong merge
        new(4, "Location", "Watson", [], 1, "r4"),                                      // gold "Watson" is a Character → wrong type
        new(5, "Character", "Lancet", [], 1, "r5"),                                     // rejected in review → out of scope
        new(6, "Character", "the coroner", [], 2, "r6"),                                // nothing like it in gold → spurious
        new(7, "Character", "Hugo", [], 2, "r7"),                                       // matches 5 via alias
    ];

    private static ScoreResult Result => Scorer.Score(Run, Gold, Rejected);

    [Fact]
    public void Matching_is_one_to_one_on_shared_normalized_names_of_the_same_type()
    {
        var r = Result;

        Assert.Equal([(1, 1), (3, 3), (7, 5)], r.Matches.Select(m => (m.RunEntity, m.GoldEntity)).Order());
        Assert.Equal(new Prf(3, 7, 5), r.Overall);
        Assert.Equal(0.4286, r.Overall.Precision);
        Assert.Equal(0.6, r.Overall.Recall);
        Assert.Equal(0.5, r.Overall.F1);
        Assert.Equal(new Prf(2, 5, 3), r.ByType["Character"]);
        Assert.Equal(new Prf(1, 2, 2), r.ByType["Location"]);
    }

    [Fact]
    public void Same_canonical_name_wins_the_match()
    {
        // Run 3 shares "the Hall"/"Lafter Hall" with gold 3 and 4, but its canonical name is gold 3's.
        Assert.Equal(3, Result.Matches.Single(m => m.RunEntity == 3).GoldEntity);
    }

    [Fact]
    public void Alias_errors_list_wrong_and_missing_names_of_matched_entities()
    {
        var holmes = Result.Matches.Single(m => m.RunEntity == 1);
        Assert.Equal(["Mr. Sherlock Holmes", "Sherlock"], holmes.WrongAliases);
        Assert.Equal(["Sherlock Holmes", "Mr. Holmes"], holmes.MissingAliases);

        var hall = Result.Matches.Single(m => m.RunEntity == 3);
        Assert.Equal(["Lafter Hall"], hall.WrongAliases);
        Assert.Empty(hall.MissingAliases);
    }

    [Fact]
    public void Every_failure_gets_the_right_tag()
    {
        var f = Result.Failures;
        string Tags(string name) => string.Join(",", f.Where(x => x.Name == name).Select(x => x.Tag).Distinct().Order());

        Assert.Equal("missed merge,wrong alias", Tags("Sherlock Holmes"));       // split across run 1 and 2; "Sherlock Holmes", "Mr. Holmes" missing
        Assert.Equal("spurious entity", Tags("Holmes’s"));                        // the unmatched half of the split
        Assert.Equal("wrong alias,wrong merge", Tags("Baskerville Hall"));        // also covers Lafter Hall
        Assert.Equal("missed entity", Tags("Lafter Hall"));                       // inside a run entity matched to another gold
        Assert.Equal("wrong type", Tags("Watson"));
        Assert.Equal("missed entity", Tags("Dr. Watson"));                        // only found with the wrong type
        Assert.Equal("out of scope", Tags("Lancet"));
        Assert.Equal("spurious entity", Tags("the coroner"));
        Assert.Equal("", Tags("Hugo"));                                           // clean match
        Assert.All(f, x => Assert.Contains(x.Tag, FailureTags.Scoring));

        Assert.Equal(1, Result.Count(FailureTags.WrongMerge));
        Assert.Equal(1, Result.Count(FailureTags.MissedMerge));
        Assert.Equal(1, Result.Count(FailureTags.WrongType));
        Assert.Equal(2, Result.Count(FailureTags.MissedEntity));
        Assert.Contains("found only with the wrong type", f.Single(x => x.Name == "Dr. Watson").Detail);
    }

    [Fact]
    public void BookNlp_comparison_reports_clusters_entities_and_aliases_by_offset()
    {
        TemporalGraph.Cli.Indexing.Mention M(int start, string text) =>
            new(0, text, TemporalGraph.Cli.Indexing.MentionType.Character, 0, 1, "first", start, start + text.Length, "", new TemporalGraph.Cli.Indexing.EvidenceMatch("exact", 1, 0, 0));
        var ours = new List<TemporalGraph.Cli.Indexing.Entity>
        {
            new(1, "Sherlock Holmes", TemporalGraph.Cli.Indexing.MentionType.Character, ["Holmes", "Sherlock Holmes"], [M(100, "Holmes")], 100, [1]),
            new(2, "the coroner", TemporalGraph.Cli.Indexing.MentionType.Character, ["the coroner"], [M(300, "the coroner")], 300, [2]),
        };
        var theirs = new List<TemporalGraph.Cli.Crosscheck.BookNlpMention>
        {
            new("7", "PER", "PROP", 100, 106, "Holmes"),
            new("7", "PER", "PROP", 150, 160, "Mr. Holmes"),   // same cluster: an alias we lack
            new("9", "PER", "PROP", 200, 206, "Watson"),       // nothing of ours overlaps → not in ours
            new("9", "PER", "PROP", 900, 906, "Watson"),       // outside the slice
        };

        var r = TemporalGraph.Cli.Crosscheck.Comparison.CompareBookNlp(ours, theirs, 0, 500, _ => 1);

        Assert.Equal(["not in ours|Watson", "alias difference|Sherlock Holmes", "not in theirs|the coroner"],
            r.Disagreements.Select(d => $"{d.Kind}|{d.Name}").OrderBy(x => x.Contains("ours|") ? 0 : x.StartsWith("alias") ? 1 : 2));
        Assert.Contains("Mr. Holmes", r.Disagreements.Single(d => d.Kind == "alias difference").Detail);
        Assert.Equal(2, r.TheirNames);
    }

    [Fact]
    public void Possessives_case_and_quotes_normalize_before_matching()
    {
        var r = Scorer.Score([new RunItem(1, "Character", "HOLMES’S", [], 1, "")], [new GoldItem(1, "Character", "Holmes", [], 1, "")], []);
        Assert.Single(r.Matches);
        Assert.Empty(r.Failures);
    }
}
