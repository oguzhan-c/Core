using System.Diagnostics;

namespace Can.Core.Mediator;

/// <summary>Bildirim (domain event) yayını için span'lar: <c>publish {EventTipi}</c>.</summary>
public static class MediatorTelemetry
{
    public const string Name = "Can.Core.Mediator";

    public static readonly ActivitySource Source = new(Name);
}
