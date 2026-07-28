using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Elsie.Testing;

/// <summary>
/// Fluent-style assertion helpers for <see cref="HttpResponseMessage"/> in Elsie tests.
/// Throws <see cref="HttpResponseAssertionException"/> on failure.
/// </summary>
public static class HttpResponseAssertions
{
    public static HttpResponseMessage AssertStatus(this HttpResponseMessage response, HttpStatusCode expected)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.StatusCode != expected)
        {
            throw new HttpResponseAssertionException(
                $"Expected status {(int)expected} ({expected}) but was {(int)response.StatusCode} ({response.StatusCode}).");
        }

        return response;
    }

    public static HttpResponseMessage AssertStatus(this HttpResponseMessage response, int expected) =>
        response.AssertStatus((HttpStatusCode)expected);

    public static HttpResponseMessage AssertHeader(
        this HttpResponseMessage response,
        string name,
        string expectedValue,
        StringComparison comparison = StringComparison.Ordinal)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (!TryGetHeader(response, name, out var values))
        {
            throw new HttpResponseAssertionException($"Expected header '{name}' but it was missing.");
        }

        var joined = string.Join(',', values);
        if (!values.Any(v => string.Equals(v, expectedValue, comparison))
            && !string.Equals(joined, expectedValue, comparison))
        {
            throw new HttpResponseAssertionException(
                $"Expected header '{name}' to be '{expectedValue}' but was '{joined}'.");
        }

        return response;
    }

    public static HttpResponseMessage AssertHeaderContains(
        this HttpResponseMessage response,
        string name,
        string expectedSubstring,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (!TryGetHeader(response, name, out var values))
        {
            throw new HttpResponseAssertionException($"Expected header '{name}' but it was missing.");
        }

        var joined = string.Join(',', values);
        if (joined.IndexOf(expectedSubstring, comparison) < 0)
        {
            throw new HttpResponseAssertionException(
                $"Expected header '{name}' to contain '{expectedSubstring}' but was '{joined}'.");
        }

        return response;
    }

    public static async Task<string> AssertTextAsync(
        this HttpResponseMessage response,
        string? expected = null,
        StringComparison comparison = StringComparison.Ordinal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (expected is not null && !string.Equals(body, expected, comparison))
        {
            throw new HttpResponseAssertionException(
                $"Expected body '{expected}' but was '{body}'.");
        }

        return body;
    }

    public static async Task<T?> AssertJsonAsync<T>(
        this HttpResponseMessage response,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        return await response.Content
            .ReadFromJsonAsync<T>(options ?? ElsieJson.DefaultOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool TryGetHeader(HttpResponseMessage response, string name, out IEnumerable<string> values)
    {
        if (response.Headers.TryGetValues(name, out var headerValues))
        {
            values = headerValues;
            return true;
        }

        if (response.Content.Headers.TryGetValues(name, out var contentValues))
        {
            values = contentValues;
            return true;
        }

        values = Array.Empty<string>();
        return false;
    }
}

/// <summary>Thrown when an Elsie test assertion on an HTTP response fails.</summary>
public sealed class HttpResponseAssertionException : Exception
{
    public HttpResponseAssertionException(string message) : base(message)
    {
    }
}
