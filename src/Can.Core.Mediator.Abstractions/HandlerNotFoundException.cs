namespace Can.Core.Mediator;

/// <summary>Gönderilen istek için kayıtlı handler bulunamadığında fırlatılır.</summary>
public sealed class HandlerNotFoundException : InvalidOperationException
{
    public HandlerNotFoundException(Type requestType, Type handlerType)
        : base(
            $"'{requestType.FullName}' için handler bulunamadı. '{handlerType.Name}' uygulayan bir sınıf yazıp "
                + "AddCanMediator(...) ile taranan assembly'ye eklediğinden emin ol."
        )
    {
        RequestType = requestType;
        HandlerType = handlerType;
    }

    public Type RequestType { get; }

    public Type HandlerType { get; }
}
