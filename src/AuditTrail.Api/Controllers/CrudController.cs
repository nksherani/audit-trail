using AuditTrail.Application.Catalog;
using Microsoft.AspNetCore.Mvc;

namespace AuditTrail.Api.Controllers;

[ApiController]
public abstract class CrudController<TRequest, TResponse>(ICrudService<TRequest, TResponse> service) : ControllerBase
{
    protected abstract Guid Id(TResponse response);
    [HttpGet]
    public Task<List<TResponse>> GetAll(CancellationToken ct) => service.ListAsync(ct);
    [HttpGet("{id:guid}")]
    public Task<TResponse> Get(Guid id, CancellationToken ct) => service.GetAsync(id, ct);
    [HttpPost]
    public async Task<ActionResult<TResponse>> Create(TRequest request, CancellationToken ct)
    {
        var response = await service.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = Id(response) }, response);
    }
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, TRequest request, CancellationToken ct)
    { await service.UpdateAsync(id, request, ct); return NoContent(); }
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    { await service.DeleteAsync(id, ct); return NoContent(); }
}
