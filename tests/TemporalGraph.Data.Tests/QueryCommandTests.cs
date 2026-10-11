using TemporalGraph.Cli;

namespace TemporalGraph.Data.Tests;

public class QueryCommandTests
{
    [Theory]
    [InlineData("3", 3L)]
    [InlineData("-2", -2L)]
    [InlineData("1.5", 1.5)]
    [InlineData("true", true)]     // --param spoilers=true binds a boolean, as $spoilers needs
    [InlineData("false", false)]
    [InlineData("True", "True")]   // only the lower-case literals
    [InlineData("Watson", "Watson")]
    public void Parameter_values_become_numbers_booleans_or_text(string text, object expected) =>
        Assert.Equal(expected, QueryCommand.ParseValue(text));
}
