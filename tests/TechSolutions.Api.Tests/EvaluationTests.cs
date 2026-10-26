using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace TechSolutions.Api.Tests;

public sealed class DemoFactory : WebApplicationFactory<Program>
{
    // UseSetting is applied before Program reads configuration (minimal hosting); ConfigureAppConfiguration is not.
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
        .UseEnvironment("Development")
        .UseSetting("DemoMode", "true");
}

public sealed class EvaluationTests(DemoFactory factory) : IClassFixture<DemoFactory>
{
    private readonly HttpClient client = factory.CreateClient();

    private static HttpRequestMessage Submit(string address, string? idempotencyKey = null, string? tenant = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/evaluations") { Content = JsonContent.Create(new { address }) };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (tenant is not null) request.Headers.Add("x-demo-tenant", tenant);
        return request;
    }

    [Fact]
    public async Task AcceptedJobCanBeReadBack()
    {
        var response = await client.SendAsync(Submit("4512 Maple Ridge Dr, Plano, TX 75093", "demo-001"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JobResponse>();
        Assert.NotNull(accepted);
        Assert.NotEqual(Guid.Empty, accepted.JobId);
        Assert.Equal("accepted", accepted.Status);
        Assert.Equal($"/v1/evaluations/{accepted.JobId}", accepted.StatusUrl);
        Assert.Equal(accepted.StatusUrl, response.Headers.Location?.ToString());
        var stored = await client.GetFromJsonAsync<JobResponse>(response.Headers.Location);
        Assert.Equal(accepted, stored);
    }

    [Fact]
    public async Task RetryWithSameIdempotencyKeyReturnsSameJob()
    {
        var first = await (await client.SendAsync(Submit("4512 Maple Ridge Dr, Plano, TX 75093", "retry-001"))).Content.ReadFromJsonAsync<JobResponse>();
        var second = await (await client.SendAsync(Submit("4512 Maple Ridge Dr, Plano, TX 75093", "retry-001"))).Content.ReadFromJsonAsync<JobResponse>();
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task SameIdempotencyKeyInAnotherTenantIsADifferentJob()
    {
        var a = await (await client.SendAsync(Submit("1820 Cedar Ln, Austin, TX 78704", "shared-key", "tenant-a"))).Content.ReadFromJsonAsync<JobResponse>();
        var b = await (await client.SendAsync(Submit("1820 Cedar Ln, Austin, TX 78704", "shared-key", "tenant-b"))).Content.ReadFromJsonAsync<JobResponse>();
        Assert.NotEqual(a!.JobId, b!.JobId);
    }

    [Fact]
    public async Task JobIsNotVisibleToAnotherTenant()
    {
        var response = await client.SendAsync(Submit("1820 Cedar Ln, Austin, TX 78704", tenant: "tenant-a"));
        var job = await response.Content.ReadFromJsonAsync<JobResponse>();
        var read = new HttpRequestMessage(HttpMethod.Get, job!.StatusUrl);
        read.Headers.Add("x-demo-tenant", "tenant-b");
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(read)).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankAddressIsRejected(string address)
    {
        var response = await client.SendAsync(Submit(address));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OversizedAddressIsRejected()
    {
        var response = await client.SendAsync(Submit(new string('a', 501)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OversizedIdempotencyKeyIsRejected()
    {
        var response = await client.SendAsync(Submit("1820 Cedar Ln, Austin, TX 78704", new string('a', 65)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownJobIsNotFound()
    {
        var response = await client.GetAsync($"/v1/evaluations/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ResultIsConflictUntilTheJobCompletes()
    {
        var job = await (await client.SendAsync(Submit("4512 Maple Ridge Dr, Plano, TX 75093", "result-001"))).Content.ReadFromJsonAsync<JobResponse>();
        var response = await client.GetAsync($"{job!.StatusUrl}/result");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ResultOfAnotherTenantIsNotFound()
    {
        var job = await (await client.SendAsync(Submit("1820 Cedar Ln, Austin, TX 78704", tenant: "tenant-a"))).Content.ReadFromJsonAsync<JobResponse>();
        var read = new HttpRequestMessage(HttpMethod.Get, $"{job!.StatusUrl}/result");
        read.Headers.Add("x-demo-tenant", "tenant-b");
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(read)).StatusCode);
    }

    [Fact]
    public async Task UsageCountsOnlyTheCallersJobs()
    {
        await client.SendAsync(Submit("1820 Cedar Ln, Austin, TX 78704", "usage-1", "tenant-usage"));
        await client.SendAsync(Submit("1820 Cedar Ln, Austin, TX 78704", "usage-1", "tenant-usage")); // retry: not counted twice
        await client.SendAsync(Submit("1820 Cedar Ln, Austin, TX 78704", "usage-2", "tenant-usage"));
        await client.SendAsync(Submit("1820 Cedar Ln, Austin, TX 78704", "usage-1", "tenant-other"));
        var read = new HttpRequestMessage(HttpMethod.Get, "/v1/usage");
        read.Headers.Add("x-demo-tenant", "tenant-usage");
        var usage = await (await client.SendAsync(read)).Content.ReadFromJsonAsync<UsageResponse>();
        Assert.Equal(2, usage!.Accepted);
        Assert.Equal(0, usage.Billable);
    }

    [Fact]
    public async Task ReadinessIsHealthyInDemoMode()
    {
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public void JobIdIsDeterministicAndVersion8()
    {
        var a = JobIds.FromIdempotencyKey("tenant-a", "k1");
        Assert.Equal(a, JobIds.FromIdempotencyKey("tenant-a", "k1"));
        Assert.NotEqual(a, JobIds.FromIdempotencyKey("tenant-a", "k2"));
        Assert.Equal('8', a.ToString()[14]);
    }

    [Fact]
    public void DemoCannotStartInProduction()
    {
        using var production = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .UseSetting("DemoMode", "true"));
        var error = Assert.Throws<InvalidOperationException>(() => production.CreateClient());
        Assert.Contains("DemoMode is only allowed in Development", error.Message);
    }
}
