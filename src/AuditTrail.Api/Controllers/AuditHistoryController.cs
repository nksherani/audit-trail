using AuditTrail.Application.Auditing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuditTrail.Api.Controllers;

[ApiController, Route("api/audit-history"), Authorize(Policy = "AuditReader")]
public sealed class AuditHistoryController(IAuditTrailQueryService query) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<AuditTrailDto>> Search([FromQuery] AuditTrailQuery request, CancellationToken ct) => query.SearchAsync(request, ct);
    [HttpGet("grouped")]
    public Task<PagedResult<AuditOperationGroup>> Grouped([FromQuery] AuditTrailQuery request, CancellationToken ct) => query.GroupedAsync(request, ct);
}
