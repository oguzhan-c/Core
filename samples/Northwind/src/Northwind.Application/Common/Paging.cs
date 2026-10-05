namespace Northwind.Application.Common;

/// <summary>Sayfa isteği: <see cref="Index"/> 0'dan başlar.</summary>
public sealed record PageRequest(int Index = 0, int Size = 20)
{
    public const int MaxSize = 100;
}
