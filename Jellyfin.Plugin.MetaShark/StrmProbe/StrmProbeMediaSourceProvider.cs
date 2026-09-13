using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// strm 虚拟直连 MediaSource 提供器。
/// 只对白名单第三方客户端生效；官方 Jellyfin* 与未知客户端一律返回空（走原生行为，不触发后台探针）。
/// 仅新增虚拟源，不修改媒体库数据与原生 MediaSource。
/// </summary>
public sealed class StrmProbeMediaSourceProvider : IMediaSourceProvider
{
    private readonly ILogger<StrmProbeMediaSourceProvider> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IStrmProbeCacheStore _store;
    private readonly IStrmProber _prober;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmProbeMediaSourceProvider"/> class.
    /// </summary>
    /// <param name="logger">日志。</param>
    /// <param name="httpContextAccessor">HTTP 上下文（用于识别客户端）。</param>
    /// <param name="store">探针缓存。</param>
    /// <param name="prober">探针器（缓存未命中后台重探用）。</param>
    public StrmProbeMediaSourceProvider(
        ILogger<StrmProbeMediaSourceProvider> logger,
        IHttpContextAccessor httpContextAccessor,
        IStrmProbeCacheStore store,
        IStrmProber prober)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _prober = prober ?? throw new ArgumentNullException(nameof(prober));
    }

    /// <inheritdoc />
    public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EnableStrmProbeWarmup)
        {
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());
        }

        if (item == null || !StrmFileHelper.IsStrmPath(item.Path))
        {
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());
        }

        var clientName = GetCurrentClientName();
        var whitelist = StrmClientPolicy.ParseWhitelist(config.StrmProbeClientWhitelist);

        // 先做不读库的预决策：原生客户端直接返回，不读缓存、不触发后台探针。
        var preDecision = StrmClientPolicy.Resolve(
            masterEnabled: true,
            directOnCacheMiss: config.EnableStrmProbeDirectOnCacheMiss,
            clientName: clientName,
            whitelist: whitelist,
            cacheHit: false);
        if (!preDecision.ShouldReadCache)
        {
            // 取流补身份：PlaybackInfo 带身份放行后，切源取流可能是纯 api_key
            //（无鉴权头/DeviceId）。此时按取流请求的 mediaSourceId 回查，
            // 能说出虚拟 Guid 即证明见过双源，直接按缓存 key 放行。
            var streamFallback = StrmStreamFallback.TryResolve(
                _httpContextAccessor.HttpContext?.Request,
                item,
                _store,
                DateTime.UtcNow);
            if (streamFallback != null)
            {
                _logger.LogInformation("strm 虚拟直连放行（取流补身份） item={Item}", item.Name);
                return Task.FromResult<IEnumerable<MediaSourceInfo>>(new[] { streamFallback });
            }

            _logger.LogDebug("strm 虚拟直连跳过 client={Client} item={Item}（原生行为）", clientName ?? "<unknown>", item.Name);
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());
        }

        if (!StrmFileHelper.TryReadStrmLink(item.Path, out var url, out var fileSize, out var signature))
        {
            _logger.LogDebug("strm 文件读取失败，走原生行为 path={Path}", item.Path);
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());
        }

        var key = StrmProbeCacheKey.Compute(url, fileSize, signature);
        var now = DateTime.UtcNow;
        var cached = _store.TryGet(key, now);
        var decision = StrmClientPolicy.Resolve(
            masterEnabled: true,
            directOnCacheMiss: config.EnableStrmProbeDirectOnCacheMiss,
            clientName: clientName,
            whitelist: whitelist,
            cacheHit: cached != null);

        switch (decision.Kind)
        {
            case StrmPlaybackDecisionKind.VirtualCached:
                // 命中分支同样发 openlist 原始签址（Url 列）：openlist 永久 sign 长期有效，
                // 探针解析出的存储直链（DirectUrl）多由存储驱动按请求构造、必然过期，不作下发。
                // 探针产出的 ContentLength/ContentType 是文件级事实，继续用于补全元数据。
                _logger.LogInformation("strm 虚拟直连放行（缓存命中） client={Client} item={Item}", clientName, item.Name);
                return Task.FromResult<IEnumerable<MediaSourceInfo>>(new[] { StrmVirtualSourceFactory.Build(key, cached!.Url, cached.ContentLength, cached.ContentType) });
            case StrmPlaybackDecisionKind.VirtualUnprobed:
                _logger.LogInformation("strm 虚拟直连放行（无缓存，返回原始直链并后台重探） client={Client} item={Item}", clientName, item.Name);
                EnqueueBackgroundProbe(url, fileSize, signature, key);
                return Task.FromResult<IEnumerable<MediaSourceInfo>>(new[] { StrmVirtualSourceFactory.Build(key, url, null, null) });
            case StrmPlaybackDecisionKind.NativeWithWarmup:
                _logger.LogInformation("strm 无缓存且已关闭直连返回，走原生并后台预热 client={Client} item={Item}", clientName, item.Name);
                EnqueueBackgroundProbe(url, fileSize, signature, key);
                return Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());
            default:
                return Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());
        }
    }

    /// <inheritdoc />
    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("虚拟直连源无需打开（RequiresOpening=false）。");
    }

    internal string? GetCurrentClientName()
    {
        try
        {
            var context = _httpContextAccessor.HttpContext;
            return StrmClientResolver.Resolve(context?.Request, context?.Items);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "解析客户端名称失败，按未知客户端处理");
            return null;
        }
    }

    internal void EnqueueBackgroundProbe(string url, long fileSize, string signature, string key)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _prober.ProbeAsync(url, CancellationToken.None).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                _store.Set(new StrmProbeCacheEntry
                {
                    Key = key,
                    Url = url,
                    FileSize = fileSize,
                    Signature = signature,
                    DirectUrl = result.DirectUrl,
                    ContentType = result.ContentType,
                    ContentLength = result.ContentLength,
                    ProbedAtUtc = now,
                    ExpiresAtUtc = now.Add(StrmProbeConstants.DefaultTtl),
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "strm 后台探针失败 url={Url}", url);
            }
        });
    }
}
