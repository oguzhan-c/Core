using Can.Core.Application.Exceptions;
using Can.Core.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Can.Core.WebApi.ExceptionHandling;

/// <summary>
/// Can.Core hata tiplerini HTTP durum kodlarına ve RFC 9457 ProblemDetails yanıtlarına çevirir:
/// <code>
/// ValidationException    400  (+ "errors": { "Name": ["..."] })
/// BusinessException      400
/// UnauthorizedException  401
/// ForbiddenException     403
/// NotFoundException      404
/// ConflictException      409
/// diğer her şey          500  (ayrıntı yalnızca Development ortamında gösterilir)
/// </code>
/// </summary>
public sealed class CanExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetailsService;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<CanExceptionHandler> _logger;

    public CanExceptionHandler(
        IProblemDetailsService problemDetailsService,
        IHostEnvironment environment,
        ILogger<CanExceptionHandler> logger)
    {
        _problemDetailsService = problemDetailsService;
        _environment = environment;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // İstemci bağlantıyı kapattıysa yazacak bir yanıt yok.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            httpContext.Response.StatusCode = 499;
            return true;
        }

        (int status, string title) = exception switch
        {
            ValidationException => (StatusCodes.Status400BadRequest, "Doğrulama hatası"),
            BusinessException => (StatusCodes.Status400BadRequest, "İş kuralı ihlali"),
            UnauthorizedException => (StatusCodes.Status401Unauthorized, "Giriş gerekli"),
            ForbiddenException => (StatusCodes.Status403Forbidden, "Yetki yok"),
            NotFoundException => (StatusCodes.Status404NotFound, "Bulunamadı"),
            ConflictException => (StatusCodes.Status409Conflict, "Çakışma"),
            _ => (StatusCodes.Status500InternalServerError, "Sunucu hatası"),
        };

        ProblemDetails problem = exception is ValidationException validation
            ? new HttpValidationProblemDetails(validation.Errors.ToDictionary(e => e.Key, e => e.Value))
            : new ProblemDetails();

        problem.Status = status;
        problem.Title = title;

        if (status >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(exception, "İşlenmeyen hata: {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);

            // Üretimde iç ayrıntılar (SQL, stack trace, bağlantı bilgisi ...) istemciye sızmasın.
            problem.Detail = _environment.IsDevelopment() ? exception.ToString() : "Beklenmeyen bir hata oluştu.";
        }
        else
        {
            _logger.LogDebug(exception, "{Status} {Method} {Path}", status, httpContext.Request.Method, httpContext.Request.Path);
            problem.Detail = exception.Message;
        }

        httpContext.Response.StatusCode = status;

        return await _problemDetailsService
            .TryWriteAsync(
                new ProblemDetailsContext
                {
                    HttpContext = httpContext,
                    ProblemDetails = problem,
                    Exception = exception,
                }
            )
            .ConfigureAwait(false);
    }
}
