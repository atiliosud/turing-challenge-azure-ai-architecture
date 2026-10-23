using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace TechSolutions.Api.Tests;

public sealed class DemoFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder
        .UseEnvironment("Development")
        .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["DemoMode"] = "true" }));
}

public sealed class EvaluationTests : IClassFixture<DemoFactory>
{
    private readonly HttpClient client;
    public EvaluationTests(DemoFactory factory) => client = factory.CreateClient();

    [Fact]
    public async Task AcceptedJobCanBeReadBack()
    {
        var response = await client.PostAsJsonAsync("/v1/evaluations", new { address = "Calle Mayor 1, Madrid", idempotencyKey = "demo-001" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JobResponse>();
        Assert.NotNull(accepted);
        Assert.NotEqual(Guid.Empty, accepted.JobId);
        Assert.Equal("accepted", accepted.Status);
        Assert.Equal($"/v1/evaluations/{accepted.JobId}", response.Headers.Location?.ToString());
        var stored = await client.GetFromJsonAsync<JobResponse>(response.Headers.Location);
        Assert.Equal(accepted, stored);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankAddressIsRejected(string address)
    {
        var response = await client.PostAsJsonAsync("/v1/evaluations", new { address });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OversizedAddressIsRejected()
    {
        var response = await client.PostAsJsonAsync("/v1/evaluations", new { address = new string('a', 501) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OversizedIdempotencyKeyIsRejected()
    {
        var response = await client.PostAsJsonAsync("/v1/evaluations", new { address = "Madrid", idempotencyKey = new string('a', 65) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownJobIsNotFound()
    {
        var response = await client.GetAsync($"/v1/evaluations/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void DemoCannotStartInProduction()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["DemoMode"] = "true" })));
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("DemoMode is only allowed in Development", error.Message);
    }
}
