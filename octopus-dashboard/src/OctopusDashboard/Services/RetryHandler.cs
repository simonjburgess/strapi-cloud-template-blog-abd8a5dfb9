using System.Net;

namespace OctopusDashboard.Services;

/// <summary>Retries a GET once after a dropped connection, a 429 or a 5xx, which the Octopus API returns occasionally.</summary>
public sealed class RetryHandler : DelegatingHandler
{
    internal static TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(500);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Get) return await base.SendAsync(request, ct);
        try
        {
            var response = await base.SendAsync(request, ct);
            if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500) return response;
            response.Dispose();
        }
        catch (HttpRequestException) { }

        await Task.Delay(Delay, ct);
        return await base.SendAsync(request, ct);
    }
}
