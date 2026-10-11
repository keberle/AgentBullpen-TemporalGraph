using Microsoft.Extensions.Configuration;
using TemporalGraph.Cli;
using TemporalGraph.Cli.Runs;
using TemporalGraph.Data;

namespace TemporalGraph.Data.Tests;

// `chat vet` against FakeLlmServer, in a repo-shaped temp folder with the committed Hound and Treasure Island graphs.
public sealed class ChatVetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TemporalGraph.Data.Tests", Guid.NewGuid().ToString("N"));

    public ChatVetTests()
    {
        var repo = RepoPaths.FindRoot();
        var books = Directory.CreateDirectory(RepoPaths.Books(_root)).FullName;
        File.Copy(Path.Combine(RepoPaths.Books(repo), BookManifest.FileName), Path.Combine(books, BookManifest.FileName));
        foreach (var id in new[] { "hound-of-the-baskervilles", "treasure-island" })
        {
            File.Copy(Path.Combine(RepoPaths.Books(repo), id + ".sqlite"), Path.Combine(books, id + ".sqlite"));
            var queries = Directory.CreateDirectory(Path.Combine(_root, "queries", id)).FullName;
            foreach (var f in Directory.GetFiles(Path.Combine(repo, "queries", id), "*.cypher")) File.Copy(f, Path.Combine(queries, Path.GetFileName(f)));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static IConfiguration Config(FakeLlmServer server) => new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "templates", "cli.appsettings.json"))
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Llm:Type"] = "OpenAI",
            ["Llm:Model"] = "my-deployment",
            ["Llm:Endpoint"] = server.BaseUrl + "/openai/v1/",
            ["Llm:ApiKey"] = "fake-key-for-tests-0123456789",
        })
        .Build();

    [Fact]
    public void Paragraphs_are_blocks_separated_by_a_blank_line()
    {
        Assert.Equal(1, ChatVetCommand.Paragraphs("One. Two.\nThree."));
        Assert.Equal(2, ChatVetCommand.Paragraphs("One.\n\n  \nTwo.\n"));
    }

    [Fact]
    public void Broad_questions_may_have_three_paragraphs_and_pointed_ones_one()
    {
        string Of(int n) => string.Join("\n\n", Enumerable.Repeat("A short paragraph.", n));
        var questions = ChatVetCommand.VoiceQuestions;   // broad, pointed, broad

        Assert.Equal((true, "paragraphs per answer: 3 / 1 / 2 (at most 3 / 1 / 3)"), ChatVetCommand.Length(questions, [Of(3), Of(1), Of(2)]));
        Assert.False(ChatVetCommand.Length(questions, [Of(1), Of(2), Of(1)]).Passed);   // the pointed question, two paragraphs
        Assert.False(ChatVetCommand.Length(questions, [Of(4), Of(1), Of(1)]).Passed);   // a broad one, four
    }

    [Fact]
    public async Task A_model_that_calls_tools_keeps_the_secrets_and_answers_briefly_passes()
    {
        using var server = new FakeLlmServer();
        // Each of the 14 turns: one tool call, then a one-paragraph answer that names no one; the last (Watson, asked who
        // warned him in his own chapter) names her.
        for (var i = 0; i < 14; i++)
        {
            server.OpenAIMessages.Enqueue(FakeLlmServer.ToolCall($"call_{i}", "characters_so_far", new { }));
            server.OpenAIMessages.Enqueue(FakeLlmServer.Text(i == 13 ? "The lady her brother called Beryl." : "I cannot say yet."));
        }
        var output = new StringWriter();

        var exit = await ChatVetCommand.RunAsync(new ChatVetCommand.Settings(), Config(server), _root, output, TestContext.Current.CancellationToken);

        Assert.True(exit == 0, output.ToString());
        Assert.Contains("pass  tool use: every turn called a tool: 14 of 14 turns", output.ToString());
        Assert.Contains("pass  gate: Hound ch. 7, Holmes has not been told the chapter: tells nothing of it", output.ToString());
        Assert.Contains("pass  gate: Hound ch. 7, Watson knows his own chapter: names her", output.ToString());
        Assert.Contains("info  voice match (blind): skipped: the judge model (Indexing:Llm) is not configured", output.ToString());
        Assert.Contains("All checks passed", output.ToString());
        Assert.Single(Directory.GetDirectories(RepoPaths.Runs(_root)));   // one run folder
    }

    [Fact]
    public async Task A_spoiler_a_long_answer_or_no_tool_call_fails()
    {
        using var server = new FakeLlmServer { OpenAIReply = "Mr. Stapleton did it, and the mutineers are coming.\n\nThat is all I know." };
        var output = new StringWriter();

        var exit = await ChatVetCommand.RunAsync(new ChatVetCommand.Settings(), Config(server), _root, output, TestContext.Current.CancellationToken);

        var text = output.ToString();
        Assert.Equal(4, exit);   // A check failed
        Assert.Contains("FAIL  spoiler: Hound ch. 3, the killer: names Stapleton", text);
        Assert.Contains("FAIL  spoiler: Treasure Island ch. 8, Silver: mentions the mutiny", text);
        Assert.Contains("FAIL  voice: Barrymore, ch. 7: paragraphs per answer: 2 / 2 / 2 (at most 3 / 1 / 3)", text);   // "Who is Sir Henry?"
        Assert.Contains("FAIL  tool use: every turn called a tool: 0 of 14 turns", text);
        // Watson, who was there, does not name her; Holmes, who was not, tells nothing of the chapter (this answer names only Stapleton).
        Assert.Contains("FAIL  gate: Hound ch. 7, Watson knows his own chapter: does not name Miss Stapleton (Beryl), whom he met in this chapter", text);
        Assert.Contains("pass  gate: Hound ch. 7, Holmes has not been told the chapter: tells nothing of it", text);
        Assert.Contains("FAIL  spoiler: Treasure Island ch. 8, Silver himself: mentions the mutiny", text);
    }

    [Fact]
    public void The_voice_match_masks_names_shuffles_the_same_way_and_grades_by_character_and_book()
    {
        var names = new List<(string Name, string Mask)> { ("Sherlock Holmes", "[name]"), ("Holmes", "[name]"), ("Baker Street", "[place]"), ("d'Artagnan", "[name]") }
            .OrderByDescending(n => n.Name.Length).ToList();
        Assert.Equal("I am [name], of [place]; [name] speaks so, and [name] too.",
            VoiceMatch.Mask("I am Sherlock Holmes, of Baker Street; Holmes's friend speaks so, and D\u2019Artagnan too.".Replace("Holmes's friend", "Holmes's"), names));

        Assert.Equal(VoiceMatch.Order(9, 48), VoiceMatch.Order(9, 48));
        Assert.Equal(Enumerable.Range(0, 9), VoiceMatch.Order(9, 48).Order());
        Assert.NotEqual(Enumerable.Range(0, 9), VoiceMatch.Order(9, 48));

        var cast = VoiceMatch.Cast;
        var order = Enumerable.Range(0, 9).ToArray();   // slot i holds cast member i
        var picks = VoiceMatch.ParseJudge("""
            1: Sherlock Holmes
            Answer 2: Barrymore
            3. Dr. Watson
            4: **d'Artagnan**
            5: Planchet
            6: Athos
            7: Long John Silver
            9 - Nobody
            """, cast.Select(c => c.Character).ToList());
        var score = VoiceMatch.Grade(cast, order, picks);
        Assert.Equal(3, score.Correct);   // 1, 4, 7 right; 2 and 3 swapped; 5 and 6 swapped; 8 and 9 missing
        Assert.Equal(7, score.SameBook);
        Assert.Equal("characters 3/9 (chance 1/9) · book 7/9 (chance 1/3)", score.Detail);

        var text = VoiceMatch.JudgeText(cast.Select((_, i) => $"answer {i}").ToList(), cast.Select(c => c.Character).ToList(), VoiceMatch.Order(9, 48));
        Assert.Contains("Each character wrote exactly one answer.", text);
        Assert.Contains("Answer 9:", text);
        Assert.DoesNotContain("{names}", text);
    }
}
