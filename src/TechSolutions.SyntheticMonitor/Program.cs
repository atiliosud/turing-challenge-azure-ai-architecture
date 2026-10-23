using System.Net.Http.Headers;
using System.Text;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TechSolutions.SyntheticMonitor;

// Container Apps Job (cron). Identity: user-assigned managed identity that holds the API app role
// TechSolutions.Submit, so the probe needs no client secret. Jira token comes from Key Vault as a secret ref.
var builder = Host.CreateApplicationBuilder(args);
var config = builder.Configuration;
using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SyntheticProbe");

var probeOptions = config.GetSection("Probe").Get<ProbeOptions>() ?? throw new InvalidOperationException("Probe section missing");
var jiraOptions = config.GetSection("Jira").Get<JiraOptions>() ?? new JiraOptions();
var apiBaseUrl = config["Probe:ApiBaseUrl"] ?? throw new InvalidOperationException("Probe:ApiBaseUrl missing");
var apiScope = config["Probe:ApiScope"] ?? throw new InvalidOperationException("Probe:ApiScope missing");
var attempts = config.GetValue("Probe:Attempts", 2);

var credential = new DefaultAzureCredential();
using var api = new HttpClient { BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + "/") };
api.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", config["Probe:SubscriptionKey"]); // dedicated "synthetic" product, excluded from billing
var probe = new E2EProbe(api, async ct => (await credential.GetTokenAsync(new TokenRequestContext([apiScope]), ct)).Token, probeOptions);

ProbeOutcome outcome = ProbeOutcome.Fail("not executed", TimeSpan.Zero);
for (var attempt = 1; attempt <= attempts; attempt++)
{
    outcome = await probe.RunAsync(CancellationToken.None);
    // Structured line consumed by the Log Analytics alert rule and the monthly workbook.
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
