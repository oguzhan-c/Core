using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Microsoft.Extensions.Logging;

namespace Can.Core.Application.Behaviors;

/// <summary><see cref="ILoggableRequest"/> isteklerinin başlangıcını, bitişini ve hatalarını loglar.</summary>
public sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ILoggableRequest
{
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;

    public LoggingBehavior(ICurrentUser currentUser, ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    {
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        string requestName = typeof(TRequest).Name;
        string user = _currentUser.UserName ?? _currentUser.Id ?? "anonim";

        _logger.LogInformation("{RequestName} başladı. Kullanıcı: {User}", requestName, user);

        try
        {
            TResponse response = await next().ConfigureAwait(false);

            if (response is IResultBase { IsSuccess: false } failed)
            {
                _logger.LogWarning(
                    "{RequestName} başarısız. Kullanıcı: {User}. Hatalar: {Errors}",
                    requestName,
                    user,
                    string.Join("; ", failed.Errors.Select(e => e.ToString()))
                );
            }
            else
            {
                _logger.LogInformation("{RequestName} tamamlandı. Kullanıcı: {User}", requestName, user);
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{RequestName} hata ile bitti. Kullanıcı: {User}", requestName, user);
            throw;
        }
    }
}
