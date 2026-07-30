using System.Globalization;
using System.Text;
using System.Text.Json;
using Elsie.Binding;
using Elsie.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie;

/// <summary>
/// Per-request facade available to Elsie route handlers (host-agnostic).
/// </summary>
public sealed class ElsieContext
{
    private readonly RouteTable? _routes;
    private readonly long _maxBindBodySize;

    public ElsieContext(
        ElsieRequest request,
        ElsieResponse response,
        IReadOnlyDictionary<string, string> routeValues,
        JsonSerializerOptions? jsonSerializerOptions = null,
        RouteTable? routes = null,
        long maxBindBodySize = 4 * 1024 * 1024)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Response = response ?? throw new ArgumentNullException(nameof(response));
        RouteValues = routeValues ?? throw new ArgumentNullException(nameof(routeValues));
        JsonSerializerOptions = jsonSerializerOptions ?? ElsieJson.DefaultOptions;
        _routes = routes;
        _maxBindBodySize = maxBindBodySize > 0 ? maxBindBodySize : 4 * 1024 * 1024;
    }

    public ElsieRequest Request { get; }
    public ElsieResponse Response { get; }
    public IReadOnlyDictionary<string, string> RouteValues { get; }
    public IServiceProvider RequestServices => Request.RequestServices;

    /// <summary>Alias for <see cref="RequestServices"/>.</summary>
    public IServiceProvider Services => Request.RequestServices;

    public CancellationToken RequestAborted => Request.RequestAborted;

    /// <summary>JSON options for this request (from <see cref="ElsieOptions"/>).</summary>
    public JsonSerializerOptions JsonSerializerOptions { get; }

    /// <summary>Resolve a required service from the current request scope.</summary>
    public T GetRequiredService<T>() where T : notnull =>
        RequestServices.GetRequiredService<T>();

    /// <summary>Resolve an optional service from the current request scope.</summary>
    public T? GetService<T>() => RequestServices.GetService<T>();

    public string? RouteOrDefault(string key) =>
        RouteValues.TryGetValue(key, out var value) ? value : null;

    public string? QueryOrDefault(string key) => Request.GetQuery(key);

    public T? Route<T>(string key)
    {
        TryRoute(key, out T? value);
        return value;
    }

    public T? Query<T>(string key)
    {
        TryQuery(key, out T? value);
        return value;
    }

    public bool TryRoute<T>(string key, out T? value)
    {
        value = default;
        if (!RouteValues.TryGetValue(key, out var raw))
        {
            return false;
        }

        if (!ElsieValueConverters.TryConvert(typeof(T), raw, out var converted, out _))
        {
            return false;
        }

        value = converted is null ? default : (T)converted;
        return true;
    }

    public bool TryQuery<T>(string key, out T? value)
    {
        value = default;
        var raw = Request.GetQuery(key);
        if (raw is null)
        {
            return false;
        }

        if (!ElsieValueConverters.TryConvert(typeof(T), raw, out var converted, out _))
        {
            return false;
        }

        value = converted is null ? default : (T)converted;
        return true;
    }

    public bool RequireRoute<T>(string key, out T? value, out ElsieResult? error)
    {
        if (TryRoute(key, out value))
        {
            error = null;
            return true;
        }

        value = default;
        error = ElsieResult.BadRequest($"Route value '{key}' must be a valid {typeof(T).Name}.");
        return false;
    }

    public bool RequireQuery<T>(string key, out T? value, out ElsieResult? error)
    {
        if (TryQuery(key, out value))
        {
            error = null;
            return true;
        }

        value = default;
        error = ElsieResult.BadRequest($"Query value '{key}' must be a valid {typeof(T).Name}.");
        return false;
    }

    // ---- legacy typed helpers (thin wrappers) ----

    public bool TryGetRouteInt(string key, out int value)
    {
        if (TryRoute<int>(key, out var v))
        {
            value = v;
            return true;
        }

        value = default;
        return false;
    }

    public bool TryGetRouteLong(string key, out long value)
    {
        if (TryRoute<long>(key, out var v))
        {
            value = v;
            return true;
        }

        value = default;
        return false;
    }

    public bool TryGetRouteGuid(string key, out Guid value)
    {
        if (TryRoute<Guid>(key, out var v))
        {
            value = v;
            return true;
        }

        value = default;
        return false;
    }

    public bool TryGetRouteBool(string key, out bool value)
    {
        if (TryRoute<bool>(key, out var v))
        {
            value = v;
            return true;
        }

        value = default;
        return false;
    }

    public bool TryGetQueryInt(string key, out int value)
    {
        if (TryQuery<int>(key, out var v))
        {
            value = v;
            return true;
        }

        value = default;
        return false;
    }

    public bool TryGetQueryBool(string key, out bool value)
    {
        if (TryQuery<bool>(key, out var v))
        {
            value = v;
            return true;
        }

        value = default;
        return false;
    }

    public bool RequireRouteInt(string key, out int value, out ElsieResult? error)
    {
        if (RequireRoute<int>(key, out var v, out error))
        {
            value = v;
            return true;
        }

        value = default;
        return false;
    }

    public bool RequireRouteGuid(string key, out Guid value, out ElsieResult? error)
    {
        if (RequireRoute<Guid>(key, out var v, out error))
        {
            value = v;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Bind a POCO from route values (property name = route key, case-insensitive).</summary>
    public ElsieBindResult<T> BindRoute<T>() where T : new()
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in RouteValues)
        {
            map[k] = v;
        }

        return ElsieObjectBinder.Bind<T>(map);
    }

    /// <summary>Bind a POCO from query string (first value per key).</summary>
    public ElsieBindResult<T> BindQuery<T>() where T : new()
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in Request.Query)
        {
            map[k] = v;
        }

        return ElsieObjectBinder.Bind<T>(map);
    }

    /// <summary>
    /// Bind <c>application/x-www-form-urlencoded</c> body into <typeparamref name="T"/>.
    /// Multipart is not parsed in core — use the ASP.NET <c>HttpContext</c> escape hatch.
    /// </summary>
    public async Task<ElsieBindResult<T>> BindFormAsync<T>(CancellationToken cancellationToken = default)
        where T : new()
    {
        var contentType = Request.ContentType ?? string.Empty;
        if (contentType.Length > 0
            && contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return ElsieBindResult<T>.Fail(ElsieResult.BadRequest(
                "Multipart form data is not parsed by Elsie core. Use TryGetHttpContext and request.ReadFormAsync, or a testing multipart builder."));
        }

        if (contentType.Length > 0
            && !contentType.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return ElsieBindResult<T>.Fail(ElsieResult.BadRequest(
                "Expected Content-Type application/x-www-form-urlencoded."));
        }

        byte[] bytes;
        try
        {
            bytes = await ReadBodyWithLimitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return ElsieBindResult<T>.Fail(ElsieResult.BadRequest(ex.Message));
        }

        var text = Encoding.UTF8.GetString(bytes);
        var map = ParseFormUrlEncoded(text);
        return ElsieObjectBinder.Bind<T>(map);
    }

    /// <summary>
    /// Build a path for a named route. Values may be a dictionary or an anonymous object.
    /// </summary>
    public string UrlFor(string name, object? values = null)
    {
        if (_routes is null)
        {
            throw new InvalidOperationException(
                "Link generation requires a RouteTable on the context (normal dispatcher path).");
        }

        return _routes.GetPathByName(name, values);
    }

    /// <summary>
    /// Deserialize JSON body. Returns a failed bind result (400 problem+json) when missing/invalid.
    /// Honors <see cref="ElsieOptions.MaxBindBodySize"/>.
    /// </summary>
    public async Task<ElsieBindResult<T>> BindJsonAsync<T>(CancellationToken cancellationToken = default)
    {
        try
        {
            if (Request.ContentLength is 0)
            {
                return ElsieBindResult<T>.Fail(ElsieResult.BadRequest("JSON body is required."));
            }

            if (Request.ContentLength is { } declared && declared > _maxBindBodySize)
            {
                return ElsieBindResult<T>.Fail(ElsieResult.BadRequest(
                    $"JSON body exceeds max size of {_maxBindBodySize} bytes."));
            }

            await using var limited = new SizeLimitedStream(Request.Body, _maxBindBodySize);
            var value = await JsonSerializer.DeserializeAsync<T>(
                limited,
                JsonSerializerOptions,
                cancellationToken).ConfigureAwait(false);

            if (value is null)
            {
                return ElsieBindResult<T>.Fail(ElsieResult.BadRequest("JSON body is required."));
            }

            return ElsieBindResult<T>.Success(value);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("max size", StringComparison.OrdinalIgnoreCase))
        {
            return ElsieBindResult<T>.Fail(ElsieResult.BadRequest(ex.Message));
        }
        catch (JsonException ex)
        {
            var path = ex.Path is { Length: > 0 } p ? $" (path: {p})" : string.Empty;
            return ElsieBindResult<T>.Fail(ElsieResult.BadRequest($"Invalid JSON{path}: {ex.Message}"));
        }
    }

    /// <summary>
    /// Minimal Accept negotiation: JSON for objects, text for strings, 406 otherwise.
    /// </summary>
    public ElsieResult Negotiate(object? model)
    {
        var accept = Request.GetHeader("Accept");
        var ranges = ParseAccept(accept);

        var wantsJson = Accepts(ranges, "application/json", "text/json", "application/*", "*/*");
        var wantsText = Accepts(ranges, "text/plain", "text/*", "*/*");
        var wantsHtml = Accepts(ranges, "text/html", "text/*", "*/*");

        if (model is string s)
        {
            if (wantsText || wantsHtml || ranges.Count == 0)
            {
                return wantsHtml && !wantsText
                    ? ElsieResult.Html(s)
                    : ElsieResult.Text(s);
            }

            if (wantsJson)
            {
                return Json(s);
            }

            return ElsieResult.NotAcceptable("No acceptable representation for string body.");
        }

        if (wantsJson || ranges.Count == 0)
        {
            return Json(model);
        }

        if (wantsText && model is not null)
        {
            return ElsieResult.Text(Convert.ToString(model, CultureInfo.InvariantCulture) ?? string.Empty);
        }

        return ElsieResult.NotAcceptable("No acceptable representation.");
    }

    /// <summary>Serialize <paramref name="value"/> with this request's JSON options (app options).</summary>
    public ElsieResult Json<T>(T value, int statusCode = 200) =>
        ElsieResult.Json(value, statusCode, JsonSerializerOptions);

    private async Task<byte[]> ReadBodyWithLimitAsync(CancellationToken cancellationToken)
    {
        if (Request.ContentLength is { } declared && declared > _maxBindBodySize)
        {
            throw new InvalidOperationException($"Body exceeds max size of {_maxBindBodySize} bytes.");
        }

        await using var limited = new SizeLimitedStream(Request.Body, _maxBindBodySize);
        await using var ms = new MemoryStream();
        await limited.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }

    private static Dictionary<string, string?> ParseFormUrlEncoded(string text)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text))
        {
            return map;
        }

        foreach (var part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string key;
            string value;
            var eq = part.IndexOf('=');
            if (eq < 0)
            {
                key = Uri.UnescapeDataString(part.Replace('+', ' '));
                value = string.Empty;
            }
            else
            {
                key = Uri.UnescapeDataString(part[..eq].Replace('+', ' '));
                value = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            }

            map[key] = value;
        }

        return map;
    }

    private static List<(string Type, double Q)> ParseAccept(string? header)
    {
        var list = new List<(string, double)>();
        if (string.IsNullOrWhiteSpace(header))
        {
            return list;
        }

        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var segments = part.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) continue;
            var type = segments[0].ToLowerInvariant();
            var q = 1.0;
            for (var i = 1; i < segments.Length; i++)
            {
                if (segments[i].StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(segments[i].AsSpan(2), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                {
                    q = parsed;
                }
            }

            list.Add((type, q));
        }

        list.Sort(static (a, b) => b.Item2.CompareTo(a.Item2));
        return list;
    }

    private static bool Accepts(List<(string Type, double Q)> ranges, params string[] candidates)
    {
        if (ranges.Count == 0)
        {
            return true;
        }

        foreach (var (type, q) in ranges)
        {
            if (q <= 0) continue;
            foreach (var candidate in candidates)
            {
                if (type == "*/*") return true;
                if (type == candidate) return true;
                if (type.EndsWith("/*", StringComparison.Ordinal)
                    && candidate.StartsWith(type[..^1], StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private sealed class SizeLimitedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private long _read;

        public SizeLimitedStream(Stream inner, long limit)
        {
            _inner = inner;
            _limit = limit;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            Accumulate(n);
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var n = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            Accumulate(n);
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Accumulate(n);
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void Accumulate(int n)
        {
            if (n <= 0) return;
            _read += n;
            if (_read > _limit)
            {
                throw new InvalidOperationException($"Body exceeds max size of {_limit} bytes.");
            }
        }
    }
}
