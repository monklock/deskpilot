using System.Net;
using DeskPilot.Voice.Abstractions;

namespace DeskPilot.Infrastructure.ModelManagement;

/// <summary>Downloads model resources while validating every redirect origin.</summary>
public interface IVoiceModelHttpClient
{
    /// <summary>Sends a GET request and consumes its body inside the bounded operation lifetime.</summary>
    Task<T> GetAsync<T>(
        Uri uri,
        Func<HttpResponseMessage, CancellationToken, Task<T>> consumeAsync,
        CancellationToken cancellationToken);
}

/// <summary>Owns a non-redirecting transport and follows only explicitly allowed HTTPS origins.</summary>
public sealed class SafeVoiceModelHttpClient : IVoiceModelHttpClient, IDisposable
{
    private const int MaximumRedirects = 5;
    private readonly HttpMessageInvoker _invoker;
    private readonly HashSet<string> _allowedOrigins;
    private readonly TimeSpan _requestTimeout;

    /// <summary>Creates a client over a transport whose automatic redirects are disabled.</summary>
    public SafeVoiceModelHttpClient(
        HttpMessageHandler transport,
        IEnumerable<Uri> allowedOrigins,
        TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(allowedOrigins);
        _allowedOrigins = new HashSet<string>(
            allowedOrigins.Select(GetValidatedOrigin),
            StringComparer.OrdinalIgnoreCase);
        if (_allowedOrigins.Count == 0)
        {
            throw new ArgumentException("At least one allowed model origin is required.", nameof(allowedOrigins));
        }

        _requestTimeout = requestTimeout ?? TimeSpan.FromMinutes(5);
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        _invoker = new HttpMessageInvoker(transport, disposeHandler: true);
    }

    /// <inheritdoc />
    public async Task<T> GetAsync<T>(
        Uri uri,
        Func<HttpResponseMessage, CancellationToken, Task<T>> consumeAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(consumeAsync);
        ValidateAllowedOrigin(uri);
        var currentUri = uri;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);

        try
        {
            for (var redirectCount = 0; ; redirectCount++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                var response = await _invoker.SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (!IsRedirect(response.StatusCode))
                {
                    using (response)
                    {
                        return await consumeAsync(response, timeout.Token).ConfigureAwait(false);
                    }
                }

                if (redirectCount >= MaximumRedirects || response.Headers.Location is null)
                {
                    response.Dispose();
                    throw InvalidOrigin("Цепочка перенаправлений модели некорректна.");
                }

                var nextUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(currentUri, response.Headers.Location);
                response.Dispose();
                ValidateAllowedOrigin(nextUri);
                currentUri = nextUri;
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("Voice model request timed out.", exception);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _invoker.Dispose();

    private void ValidateAllowedOrigin(Uri uri)
    {
        var origin = GetValidatedOrigin(uri);
        if (!_allowedOrigins.Contains(origin))
        {
            throw InvalidOrigin("Источник модели не разрешён.");
        }
    }

    private static string GetValidatedOrigin(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw InvalidOrigin("Источник модели должен использовать разрешённый HTTPS origin.");
        }

        return $"https://{uri.IdnHost}:{uri.Port}";
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently
        or HttpStatusCode.Redirect
        or HttpStatusCode.RedirectMethod
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    private static VoiceModelCatalogException InvalidOrigin(string message) =>
        new(VoiceModelResultCode.InvalidManifest, message);
}
