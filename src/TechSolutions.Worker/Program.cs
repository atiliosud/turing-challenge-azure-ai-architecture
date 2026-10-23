using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<QueueWorker>();
await builder.Build().RunAsync();

public record JobEnvelope(Guid JobId, string TenantId, string Address, DateTimeOffset SubmittedAt);

public sealed class QueueWorker(IConfiguration config, ILogger<QueueWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ns = config["ServiceBus:Namespace"] ?? throw new InvalidOperationException("ServiceBus namespace missing");
        var queue = config["ServiceBus:Queue"] ?? "inference-requests";
        await using var client = new ServiceBusClient(ns, new DefaultAzureCredential());
        await using var processor = client.CreateProcessor(queue, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false, MaxConcurrentCalls = 2, PrefetchCount = 4
        });
        processor.ProcessMessageAsync += async e =>
        {
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
                return;
            }
            // Settlement errors propagate to the processor; they are not invalid payloads.
            // Do NOT mark complete until the idempotent durable result has been committed.
            logger.LogWarning("SAMPLE ONLY: Job {JobId} for tenant {TenantId} requires production handlers", job.JobId, job.TenantId);
            await e.DeadLetterMessageAsync(e.Message, "NotImplemented", "Pipeline connectors are not implemented in the reference skeleton", cancellationToken: e.CancellationToken);
        };
        processor.ProcessErrorAsync += e => { logger.LogError(e.Exception, "Service Bus processor error"); return Task.CompletedTask; };
        await processor.StartProcessingAsync(stoppingToken);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
        await processor.StopProcessingAsync(CancellationToken.None);
    }
}
