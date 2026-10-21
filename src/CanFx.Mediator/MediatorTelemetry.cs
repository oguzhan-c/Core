using System.Diagnostics;

namespace CanFx.Mediator;

/// <summary>Bildirim (domain event) yayını için span'lar: <c>publish {EventTipi}</c>.</summary>
public static class MediatorTelemetry
{
    public const string Name = "CanFx.Mediator";

    public static readonly ActivitySource Source = new(Name);
}
