using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TechSolutions.SyntheticMonitor;

/// <summary>
/// Sends the golden request through the public entry point (Front Door -> APIM -> API), polls until a final
/// state and validates the result. Any wrong result, error, or exceeded budget is a failure.
/// </summary>
public sealed class E2EProbe(HttpClient api, Func<CancellationToken, Task<string>> accessToken, ProbeOptions options, TimeProvider? time = null)
{
    private readonly TimeProvider time = time ?? TimeProvider.System;

    public async Task<ProbeOutcome> RunAsync(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(options.Timeout);
        Guid? jobId = null;
        try
        {
            var token = await accessToken(budget.Token);
            using var submit = Request(HttpMethod.Post, "v1/evaluations", token);
            submit.Headers.Add("Idempotency-Key", $"synthetic-{Guid.NewGuid():N}"); // every run must really execute
            submit.Content = JsonContent.Create(new { address = options.Address });
            using var accepted = await api.SendAsync(submit, budget.Token);
            if (accepted.StatusCode != HttpStatusCode.Accepted)
                return ProbeOutcome.Fail($"submit returned {(int)accepted.StatusCode}", clock.Elapsed);

            var job = await accepted.Content.ReadFromJsonAsync<JsonElement>(budget.Token);
            jobId = job.GetProperty("jobId").GetGuid();
            var statusUrl = job.GetProperty("statusUrl").GetString()!.TrimStart('/');

            while (true)
            {
                await Task.Delay(options.PollInterval, time, budget.Token);
                using var poll = Request(HttpMethod.Get, statusUrl, token);
                using var statusResponse = await api.SendAsync(poll, budget.Token);
                if (!statusResponse.IsSuccessStatusCode)
                    return ProbeOutcome.Fail($"status returned {(int)statusResponse.StatusCode}", clock.Elapsed, jobId);

                var status = await statusResponse.Content.ReadFromJsonAsync<JsonElement>(budget.Token);
                switch (status.GetProperty("status").GetString())
                {
                    case "completed":
                        using (var get = Request(HttpMethod.Get, $"{statusUrl}/result", token))
                        using (var resultResponse = await api.SendAsync(get, budget.Token))
                        {
                            if (!resultResponse.IsSuccessStatusCode)
                                return ProbeOutcome.Fail($"result returned {(int)resultResponse.StatusCode}", clock.Elapsed, jobId);
                            var result = await resultResponse.Content.ReadFromJsonAsync<JsonElement>(budget.Token);
                            var errors = ResultValidator.Validate(options.Expected, result, options.AreaTolerance);
                            return errors.Count == 0
                                ? ProbeOutcome.Ok(clock.Elapsed, jobId.Value)
                                : ProbeOutcome.Fail("unexpected result: " + string.Join("; ", errors), clock.Elapsed, jobId);
                        }
                    case "failed":
                    case "cancelled":
                        var error = status.TryGetProperty("error", out var e) ? e.ToString() : "no detail";
                        return ProbeOutcome.Fail($"job ended {status.GetProperty("status").GetString()}: {error}", clock.Elapsed, jobId);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ProbeOutcome.Fail($"timeout after {options.Timeout.TotalSeconds:0}s", clock.Elapsed, jobId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Includes token acquisition failures: an identity/RBAC break is also an E2E incident.
            return ProbeOutcome.Fail($"{ex.GetType().Name}: {ex.Message}", clock.Elapsed, jobId);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
