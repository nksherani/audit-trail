using System.Diagnostics;
using System.Security.Claims;
using AuditTrail.Application.Auditing;

namespace AuditTrail.Api.Auditing;

public sealed class HttpAuditContextProvider(IHttpContextAccessor accessor, BackgroundAuditContext background)
    : IAuditContextProvider, IAuditAccessProvider
{
    public AuditContextInfo GetCurrent()
    {
        var http = accessor.HttpContext;
        if (http is null) return background.GetCurrent();
        var authenticated = http.User.Identity?.IsAuthenticated == true;
        var userId = authenticated ? http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub") : null;
        if (authenticated && string.IsNullOrWhiteSpace(userId)) throw new UnauthorizedAccessException("Authenticated audit actors require a subject claim.");
        return new(authenticated ? "User" : "System", userId, "WebAPI",
            Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier,
            authenticated ? http.User.FindFirstValue("tenant_id") : null);
    }
    public bool CanReadHistory => accessor.HttpContext?.User is { Identity.IsAuthenticated: true } user && user.IsInRole("AuditReader");
    public string? TenantId => GetCurrent().TenantId;
}
