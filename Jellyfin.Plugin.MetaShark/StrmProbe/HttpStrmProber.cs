using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// 基于 HTTP HEAD + Range 回退的轻量探针实现。只取响应头，不下载正文。
/// </summary>
public sealed class HttpStrmProber : IStrmProber
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpStrmProber> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpStrmProber"/> class.
    /// </summary>
    /// <param name="httpClientFactory">HTTP 客户端工厂。</param>
    /// <param name="logger">日志。</param>
    public HttpStrmProber(IHttpClientFactory httpClientFactory, ILogger<HttpStrmProber> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<StrmProbeResult> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("探针 URL 不能为空", nameof(url));
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        // 先 HEAD（自动跟随跳转，拿到最终地址）。
        var head = await TryHeadAsync(client, url, cancellationToken).ConfigureAwait(false);
        if (head != null)
        {
            return head;
        }

        // 部分网盘站禁用 HEAD，回退为 Range 取首字节。
        var ranged = await TryRangeGetAsync(client, url, cancellationToken).ConfigureAwait(false);
        if (ranged != null)
        {
            return ranged;
        }

        throw new HttpRequestException("直链探针失败：HEAD 与 Range GET 均无有效响应 url=" + url);
    }

    private async Task<StrmProbeResult?> TryHeadAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 400)
            {
                var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
                _logger.LogDebug("直链探针 HEAD 成功 url={Url} final={Final} status={Status}", url, finalUrl, (int)response.StatusCode);
                return new StrmProbeResult
                {
                    Url = url,
                    DirectUrl = finalUrl,
                    ContentType = response.Content.Headers.ContentType?.ToString(),
                    ContentLength = response.Content.Headers.ContentLength,
                    ProbedAtUtc = DateTime.UtcNow,
                };
            }

            _logger.LogDebug("直链探针 HEAD 非成功状态 url={Url} status={Status}，尝试 Range 回退", url, (int)response.StatusCode);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException)
        {
            _logger.LogDebug(ex, "直链探针 HEAD 异常 url={Url}，尝试 Range 回退", url);
            return null;
        }
    }

    private async Task<StrmProbeResult?> TryRangeGetAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.PartialContent || (int)response.StatusCode == 416)
            {
                var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
                long? length = response.Content.Headers.ContentLength;
                var contentRange = response.Content.Headers.ContentRange;
                if (contentRange?.Length.HasValue == true)
                {
                    length = contentRange.Length;
                }

                _logger.LogDebug("直链探针 Range 成功 url={Url} final={Final} status={Status}", url, finalUrl, (int)response.StatusCode);
                return new StrmProbeResult
                {
                    Url = url,
                    DirectUrl = finalUrl,
                    ContentType = response.Content.Headers.ContentType?.ToString(),
                    ContentLength = length,
                    ProbedAtUtc = DateTime.UtcNow,
                };
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException)
        {
            _logger.LogDebug(ex, "直链探针 Range 异常 url={Url}", url);
            return null;
        }
    }
}
