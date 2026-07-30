using System.Globalization;

namespace Elsie.Cors;

internal static class ElsieCorsEvaluator
{
    public static bool TryBuildPreflightHeaders(
        ElsieCorsPolicy policy,
        string origin,
        string? requestMethod,
        string? requestHeaders,
        out IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        headers = Array.Empty<KeyValuePair<string, string>>();
        if (!policy.IsOriginAllowed(origin))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(requestMethod))
        {
            requestMethod = requestMethod.ToUpperInvariant();
            if (!policy.AllowAnyMethod &&
                policy.Methods.Count > 0 &&
                !policy.Methods.Contains(requestMethod))
            {
                return false;
            }
        }

        if (!string.IsNullOrEmpty(requestHeaders) &&
            !policy.AllowAnyHeader &&
            policy.Headers.Count > 0)
        {
            foreach (var header in requestHeaders.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!policy.Headers.Contains(header))
                {
                    return false;
                }
            }
        }

        var bag = new List<KeyValuePair<string, string>>();
        WriteOrigin(bag, policy, origin);

        if (policy.AllowAnyMethod)
        {
            bag.Add(new("Access-Control-Allow-Methods", requestMethod is { Length: > 0 } ? requestMethod : "*"));
        }
        else if (policy.Methods.Count > 0)
        {
            bag.Add(new("Access-Control-Allow-Methods", string.Join(", ", policy.Methods)));
        }
        else if (!string.IsNullOrEmpty(requestMethod))
        {
            bag.Add(new("Access-Control-Allow-Methods", requestMethod));
        }

        if (policy.AllowAnyHeader)
        {
            bag.Add(new(
                "Access-Control-Allow-Headers",
                string.IsNullOrEmpty(requestHeaders) ? "*" : requestHeaders));
        }
        else if (policy.Headers.Count > 0)
        {
            bag.Add(new("Access-Control-Allow-Headers", string.Join(", ", policy.Headers)));
        }
        else if (!string.IsNullOrEmpty(requestHeaders))
        {
            bag.Add(new("Access-Control-Allow-Headers", requestHeaders));
        }

        if (policy.PreflightMaxAge is { } maxAge)
        {
            bag.Add(new(
                "Access-Control-Max-Age",
                ((int)maxAge.TotalSeconds).ToString(CultureInfo.InvariantCulture)));
        }

        headers = bag;
        return true;
    }

    public static bool TryBuildActualHeaders(
        ElsieCorsPolicy policy,
        string origin,
        out IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        headers = Array.Empty<KeyValuePair<string, string>>();
        if (!policy.IsOriginAllowed(origin))
        {
            return false;
        }

        var bag = new List<KeyValuePair<string, string>>();
        WriteOrigin(bag, policy, origin);
        if (policy.ExposedHeaders.Count > 0)
        {
            bag.Add(new("Access-Control-Expose-Headers", string.Join(", ", policy.ExposedHeaders)));
        }

        headers = bag;
        return true;
    }

    private static void WriteOrigin(List<KeyValuePair<string, string>> bag, ElsieCorsPolicy policy, string origin)
    {
        if (policy.SupportsCredentials)
        {
            bag.Add(new("Access-Control-Allow-Origin", origin));
            bag.Add(new("Access-Control-Allow-Credentials", "true"));
            bag.Add(new("Vary", "Origin"));
        }
        else if (policy.AllowAnyOrigin)
        {
            bag.Add(new("Access-Control-Allow-Origin", "*"));
        }
        else
        {
            bag.Add(new("Access-Control-Allow-Origin", origin));
            bag.Add(new("Vary", "Origin"));
        }
    }
}
