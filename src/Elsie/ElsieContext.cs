using System.Globalization;
using System.Text;
using System.Text.Json;
using Elsie.Binding;
using Elsie.Routing;
using Microsoft.Extensions.DependencyInjection;
// MultipartFormParser is internal in Binding

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

    /// <summary>Bind a POCO from route values (property name = route key, case-insensitive).</summary>
    public ElsieBindResult<T> BindRoute<T>() where T : new()
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in RouteValues)
        {
            map[k] = new[] { v };
        }

        return ElsieObjectBinder.Bind<T>(map);
    }

    /// <summary>Bind a POCO from query string (supports repeated keys → arrays/lists).</summary>
    public ElsieBindResult<T> BindQuery<T>() where T : new()
    {
        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in Request.Query.Keys)
        {
            map[key] = Request.GetQueryValues(key);
        }

        return ElsieObjectBinder.Bind<T>(map);
    }

    /// <summary>
    /// Bind <c>application/x-www-form-urlencoded</c> or <c>multipart/form-data</c> into <typeparamref name="T"/>.
    /// Multipart file parts are ignored for POCO binding (field values only).
    /// </summary>
    public async Task<ElsieBindResult<T>> BindFormAsync<T>(CancellationToken cancellationToken = default)
        where T : new()
    {
        var contentType = Request.ContentType ?? string.Empty;
        if (contentType.Length > 0
            && contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return await BindMultipartFormAsync<T>(cancellationToken).ConfigureAwait(false);
        }

        if (contentType.Length > 0
            && !contentType.Contains("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return ElsieBindResult<T>.Fail(ElsieResult.BadRequest(
                "Expected Content-Type application/x-www-form-urlencoded or multipart/form-data."));
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
        var map = ParseFormUrlEncodedMulti(text);
        return ElsieObjectBinder.Bind<T>(map);
    }

    private async Task<ElsieBindResult<T>> BindMultipartFormAsync<T>(CancellationToken cancellationToken)
        where T : new()
    {
        byte[] bytes;
        try
        {
            bytes = await ReadBodyWithLimitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return ElsieBindResult<T>.Fail(ElsieResult.BadRequest(ex.Message));
        }

        try
        {
            var map = MultipartFormParser.ParseFields(bytes, Request.ContentType ?? string.Empty);
            return ElsieObjectBinder.Bind<T>(map);
        }
        catch (InvalidOperationException ex)
        {
            return ElsieBindResult<T>.Fail(ElsieResult.BadRequest(ex.Message));
        }
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
            if (!IsJsonContentType(Request.ContentType))
            {
                return ElsieBindResult<T>.Fail(ElsieResult.Problem(415, "Unsupported Media Type",
                    "Expected Content-Type application/json (or *+json)."));
            }

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
    /// Problem+json with <c>instance</c> = request path and optional <c>traceId</c> from Activity.Current.
    /// </summary>
    public ElsieResult Problem(int statusCode, string title, string? detail = null) =>
        ElsieResult.Problem(
            statusCode,
            title,
            detail,
            JsonSerializerOptions,
            instance: Request.Path,
            traceId: System.Diagnostics.Activity.Current?.Id);

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


    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            // Many clients omit Content-Type; attempt deserialize.
            return true;
        }

        var media = contentType;
        var semi = media.IndexOf(';');
        if (semi >= 0)
        {
            media = media[..semi];
        }

        media = media.Trim();
        return media.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)
            || media.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, IReadOnlyList<string>> ParseFormUrlEncodedMulti(string text)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text))
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
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

            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }

            list.Add(value);
        }

        return map.ToDictionary(
            static kv => kv.Key,
            static kv => (IReadOnlyList<string>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
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
