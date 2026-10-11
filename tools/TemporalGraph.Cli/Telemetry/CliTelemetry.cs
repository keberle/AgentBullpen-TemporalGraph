using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace TemporalGraph.Cli.Telemetry;

/// <summary>
/// OpenTelemetry for the CLI: ServiceDefaults' non-ASP.NET part (logs, metrics, traces, OTLP exporter when
/// OTEL_EXPORTER_OTLP_ENDPOINT is set — Aspire sets it) plus the indexing and LLM sources. Without an
/// endpoint nothing is exported; the run folder is the record. Disposing flushes the exporters.
/// </summary>
public sealed class CliTelemetry : IDisposable
{
    private readonly IHost _host;

    public ILoggerFactory LoggerFactory { get; }

    private CliTelemetry(IHost host)
    {
        _host = host;
        LoggerFactory = host.Services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
    }

    /// <summary>Set by Program for the commands. Null-object loggers if telemetry wasn't started (tests).</summary>
    public static CliTelemetry? Current { get; private set; }

    public static ILogger Logger(string category) => Current?.LoggerFactory.CreateLogger(category) ?? NullLogger.Instance;

    public static CliTelemetry Start(IConfiguration configuration)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "TemporalGraph.Cli",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.AddConfiguration(configuration);
        // A one-shot CLI: no "Application started. Press Ctrl+C…" / "Hosting environment" lines in the logs.
        builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);

        builder.ConfigureOpenTelemetryCore();
        builder.Services.AddOpenTelemetry()
            .WithTracing(t => t.AddSource(IndexingTelemetry.SourceName, IndexingTelemetry.LlmSourceName, TemporalGraph.Chat.ChatTelemetry.SourceName))
            .WithMetrics(m => m.AddMeter(IndexingTelemetry.LlmSourceName, TemporalGraph.Chat.ChatTelemetry.SourceName));

        var host = builder.Build();
        host.Start(); // creates the tracer / meter providers
        return Current = new CliTelemetry(host);
    }

    public void Dispose()
    {
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose(); // flushes the OTLP exporters
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
