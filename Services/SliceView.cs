namespace Backend.Services;

/// <summary>
/// Lightweight read-only view của một slice trong list/array, tránh allocate list mới
/// khi dùng <c>Skip(start).Take(count).ToList()</c> hàng triệu lần.
/// </summary>
public sealed class SliceView<T> : IReadOnlyList<T>
{
    private readonly IReadOnlyList<T> _source;
    private readonly int _start;
    private readonly int _count;

    public SliceView(IReadOnlyList<T> source, int start, int count)
    {
        if (start < 0 || start > source.Count)
            throw new ArgumentOutOfRangeException(nameof(start));
        if (count < 0 || start + count > source.Count)
            throw new ArgumentOutOfRangeException(nameof(count));

        _source = source;
        _start = start;
        _count = count;
    }

    public int Count => _count;

    public T this[int index]
    {
        get
        {
            if (index < 0 || index >= _count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _source[_start + index];
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
            yield return _source[_start + i];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
