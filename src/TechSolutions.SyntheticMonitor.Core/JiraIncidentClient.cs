using System.Net.Http.Json;
using System.Text.Json;

namespace TechSolutions.SyntheticMonitor;

/// <summary>
/// Opens one Jira issue per incident: if an open issue with the dedup label exists, the new failure is added
/// as a comment instead of creating a duplicate ticket on every run.
/// Base address and Basic auth (account email + API token from Key Vault) are configured on the HttpClient.
/// </summary>
public sealed class JiraIncidentClient(HttpClient jira, JiraOptions options)
{
    public async Task<string> ReportAsync(ProbeOutcome outcome, CancellationToken ct)
    {
        var text = $"[{options.Environment}] Synthetic E2E failed: {outcome.Failure} | jobId={outcome.JobId?.ToString() ?? "n/a"} | elapsed={outcome.Elapsed.TotalSeconds:0.0}s | {DateTimeOffset.UtcNow:O}";

        var jql = $"project = \"{options.ProjectKey}\" AND labels = \"{options.DedupLabel}\" AND statusCategory != Done ORDER BY created DESC";
        using var search = await jira.PostAsJsonAsync("rest/api/3/search/jql", new { jql, maxResults = 1, fields = new[] { "key" } }, ct);
        search.EnsureSuccessStatusCode();
        var found = await search.Content.ReadFromJsonAsync<JsonElement>(ct);
        var issues = found.GetProperty("issues");

        if (issues.GetArrayLength() > 0)
        {
            var key = issues[0].GetProperty("key").GetString()!;
            using var comment = await jira.PostAsJsonAsync($"rest/api/3/issue/{key}/comment", new { body = Adf(text) }, ct);
            comment.EnsureSuccessStatusCode();
            return $"commented {key}";
        }

        var issue = new
        {
            fields = new
            {
                project = new { key = options.ProjectKey },
                issuetype = new { name = options.IssueType },
                summary = $"[{options.Environment}] TechSolutions E2E synthetic probe failing",
                labels = new[] { options.DedupLabel, $"env-{options.Environment}" },
                description = Adf(text)
            }
        };
        using var created = await jira.PostAsJsonAsync("rest/api/3/issue", issue, ct);
        created.EnsureSuccessStatusCode();
        var createdKey = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("key").GetString();
        return $"created {createdKey}";
    }

    // Jira Cloud REST v3 expects Atlassian Document Format for rich text fields.
    private static object Adf(string text) => new
    {
        type = "doc",
        version = 1,
        content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text } } } }
    };
}
