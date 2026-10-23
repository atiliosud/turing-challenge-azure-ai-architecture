using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Authentication.JwtBearer;

var builder = WebApplication.CreateBuilder(args);
var queueName = builder.Configuration["ServiceBus:Queue"] ?? "inference-requests";
var namespaceName = builder.Configuration["ServiceBus:Namespace"];
var demoMode = builder.Configuration.GetValue("DemoMode", false);
if (demoMode && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("DemoMode is only allowed in Development");

builder.Services.AddHealthChecks();
if (!demoMode)
{
    if (string.IsNullOrWhiteSpace(namespaceName)) throw new InvalidOperationException("ServiceBus:Namespace is required");
    builder.Services.AddSingleton(_ => new ServiceBusClient(namespaceName, new DefaultAzureCredential()));
    builder.Services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateSender(queueName));
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
app.MapHealthChecks("/health/live");
app.MapGet("/health/ready", () => demoMode
    ? Results.Ok(new { ready = true, mode = "demo" })
    : Results.Problem("Dependency readiness checks are not implemented", statusCode: 503));

// Demonstration only: this state disappears when the process restarts.
// Production design uses a transactional job/outbox repository, not a dictionary.
var demoJobs = new ConcurrentDictionary<Guid, JobResponse>();

var submit = app.MapPost("/v1/evaluations", async (EvaluationRequest request, HttpContext http, IServiceProvider services, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Address) || request.Address.Length > 500)
        return Results.BadRequest(new { error = "address_required_or_invalid" });
    if (request.IdempotencyKey is not null && (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 64))
        return Results.BadRequest(new { error = "idempotency_key_invalid" });
    var tenant = demoMode ? "demo-tenant" : http.User.FindFirstValue("azp") ?? http.User.FindFirstValue("appid");
    if (string.IsNullOrWhiteSpace(tenant)) return Results.Forbid();
    var jobId = Guid.NewGuid();
    var accepted = new JobResponse(jobId, "accepted", DateTimeOffset.UtcNow);
    if (demoMode) demoJobs[jobId] = accepted;
    else
    {
        // Not atomic with a durable status store: see ADR-004 and production backlog.
        var envelope = new JobEnvelope(jobId, tenant, request.Address, DateTimeOffset.UtcNow);
        var message = new ServiceBusMessage(JsonSerializer.Serialize(envelope))
        {
            MessageId = $"{tenant}:{request.IdempotencyKey ?? jobId.ToString()}",
            CorrelationId = jobId.ToString(), ContentType = "application/json",
            ApplicationProperties = { ["tenantId"] = tenant }
        };
        await services.GetRequiredService<ServiceBusSender>().SendMessageAsync(message, ct);
    }
    return Results.Accepted($"/v1/evaluations/{jobId}", accepted);
});
if (!demoMode) submit.RequireAuthorization("B2B");

var status = app.MapGet("/v1/evaluations/{id:guid}", (Guid id) =>
    demoMode && demoJobs.TryGetValue(id, out var job)
        ? Results.Ok(job) : Results.NotFound(new { error = "not_available_in_sample", jobId = id }));
if (!demoMode) status.RequireAuthorization("B2B");

app.Run();

public record EvaluationRequest(string Address, string? IdempotencyKey);
public record JobResponse(Guid JobId, string Status, DateTimeOffset SubmittedAt);
public record JobEnvelope(Guid JobId, string TenantId, string Address, DateTimeOffset SubmittedAt);
public partial class Program { }
