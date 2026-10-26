using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Text;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TechSolutions.SyntheticMonitor;

// Container Apps Job (cron) with its own user-assigned managed identity. That identity holds the API app role
// TechSolutions.Submit, so the probe needs no client secret. The Jira token comes from Key Vault as a secret reference.
var builder = Host.CreateApplicationBuilder(args);
var config = builder.Configuration;
var credential = new DefaultAzureCredential();

var appInsights = config["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(appInsights))
{
    builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(ProbeTelemetry.Name)
        .AddAzureMonitorMetricExporter(o => { o.ConnectionString = appInsights; o.Credential = credential; }));
}

using var host = builder.Build();
await host.StartAsync();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SyntheticProbe");
var exitCode = await RunAsync();
await host.StopAsync(); // flushes pending metrics before the job execution ends
return exitCode;

async Task<int> RunAsync()
{
    var probeOptions = config.GetSection("Probe").Get<ProbeOptions>() ?? throw new InvalidOperationException("Probe section missing");
    var jiraOptions = config.GetSection("Jira").Get<JiraOptions>() ?? new JiraOptions();
    var apiBaseUrl = config["Probe:ApiBaseUrl"] ?? throw new InvalidOperationException("Probe:ApiBaseUrl missing");
    var apiScope = config["Probe:ApiScope"] ?? throw new InvalidOperationException("Probe:ApiScope missing");
    var attempts = config.GetValue("Probe:Attempts", 2);

    using var api = new HttpClient { BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/") };
    api.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", config["Probe:SubscriptionKey"]); // "synthetic" product, excluded from billing
    var probe = new E2EProbe(api, async ct => (await credential.GetTokenAsync(new TokenRequestContext([apiScope]), ct)).Token, probeOptions);

    var outcome = ProbeOutcome.Fail("not executed", TimeSpan.Zero);
    for (var attempt = 1; attempt <= attempts; attempt++)
    {
        outcome = await probe.RunAsync(CancellationToken.None);
        ProbeTelemetry.Duration.Record(outcome.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("result", outcome.Success ? "pass" : "fail"));
        // Structured line consumed by the Log Analytics alert rule.
        logger.LogInformation("SyntheticProbe result={Result} attempt={Attempt} elapsedMs={ElapsedMs} jobId={JobId} failure={Failure}",
            outcome.Success ? "Pass" : "Fail", attempt, (long)outcome.Elapsed.TotalMilliseconds, outcome.JobId, outcome.Failure);
        if (outcome.Success) return 0;
    }

    // Ticket only after all attempts fail, to avoid noise from a single transient error.
    try
    {
        using var jira = new HttpClient { BaseAddress = new Uri(config["Jira:BaseUrl"]!.TrimEnd('/') + "/") };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config["Jira:User"]}:{config["Jira:ApiToken"]}"));
        jira.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        var action = await new JiraIncidentClient(jira, jiraOptions).ReportAsync(outcome, CancellationToken.None);
        logger.LogWarning("SyntheticProbe jira={JiraAction}", action);
    }
    catch (Exception ex)
    {
        // Jira being down must not hide the incident: the failed execution still fires the Azure Monitor alert.
        logger.LogError(ex, "SyntheticProbe jira=error");
    }
    return 1;
}

static class ProbeTelemetry
{
    public const string Name = "TechSolutions.SyntheticProbe";
    private static readonly Meter Meter = new(Name);
    public static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("probe.duration", unit: "ms");
}
