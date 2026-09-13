using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// 扫描入库后台预热服务：订阅 ItemAdded，对新增 strm 条目做后台预热探针写缓存，不阻塞扫描主流程。
/// 总开关关闭时不做任何文件 IO 与探针。
/// </summary>
public sealed class StrmProbeWarmupService : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly IStrmProbeCacheStore _store;
    private readonly IStrmProber _prober;
    private readonly ILogger<StrmProbeWarmupService> _logger;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(StrmProbeConstants.MaxConcurrentWarmups, StrmProbeConstants.MaxConcurrentWarmups);
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmProbeWarmupService"/> class.
    /// </summary>
    /// <param name="libraryManager">媒体库管理器。</param>
    /// <param name="store">探针缓存。</param>
    /// <param name="prober">探针器。</param>
    /// <param name="logger">日志。</param>
    public StrmProbeWarmupService(
        ILibraryManager libraryManager,
        IStrmProbeCacheStore store,
        IStrmProber prober,
        ILogger<StrmProbeWarmupService> logger)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _prober = prober ?? throw new ArgumentNullException(nameof(prober));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    internal void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EnableStrmProbeWarmup)
        {
            return;
        }

        var path = e.Item?.Path;
        if (!StrmFileHelper.IsStrmPath(path))
        {
            return;
        }

        // 不阻塞扫描主流程：仅入队，后台执行。
        _ = WarmupInBackgroundAsync(path!, CancellationToken.None);
    }

    internal async Task WarmupInBackgroundAsync(string strmPath, CancellationToken cancellationToken)
    {
        var entered = await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        if (!entered)
        {
            _logger.LogDebug("strm 预热并发已满，跳过 path={Path}", strmPath);
            return;
        }

        try
        {
            await WarmupSingleFileAsync(strmPath, _store, _prober, _logger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 单文件预热：命中且未过期直接返回；否则探针并写缓存。失败只记日志，不抛异常。
    /// </summary>
    /// <param name="strmPath">strm 文件路径。</param>
    /// <param name="store">缓存。</param>
    /// <param name="prober">探针器。</param>
    /// <param name="logger">日志。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>true 表示已写入/已命中；false 表示跳过或失败。</returns>
    public static async Task<bool> WarmupSingleFileAsync(
        string strmPath,
        IStrmProbeCacheStore store,
        IStrmProber prober,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!StrmFileHelper.TryReadStrmLink(strmPath, out var url, out var fileSize, out var signature))
        {
            logger.LogDebug("strm 预热跳过（非直链或不可读） path={Path}", strmPath);
            return false;
        }

        var key = StrmProbeCacheKey.Compute(url, fileSize, signature);
        var now = DateTime.UtcNow;
        var cached = store.TryGet(key, now);
        if (cached != null)
        {
            logger.LogDebug("strm 预热跳过（缓存命中） key={CacheKey} path={Path}", key, strmPath);
            return true;
        }

        try
        {
            var result = await prober.ProbeAsync(url, cancellationToken).ConfigureAwait(false);
            var probedAt = DateTime.UtcNow;
            store.Set(new StrmProbeCacheEntry
            {
                Key = key,
                Url = url,
                FileSize = fileSize,
                Signature = signature,
                DirectUrl = result.DirectUrl,
                ContentType = result.ContentType,
                ContentLength = result.ContentLength,
                ProbedAtUtc = probedAt,
                ExpiresAtUtc = probedAt.Add(StrmProbeConstants.DefaultTtl),
            });
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException || ex is InvalidOperationException)
        {
            logger.LogWarning(ex, "strm 预热探针失败 path={Path} url={Url}", strmPath, url);
            return false;
        }
    }
}
