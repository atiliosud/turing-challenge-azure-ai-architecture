using System.Diagnostics.Metrics;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TechSolutions.Api;

public static class ApiTelemetry
{
    public const string MeterName = "TechSolutions.Api";
    private static readonly Meter Meter = new(MeterName);

    /// <summary>Accepted jobs per tenant; feeds the usage panel (the billing ledger stays the source of truth).</summary>
    public static readonly Counter<long> JobsAccepted = Meter.CreateCounter<long>("jobs.accepted", description: "Accepted evaluation jobs");
}

/// <summary>
/// Readiness = the API can actually enqueue work. Peeks the stage-1 queue with a short timeout and caches the
/// result for 15 s, so probes every few seconds do not turn into Service Bus load.
/// </summary>
public sealed class ServiceBusReadinessCheck(ServiceBusClient client, IConfiguration config) : IHealthCheck, IAsyncDisposable
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);
    private readonly ServiceBusReceiver receiver = client.CreateReceiver(config["ServiceBus:Queue"] ?? "preprocessing-requests");
    private (DateTimeOffset At, HealthCheckResult Result)? last;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (last is { } cached && DateTimeOffset.UtcNow - cached.At < CacheFor) return cached.Result;

        HealthCheckResult result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await receiver.PeekMessageAsync(cancellationToken: timeout.Token);
            result = HealthCheckResult.Healthy("Service Bus reachable");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            result = HealthCheckResult.Unhealthy("Service Bus unreachable", ex);
        }
        last = (DateTimeOffset.UtcNow, result);
        return result;
    }

    public ValueTask DisposeAsync() => receiver.DisposeAsync();
}
