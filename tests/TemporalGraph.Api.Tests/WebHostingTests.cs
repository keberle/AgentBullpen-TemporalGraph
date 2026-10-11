using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TemporalGraph.Api.Tests;

// The API hosting a built web app (Web:Root) with the history fallback for client routes.
public sealed class WebHostingTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string dist = Directory.CreateTempSubdirectory("tg-web-").FullName;

    public void Dispose() => Directory.Delete(dist, recursive: true);

    private HttpClient Client(string? root)
    {
        File.WriteAllText(Path.Combine(dist, "index.html"), "<!doctype html><div id=\"root\"></div>");
        Directory.CreateDirectory(Path.Combine(dist, "assets"));
        File.WriteAllText(Path.Combine(dist, "assets", "app.js"), "console.log(1)");
        return factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            if (root is not null) b.UseSetting("Web:Root", root);
        }).CreateClient();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/book/hound-of-the-baskervilles")]
    [InlineData("/book/hound-of-the-baskervilles/3d?ch=7&spoilers=1&chat=dr-watson")]
    [InlineData("/book/hound-of-the-baskervilles/query?facts=1")]
    public async Task Client_routes_fall_back_to_index_html(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await Client(dist).GetAsync(path, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<div id=\"root\">", await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Assets_are_served_missing_files_stay_404_and_graphql_still_answers()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = Client(dist);
        Assert.Equal("console.log(1)", await client.GetStringAsync("/assets/app.js", ct));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/assets/missing.js", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/books/nope.webp", ct)).StatusCode);
        var gql = await client.PostAsJsonAsync("/graphql", new { query = "{ books { id } }" }, ct);
        Assert.Equal(HttpStatusCode.OK, gql.StatusCode);
        Assert.Contains("hound-of-the-baskervilles", await gql.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Without_a_built_app_nothing_is_hosted()
    {
        // The default (no Web:Root, no wwwroot/index.html): the API is GraphQL only, as before.
        var ct = TestContext.Current.CancellationToken;
        var client = Client(null);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/book/hound-of-the-baskervilles", ct)).StatusCode);
        Assert.Null(WebHosting.Root(new ConfigurationBuilder().Build(), Path.GetTempPath()));
        Assert.Equal(dist, WebHosting.Root(new ConfigurationBuilder().AddInMemoryCollection([new("Web:Root", dist)]).Build(), "/elsewhere"));
    }
}
