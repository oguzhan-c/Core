namespace Can.Core.Reporting;

/// <summary>Veritabanı sorgusunda gruplama anahtarı ve özet sonuçları için genel amaçlı satır (16 alan).</summary>
public abstract class ReportSlots
{
    public const int Capacity = 16;

    public abstract object? Get(int index);
}

/// <summary>EF Core'un çevirebildiği somut tip: anahtar ve özetler <c>S0</c>..<c>S15</c> özelliklerine yazılır.</summary>
#pragma warning disable CA1005 // çok sayıda tip parametresi: ifade ağacında anonim tip yerine
public sealed class ReportSlots<T0, T1, T2, T3, T4, T5, T6, T7, T8, T9, T10, T11, T12, T13, T14, T15> : ReportSlots
#pragma warning restore CA1005
{
    public T0 S0 { get; set; } = default!;

    public T1 S1 { get; set; } = default!;

    public T2 S2 { get; set; } = default!;

    public T3 S3 { get; set; } = default!;

    public T4 S4 { get; set; } = default!;

    public T5 S5 { get; set; } = default!;

    public T6 S6 { get; set; } = default!;

    public T7 S7 { get; set; } = default!;

    public T8 S8 { get; set; } = default!;

    public T9 S9 { get; set; } = default!;

    public T10 S10 { get; set; } = default!;

    public T11 S11 { get; set; } = default!;

    public T12 S12 { get; set; } = default!;

    public T13 S13 { get; set; } = default!;

    public T14 S14 { get; set; } = default!;

    public T15 S15 { get; set; } = default!;

    public override object? Get(int index) =>
        index switch
        {
            0 => (object?)S0,
            1 => (object?)S1,
            2 => (object?)S2,
            3 => (object?)S3,
            4 => (object?)S4,
            5 => (object?)S5,
            6 => (object?)S6,
            7 => (object?)S7,
            8 => (object?)S8,
            9 => (object?)S9,
            10 => (object?)S10,
            11 => (object?)S11,
            12 => (object?)S12,
            13 => (object?)S13,
            14 => (object?)S14,
            15 => (object?)S15,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };
}
