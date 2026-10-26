using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Traces and metrics to Application Insights with Entra ID auth (managed identity).
var appInsights = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(appInsights))
{
    var credential = new DefaultAzureCredential();
    builder.Services.AddOpenTelemetry()
        .WithTracing(t => t.AddSource(WorkerTelemetry.Name)
            .AddAzureMonitorTraceExporter(o => { o.ConnectionString = appInsights; o.Credential = credential; }))
        .WithMetrics(m => m.AddMeter(WorkerTelemetry.Name)
            .AddAzureMonitorMetricExporter(o => { o.ConnectionString = appInsights; o.Credential = credential; }));
}

builder.Services.AddHostedService<QueueWorker>();
await builder.Build().RunAsync();

public record JobEnvelope(Guid JobId, string TenantId, string Address, DateTimeOffset SubmittedAt);

public static class WorkerTelemetry
{
    public const string Name = "TechSolutions.Worker";
    public static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);

    /// <summary>Time spent in one stage (pre | inference | post). Tags: stage, tenantId, outcome.</summary>
    public static readonly Histogram<double> StageDuration = Meter.CreateHistogram<double>("stage.duration", unit: "ms");

    /// <summary>Submission to final state. Emitted once per job by whichever stage ends it. Tags: tenantId, outcome.</summary>
    public static readonly Histogram<double> JobE2EDuration = Meter.CreateHistogram<double>("job.e2e.duration", unit: "ms");
}

public sealed class QueueWorker(IConfiguration config, ILogger<QueueWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ns = config["ServiceBus:Namespace"] ?? throw new InvalidOperationException("ServiceBus namespace missing");
        var queue = config["ServiceBus:Queue"] ?? "preprocessing-requests";
        var stage = config["Worker:Stage"] ?? "preprocessing";
        await using var client = new ServiceBusClient(ns, new DefaultAzureCredential());
        await using var processor = client.CreateProcessor(queue, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 2,
            PrefetchCount = 4
        });
        processor.ProcessMessageAsync += async e =>
        {
            // Continue the trace started by the API (W3C traceparent carried by the Service Bus SDK as Diagnostic-Id).
            ActivityContext.TryParse(e.Message.ApplicationProperties.TryGetValue("Diagnostic-Id", out var parent) ? parent as string : null,
                null, out var parentContext);
            using var activity = WorkerTelemetry.Source.StartActivity($"process {stage}", ActivityKind.Consumer, parentContext);
            var clock = Stopwatch.StartNew();

            JobEnvelope job;
            try
            {
                job = e.Message.Body.ToObjectFromJson<JobEnvelope>() ?? throw new JsonException("Invalid job payload");
                if (job.JobId == Guid.Empty || string.IsNullOrWhiteSpace(job.TenantId) || string.IsNullOrWhiteSpace(job.Address))
                    throw new JsonException("Missing required job fields");
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Invalid incoming message; dead-lettering");
                await e.DeadLetterMessageAsync(e.Message, "InvalidPayload", "Malformed JSON or missing required fields", cancellationToken: e.CancellationToken);
                Record(stage, "unknown", "invalid_payload", clock.Elapsed, submittedAt: null);
                return;
            }
            activity?.SetTag("jobId", job.JobId).SetTag("tenantId", job.TenantId);

            // Settlement errors propagate to the processor; they are not invalid payloads.
            // Do NOT mark complete until the idempotent durable result has been committed.
            logger.LogWarning("SAMPLE ONLY: Job {JobId} for tenant {TenantId} requires production handlers", job.JobId, job.TenantId);
            await e.DeadLetterMessageAsync(e.Message, "NotImplemented", "Pipeline connectors are not implemented in the reference skeleton", cancellationToken: e.CancellationToken);
            Record(stage, job.TenantId, "failed", clock.Elapsed, job.SubmittedAt); // dead-letter ends the job
        };
        processor.ProcessErrorAsync += e => { logger.LogError(e.Exception, "Service Bus processor error"); return Task.CompletedTask; };
        await processor.StartProcessingAsync(stoppingToken);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
        await processor.StopProcessingAsync(CancellationToken.None);
    }

    /// <summary>
    /// Records the stage duration and, when the job reaches a final state here (completed at the last stage,
    /// or failed at any stage), the end-to-end duration.
    /// </summary>
    private static void Record(string stage, string tenantId, string outcome, TimeSpan elapsed, DateTimeOffset? submittedAt)
    {
        WorkerTelemetry.StageDuration.Record(elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("stage", stage),
            new KeyValuePair<string, object?>("tenantId", tenantId),
            new KeyValuePair<string, object?>("outcome", outcome));
        if (submittedAt is { } submitted)
            WorkerTelemetry.JobE2EDuration.Record((DateTimeOffset.UtcNow - submitted).TotalMilliseconds,
                new KeyValuePair<string, object?>("tenantId", tenantId),
                new KeyValuePair<string, object?>("outcome", outcome));
    }
}
