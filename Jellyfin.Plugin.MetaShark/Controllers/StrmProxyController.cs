using System;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaShark.StrmProbe;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.Controllers
{
    /// <summary>
    /// MetaShark 代理源取流端点。
    /// 匿名但只认插件自签票据（HMAC-SHA256 + 过期，票据绑定条目）；校验通过后按 Range
    /// 从 .strm 首行直链取流，原样透传 200/206/416 与 Content-Range/Length/Type。
    /// 供「MetaShark 代理」虚拟源 Path 使用：播放器无法跟随直链 302 跳转、或取流请求
    /// 丢失客户端身份时，仍可凭签名 URL 由服务端中转字节。fail-closed，永不抛异常。
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route(StrmProxyUrl.RoutePrefix)]
    public class StrmProxyController : ControllerBase
    {
        private readonly ILibraryManager _libraryManager;
        private readonly StrmProxyTokenService _tokenService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<StrmProxyController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="StrmProxyController"/> class.
        /// </summary>
        /// <param name="libraryManager">媒体库（按票据条目 Id 取条目）。</param>
        /// <param name="tokenService">代理源票据服务。</param>
        /// <param name="httpClientFactory">HTTP 客户端工厂。</param>
        /// <param name="logger">日志。</param>
        public StrmProxyController(
            ILibraryManager libraryManager,
            StrmProxyTokenService tokenService,
            IHttpClientFactory httpClientFactory,
            ILogger<StrmProxyController> logger)
        {
            _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
            _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 代理取流。校验票据并透传上游 Range 响应。
        /// </summary>
        /// <param name="itemId">条目 Id。</param>
        /// <param name="exp">票据过期时间（Unix 秒）。</param>
        /// <param name="sig">票据签名。</param>
        /// <returns>无返回体（直接写响应流）。</returns>
        [HttpGet("{itemId}")]
        [HttpHead("{itemId}")]
        public async Task Proxy([FromRoute] Guid itemId, [FromQuery] string? exp, [FromQuery] string? sig)
        {
            try
            {
                if (!_tokenService.Validate(itemId, exp, sig, DateTime.UtcNow))
                {
                    _logger.LogDebug("MetaShark 代理源票据校验失败 itemId={ItemId}", itemId);
                    Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                var item = _libraryManager.GetItemById(itemId);
                if (item == null || !StrmFileHelper.IsStrmPath(item.Path))
                {
                    Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                if (!StrmFileHelper.TryReadStrmLink(item.Path, out var url, out _, out _)
                    || !StrmDirectRedirectFilter.IsHttpUrl(url))
                {
                    Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await ProxyToDirectLinkAsync(url).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 客户端断开，正常收尾。
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MetaShark 代理源取流失败 itemId={ItemId}", itemId);
                if (!Response.HasStarted)
                {
                    try
                    {
                        Response.StatusCode = StatusCodes.Status502BadGateway;
                    }
                    catch (Exception)
                    {
                        // 响应已进入不可写状态，忽略。
                    }
                }
            }
        }

        private async Task ProxyToDirectLinkAsync(string directUrl)
        {
            var isHead = HttpMethods.IsHead(Request.Method);
            using var upstreamRequest = new HttpRequestMessage(isHead ? HttpMethod.Head : HttpMethod.Get, directUrl);

            var range = Request.Headers.Range.ToString();
            if (!string.IsNullOrWhiteSpace(range))
            {
                upstreamRequest.Headers.TryAddWithoutValidation("Range", range);
            }

            var httpClient = _httpClientFactory.CreateClient();
            using var upstream = await httpClient
                .SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted)
                .ConfigureAwait(false);

            // 原样透传状态码与 Range 相关头（200/206/416 等）。
            Response.StatusCode = (int)upstream.StatusCode;
            if (upstream.Content.Headers.ContentType != null)
            {
                Response.ContentType = upstream.Content.Headers.ContentType.ToString();
            }

            if (upstream.Content.Headers.ContentRange != null)
            {
                Response.Headers["Content-Range"] = upstream.Content.Headers.ContentRange.ToString();
            }

            if (upstream.Headers.AcceptRanges.Count > 0)
            {
                Response.Headers["Accept-Ranges"] = string.Join(", ", upstream.Headers.AcceptRanges);
            }

            if (upstream.Content.Headers.ContentLength.HasValue)
            {
                Response.ContentLength = upstream.Content.Headers.ContentLength.Value;
            }

            if (isHead)
            {
                return;
            }

            await using var stream = await upstream.Content
                .ReadAsStreamAsync(HttpContext.RequestAborted)
                .ConfigureAwait(false);
            await stream.CopyToAsync(Response.Body, HttpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
