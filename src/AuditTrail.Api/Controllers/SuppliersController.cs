using AuditTrail.Application.Catalog;
using AuditTrail.Application.Contracts;
using Microsoft.AspNetCore.Mvc;
namespace AuditTrail.Api.Controllers;
[Route("api/suppliers")]
public sealed class SuppliersController(ICrudService<SupplierRequest, SupplierResponse> service)
    : CrudController<SupplierRequest, SupplierResponse>(service)
{
    protected override Guid Id(SupplierResponse response) => response.Id;
}
