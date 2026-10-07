namespace Can.Core.Reporting;

/// <summary>
/// Bir hücredeki bir değerin özeti. Toplam, sayı, en küçük/büyük ve varyans akış hâlinde (bellek sabit) hesaplanır;
/// ortanca, yüzdelik ve farklı değer sayısı değerleri tutar. Birleştirilebilir (Top N'deki "Diğer" için).
/// </summary>
internal sealed class Accumulator
{
    private readonly ReportAggregate _kind;
    private readonly bool _countRows;
    private long _rows;
    private long _count;
    private decimal _sum;
    private object? _min;
    private object? _max;
    private object? _first;
    private object? _last;
    private bool _hasFirst;

    // Welford: sayısal kararlı tek geçişte varyans
    private double _mean;
    private double _m2;

    private HashSet<object>? _distinct;
    private List<decimal>? _values;

    public Accumulator(ReportAggregate kind, bool countRows)
    {
        _kind = kind;
        _countRows = countRows;
        if (kind == ReportAggregate.CountDistinct)
            _distinct = [];
        else if (kind is ReportAggregate.Median or ReportAggregate.Percentile)
            _values = [];
    }

    public void Add(object? value)
    {
        _rows++;
        if (value is null)
            return;

        _count++;
        switch (_kind)
        {
            case ReportAggregate.Sum or ReportAggregate.Average:
                if (ReportValue.ToDecimal(value) is { } number)
                    _sum += number;
                else
                    _count--; // sayı olmayanlar ortalamaya girmez
                break;
            case ReportAggregate.CountDistinct:
                _distinct!.Add(value);
                break;
            case ReportAggregate.Min:
                if (_min is null || ReportValue.Compare(value, _min) < 0)
                    _min = value;
                break;
            case ReportAggregate.Max:
                if (_max is null || ReportValue.Compare(value, _max) > 0)
                    _max = value;
                break;
            case ReportAggregate.Median or ReportAggregate.Percentile:
                if (ReportValue.ToDecimal(value) is { } v)
                    _values!.Add(v);
                break;
            case ReportAggregate.StdDev or ReportAggregate.StdDevP or ReportAggregate.Var or ReportAggregate.VarP:
                if (ReportValue.ToDecimal(value) is { } s)
                {
                    double x = (double)s;
                    long n = ++_welfordCount;
                    double delta = x - _mean;
                    _mean += delta / n;
                    _m2 += delta * (x - _mean);
                }

                break;
            case ReportAggregate.First:
                if (!_hasFirst)
                {
                    _first = value;
                    _hasFirst = true;
                }

                break;
            case ReportAggregate.Last:
                _last = value;
                break;
        }
    }

    private long _welfordCount;

    /// <summary>Başka bir hücrenin özetini ekler (aynı fonksiyon).</summary>
    public void Merge(Accumulator other)
    {
        _rows += other._rows;
        _count += other._count;
        _sum += other._sum;

        if (other._min is not null && (_min is null || ReportValue.Compare(other._min, _min) < 0))
            _min = other._min;
        if (other._max is not null && (_max is null || ReportValue.Compare(other._max, _max) > 0))
            _max = other._max;
        if (!_hasFirst && other._hasFirst)
        {
            _first = other._first;
            _hasFirst = true;
        }

        if (other._last is not null)
            _last = other._last;

        if (other._distinct is not null)
            _distinct!.UnionWith(other._distinct);
        if (other._values is not null)
            _values!.AddRange(other._values);

        // Chan vd.: iki grubun ortalama ve kare farkları toplamının birleştirilmesi
        if (other._welfordCount > 0)
        {
            long n = _welfordCount + other._welfordCount;
            double delta = other._mean - _mean;
            _m2 += other._m2 + (delta * delta * _welfordCount * other._welfordCount / n);
            _mean += delta * other._welfordCount / n;
            _welfordCount = n;
        }
    }

    public object? Result(double? percentile)
    {
        switch (_kind)
        {
            case ReportAggregate.Sum:
                return _count == 0 ? null : _sum;
            case ReportAggregate.Count:
                return (decimal)(_countRows ? _rows : _count);
            case ReportAggregate.CountDistinct:
                return (decimal)_distinct!.Count;
            case ReportAggregate.Average:
                return _count == 0 ? null : _sum / _count;
            case ReportAggregate.Min:
                return _min;
            case ReportAggregate.Max:
                return _max;
            case ReportAggregate.Median:
                return Quantile(0.5);
            case ReportAggregate.Percentile:
                return Quantile(percentile ?? 0.5);
            case ReportAggregate.First:
                return _first;
            case ReportAggregate.Last:
                return _last;
        }

        // varyans ailesi
        bool sample = _kind is ReportAggregate.StdDev or ReportAggregate.Var;
        long divisor = sample ? _welfordCount - 1 : _welfordCount;
        if (divisor <= 0)
            return null;

        double variance = Math.Max(0, _m2 / divisor);
        double result = _kind is ReportAggregate.StdDev or ReportAggregate.StdDevP ? Math.Sqrt(variance) : variance;
        return ReportValue.ToDecimal(result);
    }

    /// <summary>Doğrusal aradeğerlemeli yüzdelik (Excel PERCENTILE.INC ile aynı).</summary>
    private decimal? Quantile(double p)
    {
        if (_values!.Count == 0)
            return null;

        _values.Sort();
        double position = Math.Clamp(p, 0, 1) * (_values.Count - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        decimal fraction = (decimal)(position - lower);
        return _values[lower] + ((_values[upper] - _values[lower]) * fraction);
    }
}
