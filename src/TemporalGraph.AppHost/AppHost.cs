// Aspire AppHost — `aspire run` starts the GraphQL API and the React app.
//
//   api   (ASP.NET Core + HotChocolate GraphQL, book manifest)
//   web   (React + Vite + TypeScript + design kit)
//   cli   (indexing CLI; explicit start: stays stopped until Start is pressed in the dashboard)

var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.TemporalGraph_Api>("api")
    .WithHttpHealthCheck("/health")
    .WithExternalHttpEndpoints();

// The browser only talks to the Vite dev server; Vite proxies /graphql to the
// api (target read from API_HTTP, see web/vite.config.ts).
builder.AddViteApp("web", "../../web")
    .WithReference(api)
    .WaitFor(api)
    .WithEnvironment("API_HTTP", api.GetEndpoint("http"))
    .WithExternalHttpEndpoints();

// The CLI gets OTLP settings from Aspire like any project resource, so its spans and logs show in the
// dashboard. Start runs `index prepare --all`; the CLI finds the repo root itself (runs/, data/books/).
builder.AddProject<Projects.TemporalGraph_Cli>("cli")
    .WithExplicitStart()
    .WithArgs("index", "prepare", "--all");

builder.Build().Run();
