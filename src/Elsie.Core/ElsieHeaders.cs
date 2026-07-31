using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Elsie;

/// <summary>
/// Multi-value HTTP header bag with Set (replace) / Add (append) semantics.
/// Indexer get returns the first value; set replaces all values for that name.
/// </summary>
public sealed class ElsieHeaders : IEnumerable<KeyValuePair<string, IReadOnlyList<string>>>
{
    private readonly Dictionary<string, List<string>> _map = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _map.Count;

    /// <summary>First value for <paramref name="name"/>, or null when absent.</summary>
    public string? this[string name]
    {
        get
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return _map.TryGetValue(name, out var list) && list.Count > 0 ? list[0] : null;
        }
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (value is null)
            {
                _map.Remove(name);
                return;
            }

            Set(name, value);
        }
    }

    /// <summary>Replace all values for <paramref name="name"/> with a single value.</summary>
    public void Set(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        ValidateHeader(name, value);
        _map[name] = [value];
    }

    /// <summary>Replace all values for <paramref name="name"/>.</summary>
    public void Set(string name, IEnumerable<string> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(values);
        var list = values.Select(v => v ?? string.Empty).ToList();
        if (list.Count == 0)
        {
            _map.Remove(name);
            return;
        }

        foreach (var v in list)
        {
            ValidateHeader(name, v);
        }

        _map[name] = list;
    }

    /// <summary>Append a value for <paramref name="name"/>.</summary>
    public void Add(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        ValidateHeader(name, value);
        if (!_map.TryGetValue(name, out var list))
        {
            list = [];
            _map[name] = list;
        }

        list.Add(value);
    }

    private static void ValidateHeader(string name, string value)
    {
        if (ContainsCtl(name))
        {
            throw new ArgumentException("Header name contains invalid control characters.", nameof(name));
        }

        if (ContainsCtl(value))
        {
            throw new ArgumentException("Header value contains invalid control characters.", nameof(value));
        }
    }

    private static bool ContainsCtl(string s)
    {
        foreach (var c in s)
        {
            if (c is '\r' or '\n' or '\0')
            {
                return true;
            }
        }

        return false;
    }

    public bool Contains(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _map.ContainsKey(name);
    }

    public bool TryGetValues(string name, [NotNullWhen(true)] out IReadOnlyList<string>? values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_map.TryGetValue(name, out var list) && list.Count > 0)
        {
            values = list;
            return true;
        }

        values = null;
        return false;
    }

    public IReadOnlyList<string> GetValues(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _map.TryGetValue(name, out var list) ? list : Array.Empty<string>();
    }

    public bool Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _map.Remove(name);
    }

    public void Clear() => _map.Clear();

    /// <summary>Copy entries from another bag (Set semantics per key).</summary>
    public void SetAll(ElsieHeaders other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var (key, values) in other)
        {
            Set(key, values);
        }
    }

    /// <summary>Merge another bag: existing keys are replaced (Set), matching result-over-hook bake order when applied last.</summary>
    public void MergeFrom(ElsieHeaders other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var (key, values) in other)
        {
            Set(key, values);
        }
    }

    /// <summary>Snapshot as a new independent bag.</summary>
    public ElsieHeaders Clone()
    {
        var clone = new ElsieHeaders();
        foreach (var (key, values) in _map)
        {
            clone._map[key] = [.. values];
        }

        return clone;
    }

    public IEnumerator<KeyValuePair<string, IReadOnlyList<string>>> GetEnumerator()
    {
        foreach (var (key, list) in _map)
        {
            yield return new KeyValuePair<string, IReadOnlyList<string>>(key, list);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
