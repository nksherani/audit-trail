using System.Net;
using System.Security.Claims;
using AuditTrail.Api.Auditing;
using AuditTrail.Application.Auditing;
using AuditTrail.Application.Catalog;
using AuditTrail.Domain.Common;
using AuditTrail.Domain.Entities;
using AuditTrail.Infrastructure.Auditing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuditTrail.Tests;

public sealed class ApiAndArchitectureTests
{
    [Fact]
    public async Task History_and_manual_endpoints_require_authentication()
    {
        await using var app = new WebApplicationFactory<Program>();
        using var client = app.CreateClient(new() { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/audit-history")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/audit-history/grouped")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"/api/products/{Guid.NewGuid()}/review", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"))).StatusCode);
    }
    [Fact]
    public void Http_identity_uses_validated_claims_and_ignores_spoofed_actor_headers()
    {
        var http = new DefaultHttpContext();
        http.Request.Headers["X-User-Id"] = "spoofed";
        http.Request.Headers["X-Tenant-Id"] = "spoofed-tenant";
        var provider = new HttpAuditContextProvider(new HttpContextAccessor { HttpContext = http }, new());
        Assert.Equal("System", provider.GetCurrent().TriggeredBy); Assert.Null(provider.GetCurrent().UserId);
        http.User = new(new ClaimsIdentity([new(ClaimTypes.NameIdentifier, "trusted"), new("tenant_id", "trusted-tenant"), new(ClaimTypes.Role, "AuditReader")], "validated"));
        Assert.Equal("trusted", provider.GetCurrent().UserId); Assert.Equal("trusted-tenant", provider.TenantId); Assert.True(provider.CanReadHistory);
    }
    [Fact]
    public void Domain_and_application_have_no_infrastructure_or_web_dependencies()
    {
        foreach (var assembly in new[] { typeof(Category).Assembly, typeof(ICatalogStore).Assembly })
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), x => x.Name!.Contains("EntityFrameworkCore") || x.Name.Contains("AspNetCore") || x.Name.Contains("Infrastructure"));
        }
        Assert.True(typeof(IAuditableEntity).IsAssignableFrom(typeof(Category)));
        Assert.True(typeof(AuditableEntity).IsAssignableFrom(typeof(Supplier)));
        Assert.False(typeof(IAuditableEntity).IsAssignableFrom(typeof(AuditRecord)));
    }
    [Fact]
    public void Application_and_api_cannot_use_direct_sql_or_ef_bulk_write_paths()
    {
        var root = Directory.GetParent(typeof(ApiAndArchitectureTests).Assembly.Location)!;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AuditTrail.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var folder in new[] { "AuditTrail.Application", "AuditTrail.Api" })
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root!.FullName, "src", folder), "*.cs", SearchOption.AllDirectories)
                .Where(x => !x.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
            {
                var code = File.ReadAllText(file);
                Assert.DoesNotContain("ExecuteUpdate", code); Assert.DoesNotContain("ExecuteDelete", code);
                Assert.DoesNotContain("ExecuteSql", code); Assert.DoesNotContain("SqlConnection", code);
                Assert.DoesNotContain("using Microsoft.EntityFrameworkCore", code);
            }
    }
}
