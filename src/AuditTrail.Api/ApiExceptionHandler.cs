using AuditTrail.Application.Catalog;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AuditTrail.Api;

public sealed class ApiExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var status = exception switch
        {
            EntityNotFoundException => 404,
            BusinessConflictException => 409,
            BusinessValidationException or ArgumentException => 400,
            UnauthorizedAccessException => 403,
            NotSupportedException => 400,
            _ => 500
        };
        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context,
            ProblemDetails = new ProblemDetails { Status = status, Title = status == 500 ? "An unexpected error occurred." : exception.Message } });
    }
}
