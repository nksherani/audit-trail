using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AuditTrail.Application.Auditing;
using AuditTrail.Application.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuditTrail.Tests;

[Collection("SQL Server")]
public sealed class AuthenticatedApiTests(SqlServerFixture fixture)
{
    private WebApplicationFactory<Program> App(string tenant, bool reader) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:DefaultConnection"] = fixture.ConnectionString }));
        builder.ConfigureTestServices(services => services.AddAuthentication(options =>
        { options.DefaultAuthenticateScheme = "Test"; options.DefaultChallengeScheme = "Test"; })
            .AddScheme<TestAuthOptions, TestAuthHandler>("Test", options => { options.Tenant = tenant; options.Reader = reader; }));
    });
    [Fact]
    public async Task Authenticated_http_writes_capture_actor_and_history_is_authorized_per_tenant()
    {
        var tenant = Guid.NewGuid().ToString("N");
        await using var app = App(tenant, true);
        using var client = app.CreateClient(new() { BaseAddress = new("https://localhost") });
        var created = await client.PostAsJsonAsync("/api/categories", new { name = "Authenticated category", description = "HTTP test" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var entity = await created.Content.ReadFromJsonAsync<CategoryResponse>();
        var history = await client.GetFromJsonAsync<PagedResult<AuditTrailDto>>($"/api/audit-history?PkId={entity!.Id}");
        var record = Assert.Single(history!.Items);
        Assert.Equal("trusted-http-user", record.UserId); Assert.Equal("User", record.TriggeredBy); Assert.Equal(tenant, record.TenantId);
        await using var deniedApp = App(tenant, false);
        using var denied = deniedApp.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync("/api/audit-history")).StatusCode);
        await using var otherApp = App(Guid.NewGuid().ToString("N"), true);
        using var other = otherApp.CreateClient(new() { BaseAddress = new("https://localhost") });
        var isolated = await other.GetFromJsonAsync<PagedResult<AuditTrailDto>>($"/api/audit-history?PkId={entity.Id}");
        Assert.Empty(isolated!.Items);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/categories/{entity.Id}")).StatusCode);
    }
    private sealed class TestAuthOptions : AuthenticationSchemeOptions
    {
        public string Tenant { get; set; } = "";
        public bool Reader { get; set; }
    }
    private sealed class TestAuthHandler(IOptionsMonitor<TestAuthOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<TestAuthOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "trusted-http-user"), new("tenant_id", Options.Tenant) };
            if (Options.Reader) claims.Add(new(ClaimTypes.Role, "AuditReader"));
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
}
