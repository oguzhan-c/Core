using System.Runtime.CompilerServices;
using Can.Core.Domain.Events;

namespace Can.Core.Mediator.Tests;

/// <summary>Testlerin ortak kayıt defteri (singleton).</summary>
public sealed class Log
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_entries)
                return _entries.ToArray();
        }
    }

    public void Add(string entry)
    {
        lock (_entries)
            _entries.Add(entry);
    }
}

// ------------------------------------------------------------ Request / response

public sealed record Ping(string Message) : IRequest<string>;

public sealed class PingHandler(Log log) : IRequestHandler<Ping, string>
{
    public Task<string> Handle(Ping request, CancellationToken cancellationToken)
    {
        log.Add("handler");
        return Task.FromResult($"Pong: {request.Message}");
    }
}

// ------------------------------------------------------------ Void request

public sealed record DoWork(int Value) : IRequest;

public sealed class DoWorkHandler(Log log) : IRequestHandler<DoWork>
{
    public Task Handle(DoWork request, CancellationToken cancellationToken)
    {
        log.Add($"work:{request.Value}");
        return Task.CompletedTask;
    }
}

// ------------------------------------------------------------ Handler'ı olmayan istek

public sealed record Orphan : IRequest<int>;

// ------------------------------------------------------------ Behaviors

public sealed class OuterBehavior<TRequest, TResponse>(Log log) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        log.Add("outer:before");
        TResponse response = await next();
        log.Add("outer:after");
        return response;
    }
}

public sealed class InnerBehavior<TRequest, TResponse>(Log log) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        log.Add("inner:before");
        TResponse response = await next();
        log.Add("inner:after");
        return response;
    }
}

/// <summary>nArchitecture'daki ICachableRequest / ISecuredRequest gibi işaretleyici.</summary>
public interface IAuditableRequest { }

public sealed record AuditedPing(string Message) : IRequest<string>, IAuditableRequest;

public sealed class AuditedPingHandler : IRequestHandler<AuditedPing, string>
{
    public Task<string> Handle(AuditedPing request, CancellationToken cancellationToken) =>
        Task.FromResult(request.Message);
}

/// <summary>Sadece IAuditableRequest uygulayan isteklerde çalışır.</summary>
public sealed class AuditBehavior<TRequest, TResponse>(Log log) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IAuditableRequest
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        log.Add($"audit:{typeof(TRequest).Name}");
        return next();
    }
}

/// <summary>Handler'a hiç gitmeden yanıt dönen (short-circuit) behavior; caching böyle çalışır.</summary>
public sealed record CachedPing : IRequest<string>, ICachedRequest;

public interface ICachedRequest { }

public sealed class CachedPingHandler(Log log) : IRequestHandler<CachedPing, string>
{
    public Task<string> Handle(CachedPing request, CancellationToken cancellationToken)
    {
        log.Add("cached-handler");
        return Task.FromResult("from-handler");
    }
}

public sealed class FakeCacheBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICachedRequest
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken) =>
        Task.FromResult((TResponse)(object)"from-cache");
}

// ------------------------------------------------------------ Notifications / domain events

public sealed record OrderConfirmed(Guid OrderId) : DomainEvent;

public sealed class OrderConfirmedEmailHandler(Log log) : INotificationHandler<OrderConfirmed>
{
    public Task Handle(OrderConfirmed notification, CancellationToken cancellationToken)
    {
        log.Add("email");
        return Task.CompletedTask;
    }
}

public sealed class OrderConfirmedStockHandler(Log log) : INotificationHandler<OrderConfirmed>
{
    public Task Handle(OrderConfirmed notification, CancellationToken cancellationToken)
    {
        log.Add("stock");
        return Task.CompletedTask;
    }
}

/// <summary>Tüm domain event'leri yakalar (ör. outbox, loglama).</summary>
public sealed class AllDomainEventsHandler(Log log) : INotificationHandler<IDomainEvent>
{
    public Task Handle(IDomainEvent notification, CancellationToken cancellationToken)
    {
        log.Add($"any:{notification.GetType().Name}");
        return Task.CompletedTask;
    }
}

public sealed record UserRegistered(string Email) : INotification;

public sealed class NobodyListens : INotification { }

// ------------------------------------------------------------ Stream

public sealed record CountTo(int Max) : IStreamRequest<int>;

public sealed class CountToHandler : IStreamRequestHandler<CountTo, int>
{
    public async IAsyncEnumerable<int> Handle(
        CountTo request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int i = 1; i <= request.Max; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }
}

/// <summary>Akıştaki her elemanı 10 ile çarpar.</summary>
public sealed class TimesTenStreamBehavior<TRequest, TResponse> : IStreamPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async IAsyncEnumerable<TResponse> Handle(
        TRequest request,
        StreamHandlerDelegate<TResponse> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (TResponse item in next().WithCancellation(cancellationToken))
            yield return item is int number ? (TResponse)(object)(number * 10) : item;
    }
}
