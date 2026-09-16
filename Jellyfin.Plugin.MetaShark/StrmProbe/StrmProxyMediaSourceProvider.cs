using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaShark.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// MetaShark 代理源 MediaSource 提供器。
/// 只对白名单第三方客户端、且开启「MetaShark 代理源」开关的 .strm 条目额外提供一个虚拟源：
/// Id 由缓存 key 派生确定性 Guid，Path 指向插件签名端点（服务端按 Range 中转直链字节）。
/// 仅新增虚拟源，不修改媒体库数据与原生 MediaSource；原生源与 302 直跳行为不受影响。
/// 任何解析失败都返回空（fail-closed）。
/// </summary>
public sealed class StrmProxyMediaSourceProvider : IMediaSourceProvider
{
    private static readonly IEnumerable<MediaSourceInfo> Empty = Array.Empty<MediaSourceInfo>();

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly StrmProxyTokenService _tokenService;
    private readonly ILogger<StrmProxyMediaSourceProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmProxyMediaSourceProvider"/> class.
    /// </summary>
    /// <param name="httpContextAccessor">HTTP 上下文（识别客户端）。</param>
    /// <param name="tokenService">代理源票据服务。</param>
    /// <param name="logger">日志。</param>
    public StrmProxyMediaSourceProvider(
        IHttpContextAccessor httpContextAccessor,
        StrmProxyTokenService tokenService,
        ILogger<StrmProxyMediaSourceProvider> logger)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 测试用配置覆盖（沿用仓库既有 <c>TestConfigOverride</c> 模式，保证单测离线确定性）。
    /// </summary>
    internal PluginConfiguration? TestConfigOverride { get; set; }

    /// <inheritdoc />
    public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult(BuildSources(item));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MetaShark 代理源构建失败，走原生行为");
            return Task.FromResult(Empty);
        }
    }

    /// <inheritdoc />
    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("MetaShark 代理源无需打开（RequiresOpening=false）。");
    }

    /// <summary>
    /// 构造代理源（无则返回空）。白名单门控 + 开关 + strm + 可签发票据，条件全中才新增一个源。
    /// </summary>
    /// <param name="item">当前条目（可为空）。</param>
    /// <returns>代理源集合（至多一个）。</returns>
    internal IEnumerable<MediaSourceInfo> BuildSources(BaseItem? item)
    {
        var config = TestConfigOverride ?? Plugin.Instance?.Configuration;
        if (config == null || !config.EnableStrmProxySource)
        {
            return Empty;
        }

        if (item == null || !StrmFileHelper.IsStrmPath(item.Path))
        {
            return Empty;
        }

        var clientName = StrmClientResolver.Resolve(_httpContextAccessor.HttpContext);
        var whitelist = StrmClientPolicy.ParseWhitelist(config.StrmProbeClientWhitelist);
        if (!StrmClientPolicy.IsWhitelistedThirdParty(clientName, whitelist))
        {
            _logger.LogDebug("MetaShark 代理源跳过 client={Client} item={Item}（非白名单）", clientName ?? "<unknown>", item.Name);
            return Empty;
        }

        if (!StrmFileHelper.TryReadStrmLink(item.Path, out var url, out var fileSize, out var signature)
            || !StrmDirectRedirectFilter.IsHttpUrl(url))
        {
            return Empty;
        }

        var key = StrmSourceKey.Compute(url, fileSize, signature);
        if (!_tokenService.TryCreateTicket(item.Id, DateTime.UtcNow, out var expiresAtUnix, out var signatureValue))
        {
            _logger.LogDebug("MetaShark 代理源票据签发失败，走原生行为 item={Item}", item.Name);
            return Empty;
        }

        var proxyPath = StrmProxyUrl.BuildAbsoluteUrl(
            _httpContextAccessor.HttpContext?.Request,
            item.Id,
            expiresAtUnix,
            signatureValue);

        _logger.LogInformation("MetaShark 代理源已下发 client={Client} item={Item}", clientName, item.Name);
        return new[] { StrmProxySourceFactory.Build(key, proxyPath, url) };
    }
}
