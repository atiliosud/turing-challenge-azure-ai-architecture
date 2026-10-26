using System.Net;
using System.Text;
using System.Text.Json;
using TechSolutions.SyntheticMonitor;
using Xunit;

namespace TechSolutions.SyntheticMonitor.Tests;

/// <summary>Scripted HTTP fake: each call is answered by the first matching route.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Calls.Add((request.Method, request.RequestUri!.AbsolutePath, body));
        return respond(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode code, object payload) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
}

public sealed class SyntheticMonitorTests
{
    private static readonly Guid JobId = Guid.Parse("3f2b7c1e-0000-8000-8000-000000000001");

    private static ProbeOptions Options(TimeSpan? timeout = null) => new()
    {
        Address = "golden parcel",
        Timeout = timeout ?? TimeSpan.FromSeconds(5),
        PollInterval = TimeSpan.FromMilliseconds(5),
        Expected = new ExpectedResult { LawnAreaSqft = 1000, PolygonCount = 2, Flags = new() { ["trees"] = true, ["fence"] = false } }
    };

    private static object Result(double area = 1010, int polygons = 2, bool trees = true) => new
    {
        lawnAreaSqft = area,
        polygons = Enumerable.Range(0, polygons).Select(i => new { id = i }).ToArray(),
        flags = new { trees, fence = false },
        modelVersion = "lawn-seg-1.4.2"
    };

    private static FakeHandler Api(object? result, string finalStatus = "completed", int pendingPolls = 1)
    {
        var polls = 0;
        return new FakeHandler((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/v1/evaluations" => FakeHandler.Json(HttpStatusCode.Accepted, new { jobId = JobId, status = "accepted", statusUrl = $"/v1/evaluations/{JobId}" }),
            var p when p.EndsWith("/result") => FakeHandler.Json(HttpStatusCode.OK, result!),
            _ => FakeHandler.Json(HttpStatusCode.OK, new { status = ++polls > pendingPolls ? finalStatus : "inferencing", error = "model_timeout" })
        });
    }

    private static E2EProbe Probe(FakeHandler handler, ProbeOptions? options = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") }, _ => Task.FromResult("token"), options ?? Options());

    [Fact]
    public async Task PassesWhenResultIsWithinTolerance()
    {
        var handler = Api(Result(area: 1015));
        var outcome = await Probe(handler).RunAsync(CancellationToken.None);
        Assert.True(outcome.Success, outcome.Failure);
        Assert.Equal(JobId, outcome.JobId);
        Assert.Contains(handler.Calls, c => c.Path.EndsWith("/result"));
    }

    [Fact]
    public async Task FailsWhenAreaIsOutsideTolerance()
    {
        var outcome = await Probe(Api(Result(area: 1100))).RunAsync(CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Contains("lawnAreaSqft", outcome.Failure);
    }

    [Fact]
    public async Task FailsWhenAFlagDiffers()
    {
        var outcome = await Probe(Api(Result(trees: false))).RunAsync(CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Contains("flag trees", outcome.Failure);
    }

    [Fact]
    public async Task FailsWhenJobEndsFailed()
    {
        var outcome = await Probe(Api(null, finalStatus: "failed")).RunAsync(CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Contains("job ended failed", outcome.Failure);
    }

    [Fact]
    public async Task FailsOnTimeoutWhenJobNeverCompletes()
    {
        var outcome = await Probe(Api(null, pendingPolls: int.MaxValue), Options(TimeSpan.FromMilliseconds(200))).RunAsync(CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.StartsWith("timeout", outcome.Failure);
        Assert.Equal(JobId, outcome.JobId);
    }

    [Fact]
    public async Task FailsWhenSubmitIsRejected()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Json(HttpStatusCode.ServiceUnavailable, new { }));
        var outcome = await Probe(handler).RunAsync(CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Equal("submit returned 503", outcome.Failure);
    }

    [Fact]
    public async Task JiraCreatesIssueWhenNoneIsOpen()
    {
        var handler = new FakeHandler((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/rest/api/3/search/jql" => FakeHandler.Json(HttpStatusCode.OK, new { issues = Array.Empty<object>() }),
            "/rest/api/3/issue" => FakeHandler.Json(HttpStatusCode.Created, new { key = "OPS-42" }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var jira = new JiraIncidentClient(new HttpClient(handler) { BaseAddress = new Uri("https://jira.test/") }, new JiraOptions());
        var action = await jira.ReportAsync(ProbeOutcome.Fail("timeout after 90s", TimeSpan.FromSeconds(90), JobId), CancellationToken.None);

        Assert.Equal("created OPS-42", action);
        var create = handler.Calls.Single(c => c.Path == "/rest/api/3/issue");
        Assert.Contains("synthetic-e2e", create.Body);
        Assert.Contains("timeout after 90s", create.Body);
    }

    [Fact]
    public async Task JiraCommentsOnOpenIssueInsteadOfDuplicating()
    {
        var handler = new FakeHandler((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/rest/api/3/search/jql" => FakeHandler.Json(HttpStatusCode.OK, new { issues = new[] { new { key = "OPS-7" } } }),
            "/rest/api/3/issue/OPS-7/comment" => FakeHandler.Json(HttpStatusCode.Created, new { id = "1" }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var jira = new JiraIncidentClient(new HttpClient(handler) { BaseAddress = new Uri("https://jira.test/") }, new JiraOptions());
        var action = await jira.ReportAsync(ProbeOutcome.Fail("unexpected result", TimeSpan.FromSeconds(20), JobId), CancellationToken.None);

        Assert.Equal("commented OPS-7", action);
        Assert.DoesNotContain(handler.Calls, c => c.Path == "/rest/api/3/issue");
    }
}
