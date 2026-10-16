using AuditTrail.Application.Catalog;
using AuditTrail.Application.Contracts;
using Microsoft.AspNetCore.Mvc;
namespace AuditTrail.Api.Controllers;
[Route("api/categories")]
public sealed class CategoriesController(ICrudService<CategoryRequest, CategoryResponse> service)
    : CrudController<CategoryRequest, CategoryResponse>(service)
{
    protected override Guid Id(CategoryResponse response) => response.Id;
}
