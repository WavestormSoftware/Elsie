using System.Globalization;
using System.Security.Cryptography;

namespace Elsie;

/// <summary>
/// Conditional request (RFC 9110 §13) helpers for dynamic results: ETag / Last-Modified
/// attachment, SHA-256 computed ETags, and 304/412 rewriting for GET/HEAD and unsafe methods.
/// </summary>
public static class ElsieResultConditionalGetExtensions
{
    /// <summary>
    /// Attach an entity-tag to this result (<c>ETag</c> header). Bare values are quoted
    /// automatically; an explicit <c>W/</c> prefix or <paramref name="weak"/> emits an
    /// RFC 9110 weak tag (<c>W/"..."</c>). Opaque tag characters are validated per
    /// RFC 9110 §8.8.3.1 (<c>etagc</c>).
    /// </summary>
    public static ElsieResult WithETag(this ElsieResult result, string etag, bool weak = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(etag);

        var value = etag.Trim();
        if (value.StartsWith("W/", StringComparison.Ordinal))
        {
            weak = true;
            value = value[2..].Trim();
        }

        var opaqueTag = UnquoteAndValidate(value);
        var headerValue = (weak ? "W/\"" : "\"") + opaqueTag + "\"";
        return result.WithHeader("ETag", headerValue);
    }

    /// <summary>
    /// Compute a strong ETag from this result's buffered body (SHA-256, lowercase hex) and
    /// attach it. Deterministic: equal bodies produce equal tags. Throws for streamed
    /// (<see cref="ElsieResult.BodyWriter"/>-only) results, which have no bytes to hash.
    /// </summary>
    public static ElsieResult WithComputedETag(this ElsieResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Body is not { } body)
        {
            throw new InvalidOperationException(
                "WithComputedETag requires a buffered body (Bytes/Text/Json results); streamed results have no bytes to hash.");
        }

        var hash = SHA256.HashData(body.Span);
        return result.WithETag(Convert.ToHexString(hash).ToLowerInvariant());
    }

    /// <summary>Attach an HTTP-date <c>Last-Modified</c> header for If-Modified-Since / If-Unmodified-Since evaluation.</summary>
    public static ElsieResult WithLastModified(this ElsieResult result, DateTimeOffset lastModified)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.WithHeader("Last-Modified", lastModified.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Evaluate RFC 9110 conditional request headers against this result's validators and return:
    /// <list type="bullet">
    /// <item>304 Not Modified when an If-None-Match (weak comparison) or If-Modified-Since validator matches for GET/HEAD — no body, no Content-Length, validators preserved;</item>
    /// <item>412 Precondition Failed when If-Match / If-Unmodified-Since (strong comparison) fails, or an If-None-Match match occurs for unsafe methods;</item>
    /// <item>the result unchanged otherwise (precedence per RFC 9110 §13.2.2: If-Match &gt; If-Unmodified-Since &gt; If-None-Match &gt; If-Modified-Since).</item>
    /// </list>
    /// <see cref="ElsieCaching.ConditionalGet"/> applies this automatically to every handled result.
    /// </summary>
    public static ElsieResult EvaluateConditional(this ElsieResult result, ElsieRequest request)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(request);

        var currentTag = ElsieEntityTag.TryParse(result.Headers["ETag"], out var tag) ? tag : null;
        var lastModified = TryParseHttpDate(result.Headers["Last-Modified"]);
        var isGetOrHead = request.Method is "GET" or "HEAD";
        var hasRepresentation = result.StatusCode is >= 200 and < 400;

        // RFC 9110 §13.2.2 evaluation order.
        // Step 1: If-Match present -> false => 412.
        if (request.GetHeader("If-Match") is { } ifMatch)
        {
            return IfMatchMatches(ifMatch, currentTag, hasRepresentation) ? result : PreconditionFailed();
        }

        // Step 2: If-Match absent + If-Unmodified-Since present -> false => 412.
        if (request.GetHeader("If-Unmodified-Since") is { } ifUnmodifiedSince &&
            lastModified is { } unmodifiedLastModified &&
            IfUnmodifiedSinceFails(ifUnmodifiedSince, unmodifiedLastModified))
        {
            return PreconditionFailed();
        }

        // Step 3: If-None-Match present -> match => 304 (GET/HEAD) / 412 (other methods).
        if (request.GetHeader("If-None-Match") is { } ifNoneMatch)
        {
            if (IfNoneMatchMatches(ifNoneMatch, currentTag, hasRepresentation))
            {
                return isGetOrHead ? NotModified(result) : PreconditionFailed();
            }

            return result;
        }

        // Step 4: If-None-Match absent + If-Modified-Since present (GET/HEAD) -> not modified => 304.
        if (isGetOrHead &&
            request.GetHeader("If-Modified-Since") is { } ifModifiedSince &&
            lastModified is { } lm &&
            IfModifiedSinceMatches(ifModifiedSince, lm))
        {
            return NotModified(result);
        }

        return result;
    }

    private static bool IfNoneMatchMatches(string header, ElsieEntityTag? currentTag, bool hasRepresentation)
    {
        if (header.Trim() == "*")
        {
            // RFC 9110 §13.1.2: "*" matches when any current representation exists.
            return hasRepresentation;
        }

        if (currentTag is null)
        {
            return false;
        }

        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (ElsieEntityTag.TryParse(part, out var candidate) &&
                candidate is not null &&
                currentTag.WeakEquals(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IfMatchMatches(string header, ElsieEntityTag? currentTag, bool hasRepresentation)
    {
        if (header.Trim() == "*")
        {
            // RFC 9110 §13.1.1: "*" matches when a current representation exists.
            return hasRepresentation;
        }

        if (currentTag is null)
        {
            return false;
        }

        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (ElsieEntityTag.TryParse(part, out var candidate) &&
                candidate is not null &&
                currentTag.StrongEquals(candidate))
            {
                return true;
            }
        }

        return false;
    }

    // HTTP-date granularity is one second; tolerate that precision (same convention as the
    // static-file handler).
    private static bool IfModifiedSinceMatches(string header, DateTimeOffset lastModified) =>
        TryParseHttpDate(header) is { } since &&
        lastModified.UtcDateTime <= since.UtcDateTime.AddSeconds(1);

    private static bool IfUnmodifiedSinceFails(string header, DateTimeOffset lastModified) =>
        TryParseHttpDate(header) is { } since &&
        lastModified.UtcDateTime > since.UtcDateTime.AddSeconds(1);

    private static DateTimeOffset? TryParseHttpDate(string? value) =>
        value is not null &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    /// <summary>Build a 304 with ETag/Cache-Control/Last-Modified/etc. preserved but no payload.</summary>
    private static ElsieResult NotModified(ElsieResult source)
    {
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var (name, values) in source.Headers)
        {
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue; // a 304 has no payload, so a (stale) Content-Length must not survive
            }

            foreach (var value in values)
            {
                headers.Add(new KeyValuePair<string, string>(name, value));
            }
        }

        return headers.Count == 0
            ? ElsieResult.Status(304)
            : ElsieResult.Status(304).WithHeaders(headers);
    }

    private static ElsieResult PreconditionFailed() => ElsieResult.Problem(412, title: "Precondition Failed");

    private static string UnquoteAndValidate(string value)
    {
        if (value.StartsWith('"'))
        {
            if (value.Length < 2 || !value.EndsWith('"'))
            {
                throw new ArgumentException("Invalid entity-tag: unbalanced quotes.", nameof(value));
            }

            value = value[1..^1];
        }

        if (value.Length == 0)
        {
            throw new ArgumentException("Invalid entity-tag: empty opaque tag.", nameof(value));
        }

        foreach (var c in value)
        {
            // etagc = %x21 / %x23-7E / obs-text (RFC 9110 §8.8.3.1)
            if (c != 0x21 && (c < 0x23 || c > 0x7E) && c < 0x80)
            {
                throw new ArgumentException(
                    $"Invalid entity-tag character '\\u{(int)c:x4}' (allowed: '!', 0x23-0x7E, obs-text).",
                    nameof(value));
            }
        }

        return value;
    }
}

/// <summary>Response-caching factories for dynamic routes.</summary>
public static class ElsieCaching
{
    /// <summary>
    /// After-hook that automatically evaluates RFC 9110 conditional request headers
    /// (If-None-Match, If-Modified-Since, If-Match, If-Unmodified-Since) against the handler's
    /// result, rewriting to 304 Not Modified / 412 Precondition Failed (see
    /// <see cref="ElsieResultConditionalGetExtensions.EvaluateConditional"/>). Results without
    /// <see cref="ElsieResultConditionalGetExtensions.WithETag"/> /
    /// <see cref="ElsieResultConditionalGetExtensions.WithLastModified"/> validators are unaffected
    /// except for the RFC wildcard/if-nothing-else cases.
    /// Register once per pipeline, e.g. <c>AddElsieMiddleware(p =&gt; p.Use(ElsieCaching.ConditionalGet()))</c>
    /// (or <c>Use(ElsieCaching.ConditionalGet())</c> inside an <see cref="ElsieModule"/>).
    /// </summary>
    public static Func<ElsieContext, ElsieResult, ElsieResult> ConditionalGet() =>
        static (ctx, result) => result.EvaluateConditional(ctx.Request);
}

/// <summary>RFC 9110 §8.8.3 entity-tag: optional weak marker plus a quoted opaque tag.</summary>
internal sealed class ElsieEntityTag
{
    private ElsieEntityTag(string opaqueTag, bool weak)
    {
        OpaqueTag = opaqueTag;
        Weak = weak;
    }

    public string OpaqueTag { get; }

    public bool Weak { get; }

    /// <summary>Weak comparison: opaque tags are equal regardless of the W/ prefix.</summary>
    public bool WeakEquals(ElsieEntityTag other) =>
        string.Equals(OpaqueTag, other.OpaqueTag, StringComparison.Ordinal);

    /// <summary>Strong comparison: both tags are strong and their opaque tags are equal.</summary>
    public bool StrongEquals(ElsieEntityTag other) => !Weak && !other.Weak && WeakEquals(other);

    public static bool TryParse(string? text, out ElsieEntityTag? tag)
    {
        tag = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        var weak = false;
        if (value.StartsWith("W/", StringComparison.Ordinal))
        {
            weak = true;
            value = value[2..].Trim();
        }

        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return false;
        }

        var opaque = value[1..^1];
        foreach (var c in opaque)
        {
            // etagc = %x21 / %x23-7E / obs-text (RFC 9110 §8.8.3.1)
            if (c != 0x21 && (c < 0x23 || c > 0x7E) && c < 0x80)
            {
                return false;
            }
        }

        tag = new ElsieEntityTag(opaque, weak);
        return true;
    }
}
