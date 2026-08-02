using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Elsie.Auth;

/// <summary>
/// Discovers JWT signing keys from an OIDC authority (<c>/.well-known/openid-configuration</c>)
/// or an explicit JWKS endpoint. Backed by <see cref="ConfigurationManager{T}"/> so metadata is
/// cached and refreshed in the background on <see cref="ElsieJwtBearerOptions.JwksRefreshInterval"/>.
/// Keys from previous key sets are kept during rollover so tokens signed with the previous key
/// keep validating for a grace period. An unreachable authority never throws out of request
/// handling: discovery failures return the previously known keys (empty on the very first failure,
/// which makes signature validation fail cleanly → 401).
/// </summary>
public sealed class JwksResolver
{
    private const int MaxCachedKeys = 32;

    private readonly ConfigurationManager<OpenIdConnectConfiguration> _manager;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, SecurityKey> _keysByKid = new(StringComparer.Ordinal);
    private readonly Queue<string> _kidOrder = new();

    /// <summary>Creates a resolver over an explicit metadata/JWKS address.</summary>
    public JwksResolver(
        string metadataAddress,
        IConfigurationRetriever<OpenIdConnectConfiguration> retriever,
        IDocumentRetriever documentRetriever,
        TimeSpan? refreshInterval = null,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataAddress);
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(documentRetriever);

        var interval = refreshInterval ?? TimeSpan.FromHours(24);
        _manager = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress,
            retriever,
            documentRetriever)
        {
            AutomaticRefreshInterval = interval,
            RefreshInterval = interval
        };
        _logger = logger;
    }

    /// <summary>
    /// Builds a resolver from JWT options. Returns null when <see cref="ElsieJwtBearerOptions.SigningKey"/>
    /// is set (static key path) or when neither <see cref="ElsieJwtBearerOptions.JwksUrl"/> nor
    /// <see cref="ElsieJwtBearerOptions.Authority"/> is configured.
    /// </summary>
    /// <param name="options">JWT bearer options.</param>
    /// <param name="refreshInterval">Optional override for the options' refresh interval.</param>
    /// <param name="httpClient">Optional HTTP client used for metadata discovery.</param>
    /// <param name="allowHttpMetadata">
    /// Allow plain-HTTP metadata endpoints. Defaults to false (strict); tests may pass true.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public static JwksResolver? TryCreate(
        ElsieJwtBearerOptions options,
        TimeSpan? refreshInterval = null,
        HttpClient? httpClient = null,
        bool allowHttpMetadata = false,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SigningKey is not null)
        {
            return null;
        }

        string metadataAddress;
        IConfigurationRetriever<OpenIdConnectConfiguration> retriever;
        if (!string.IsNullOrWhiteSpace(options.JwksUrl))
        {
            metadataAddress = options.JwksUrl!;
            retriever = new JsonWebKeySetRetriever();
        }
        else if (!string.IsNullOrWhiteSpace(options.Authority))
        {
            metadataAddress = options.Authority!.TrimEnd('/') + "/.well-known/openid-configuration";
            retriever = new OpenIdConnectConfigurationRetriever();
        }
        else
        {
            return null;
        }

        var documentRetriever = new HttpDocumentRetriever(httpClient ?? new HttpClient())
        {
            RequireHttps = !allowHttpMetadata && !options.AllowHttpMetadata
        };
        return new JwksResolver(
            metadataAddress,
            retriever,
            documentRetriever,
            refreshInterval ?? options.JwksRefreshInterval,
            logger);
    }

    /// <summary>
    /// Returns the current signing keys plus any rolled-over keys still cached.
    /// Empty when discovery has never succeeded (callers must fail validation → 401).
    /// </summary>
    public async Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await _manager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            Merge(config.SigningKeys);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(
                ex,
                "JWKS discovery failed for {Metadata}; falling back to {KeyCount} previously resolved keys.",
                _manager.MetadataAddress,
                KeyCount);
        }

        lock (_gate)
        {
            return _keysByKid.Values.ToArray();
        }
    }

    /// <summary>Number of signing keys currently cached (current + rolled-over).</summary>
    public int KeyCount
    {
        get
        {
            lock (_gate)
            {
                return _keysByKid.Count;
            }
        }
    }

    /// <summary>Forces a metadata re-fetch on the next <see cref="GetSigningKeysAsync"/> (tests, manual rotation).</summary>
    internal void RequestRefresh() => _manager.RequestRefresh();

    private void Merge(IEnumerable<SecurityKey>? keys)
    {
        if (keys is null)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var key in keys)
            {
                if (string.IsNullOrEmpty(key.KeyId) || _keysByKid.ContainsKey(key.KeyId))
                {
                    continue;
                }

                if (_keysByKid.Count >= MaxCachedKeys && _kidOrder.Count > 0)
                {
                    _keysByKid.Remove(_kidOrder.Dequeue());
                }

                _keysByKid[key.KeyId] = key;
                _kidOrder.Enqueue(key.KeyId);
            }
        }
    }

    /// <summary>Reads a bare JWKS document (no OIDC metadata envelope).</summary>
    private sealed class JsonWebKeySetRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
    {
        public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
            string address,
            IDocumentRetriever retriever,
            CancellationToken cancel)
        {
            ArgumentNullException.ThrowIfNull(retriever);
            var document = await retriever.GetDocumentAsync(address, cancel).ConfigureAwait(false);
            var keySet = new JsonWebKeySet(document);
            var configuration = new OpenIdConnectConfiguration { JsonWebKeySet = keySet };
            foreach (var key in keySet.GetSigningKeys())
            {
                configuration.SigningKeys.Add(key);
            }

            return configuration;
        }
    }
}
