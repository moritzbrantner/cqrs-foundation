using CqrsFoundation.Domain.Common;
using JasperFx;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace CqrsFoundation.Api;

public sealed class ApiExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "Bad request"),
            BusinessRuleException => (StatusCodes.Status422UnprocessableEntity, "Business rule rejected the command"),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "Unauthorized"),
            ForbiddenAccessException => (StatusCodes.Status403Forbidden, "Forbidden"),
            ConcurrencyException => (StatusCodes.Status409Conflict, "Concurrent modification detected"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected server error")
        };

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = status == StatusCodes.Status500InternalServerError ? null : exception.Message
        };

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}
