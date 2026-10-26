using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using TechSolutions.Api;

var builder = WebApplication.CreateBuilder(args);
var queueName = builder.Configuration["ServiceBus:Queue"] ?? "preprocessing-requests";
var namespaceName = builder.Configuration["ServiceBus:Namespace"];
var demoMode = builder.Configuration.GetValue("DemoMode", false);
if (demoMode && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("DemoMode is only allowed in Development");

// Telemetry to Application Insights with Entra ID auth (managed identity); no instrumentation key auth.
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
{
    builder.Services.AddOpenTelemetry()
        .UseAzureMonitor(o => o.Credential = new DefaultAzureCredential())
        .WithMetrics(m => m.AddMeter(ApiTelemetry.MeterName));
}

var health = builder.Services.AddHealthChecks();
if (!demoMode)
{
    if (string.IsNullOrWhiteSpace(namespaceName)) throw new InvalidOperationException("ServiceBus:Namespace is required");
    builder.Services.AddSingleton(_ => new ServiceBusClient(namespaceName, new DefaultAzureCredential()));
    builder.Services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateSender(queueName));
    builder.Services.AddSingleton<ServiceBusReadinessCheck>();
    health.AddCheck<ServiceBusReadinessCheck>("servicebus", tags: ["ready"]);
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Auth:Authority"] ?? throw new InvalidOperationException("Auth:Authority is required");
        options.Audience = builder.Configuration["Auth:Audience"] ?? throw new InvalidOperationException("Auth:Audience is required");
        options.MapInboundClaims = false;
        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.ValidateAudience = true;
    });
    builder.Services.AddAuthorization(options => options.AddPolicy("B2B", p => p.RequireAuthenticatedUser().RequireClaim("roles", "TechSolutions.Submit")));
}
else
{
    builder.Services.AddAuthorization();
}

var app = builder.Build();
if (!demoMode) app.UseAuthentication();
app.UseAuthorization();

// Liveness: the process answers. Readiness: dependencies answer (Service Bus peek, cached 15 s).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

// Demo only: in-memory state that disappears on restart.
// Production uses the jobs table + transactional outbox in PostgreSQL (ADR-04).
var demoJobs = new ConcurrentDictionary<Guid, StoredJob>();

var submit = app.MapPost("/v1/evaluations", async (EvaluationRequest request, HttpContext http, IServiceProvider services, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Address) || request.Address.Length > 500)
        return Results.BadRequest(new { error = "address_required_or_invalid" });

    var idempotencyKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
    if (idempotencyKey is not null && (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 64))
        return Results.BadRequest(new { error = "idempotency_key_invalid" });

    var tenant = ResolveTenant(http, demoMode);
    if (string.IsNullOrWhiteSpace(tenant)) return Results.Forbid();
    Activity.Current?.SetTag("tenantId", tenant);

    // Same tenant + same Idempotency-Key => same jobId. A client retry never creates an orphan job.
    var jobId = idempotencyKey is null ? Guid.NewGuid() : JobIds.FromIdempotencyKey(tenant, idempotencyKey);
    var statusUrl = $"/v1/evaluations/{jobId}";

    if (demoMode)
    {
        var stored = demoJobs.GetOrAdd(jobId, _ =>
        {
            ApiTelemetry.JobsAccepted.Add(1, new KeyValuePair<string, object?>("tenantId", tenant));
            return new StoredJob(tenant, new JobResponse(jobId, "accepted", statusUrl, DateTimeOffset.UtcNow));
        });
        return Results.Accepted(statusUrl, stored.Job);
    }

    // Reference only: production writes job + outbox row in one transaction and a relay publishes (ADR-04).
    var accepted = new JobResponse(jobId, "accepted", statusUrl, DateTimeOffset.UtcNow);
    var envelope = new JobEnvelope(jobId, tenant, request.Address, accepted.SubmittedAt);
    var message = new ServiceBusMessage(JsonSerializer.Serialize(envelope))
    {
        MessageId = jobId.ToString(), // Service Bus duplicate detection drops retries inside its window
        CorrelationId = http.Request.Headers["x-correlation-id"].FirstOrDefault() ?? jobId.ToString(),
        ContentType = "application/json",
        ApplicationProperties = { ["tenantId"] = tenant }
    };
    await services.GetRequiredService<ServiceBusSender>().SendMessageAsync(message, ct);
    ApiTelemetry.JobsAccepted.Add(1, new KeyValuePair<string, object?>("tenantId", tenant));
    return Results.Accepted(statusUrl, accepted);
});

var status = app.MapGet("/v1/evaluations/{id:guid}", (Guid id, HttpContext http) =>
    FindJob(id, http) is { } job ? Results.Ok(job) : Results.NotFound(new { error = "job_not_found", jobId = id }));

var result = app.MapGet("/v1/evaluations/{id:guid}/result", (Guid id, HttpContext http) =>
{
    if (FindJob(id, http) is not { } job) return Results.NotFound(new { error = "job_not_found", jobId = id });
    // The demo has no pipeline, so jobs never reach "completed". Production reads the result JSON from Blob.
    return job.Status == "completed"
        ? Results.Ok(new { jobId = id })
        : Results.Conflict(new { error = "job_not_completed", jobId = id, status = job.Status });
});

var usage = app.MapGet("/v1/usage", (HttpContext http) =>
{
    var tenant = ResolveTenant(http, demoMode);
    if (!demoMode)
        return Results.Problem("The usage ledger (PostgreSQL) is not part of this reference implementation.", statusCode: 501);
    var jobs = demoJobs.Values.Where(j => j.TenantId == tenant).Select(j => j.Job).ToList();
    return Results.Ok(new UsageResponse(tenant!, jobs.Count, jobs.Count(j => j.Status == "completed"),
        jobs.Count(j => j.Status == "failed"), Billable: jobs.Count(j => j.Status == "completed")));
});

if (!demoMode)
    foreach (var endpoint in new[] { submit, status, result, usage }) endpoint.RequireAuthorization("B2B");

app.Run();

JobResponse? FindJob(Guid id, HttpContext http)
{
    var tenant = ResolveTenant(http, demoMode);
    Activity.Current?.SetTag("tenantId", tenant);
    // Always filtered by tenant: another tenant's job looks exactly like a missing one.
    return demoMode && demoJobs.TryGetValue(id, out var stored) && stored.TenantId == tenant ? stored.Job : null;
}

static string? ResolveTenant(HttpContext http, bool demoMode) => demoMode
    ? http.Request.Headers["x-demo-tenant"].FirstOrDefault() ?? "demo-tenant"
    : http.User.FindFirstValue("azp") ?? http.User.FindFirstValue("appid");

public static class JobIds
{
    /// <summary>Deterministic jobId from tenant + Idempotency-Key (RFC 9562 version-8 layout over SHA-256).</summary>
    public static Guid FromIdempotencyKey(string tenantId, string idempotencyKey)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId}\n{idempotencyKey}"), hash);
        var bytes = hash[..16].ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 9562 variant
        return new Guid(bytes, bigEndian: true);
    }
}

public record EvaluationRequest(string Address);
public record JobResponse(Guid JobId, string Status, string StatusUrl, DateTimeOffset SubmittedAt);
public record StoredJob(string TenantId, JobResponse Job);
public record JobEnvelope(Guid JobId, string TenantId, string Address, DateTimeOffset SubmittedAt);
public record UsageResponse(string TenantId, int Accepted, int Completed, int Failed, int Billable);
public partial class Program { }
