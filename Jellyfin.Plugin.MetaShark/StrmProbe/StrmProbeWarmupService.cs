using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.System;
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
    private readonly IFileSystem _fileSystem;
    private readonly IStrmProbeCacheStore _store;
    private readonly IStrmProber _prober;
    private readonly ILogger<StrmProbeWarmupService> _logger;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(StrmProbeConstants.MaxConcurrentWarmups, StrmProbeConstants.MaxConcurrentWarmups);
    private bool _disposed;

    /// <summary>
    /// 测试用配置覆盖（沿用 <c>MoviePilotApi.TestConfigOverride</c> 模式，保证单测离线确定性）。
    /// </summary>
    internal (bool MasterEnabled, bool LibraryRefreshEnabled)? TestConfigOverride { get; set; }

    /// <summary>
    /// 延迟等待实现（默认 <c>Task.Delay</c>），可注入以保证单测离线确定性。
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    /// <summary>
    /// 单条目刷新实现（默认调 core 的 <c>RefreshMetadata</c>），可注入以保证单测离线确定性。
    /// </summary>
    internal Func<BaseItem, MetadataRefreshOptions, CancellationToken, Task> RefreshItemAsync { get; set; }
        = static async (item, options, cancellationToken) =>
        {
            await item.RefreshMetadata(options, cancellationToken).ConfigureAwait(false);
        };

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmProbeWarmupService"/> class.
    /// </summary>
    /// <param name="libraryManager">媒体库管理器。</param>
    /// <param name="fileSystem">文件系统（入库真探构造按次目录服务用）。</param>
    /// <param name="store">探针缓存。</param>
    /// <param name="prober">探针器。</param>
    /// <param name="logger">日志。</param>
    public StrmProbeWarmupService(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        IStrmProbeCacheStore store,
        IStrmProber prober,
        ILogger<StrmProbeWarmupService> logger)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
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

        // 入库真探（默认关闭）：去抖后在后台做一次完整远程探测，把流信息写入媒体库，
        // 探测必经 ffprobe 缓存装饰器，顺带预填缓存，使首次详情页也不再真实出网。
        if (config.EnableStrmProbeLibraryRefresh && e.Item != null)
        {
            _ = DelayedTrueProbeAsync(e.Item.Id, CancellationToken.None);
        }
    }

    private bool IsLibraryRefreshEnabled()
    {
        if (TestConfigOverride.HasValue)
        {
            return TestConfigOverride.Value.MasterEnabled && TestConfigOverride.Value.LibraryRefreshEnabled;
        }

        var config = Plugin.Instance?.Configuration;
        return config != null && config.EnableStrmProbeWarmup && config.EnableStrmProbeLibraryRefresh;
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
    /// 延迟入库真探：等待刮削完成，重新解析条目后做一次带远程探测的刷新。
    /// 失败只记日志，不抛异常，不阻塞任何请求路径。
    /// </summary>
    /// <param name="itemId">条目 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    internal async Task DelayedTrueProbeAsync(Guid itemId, CancellationToken cancellationToken)
    {
        try
        {
            await DelayAsync(StrmProbeConstants.LibraryRefreshDebounce, cancellationToken).ConfigureAwait(false);

            if (!IsLibraryRefreshEnabled())
            {
                return;
            }

            if (itemId == Guid.Empty)
            {
                return;
            }

            var item = _libraryManager.GetItemById(itemId);
            if (item == null || !StrmFileHelper.IsStrmPath(item.Path))
            {
                return;
            }

            var entered = await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!entered)
            {
                _logger.LogDebug("strm 入库真探并发已满，跳过 item={Item}", item.Name);
                return;
            }

            try
            {
                // 与 core 在 PlaybackInfo 内对 strm 的刷新同构（仅远程探测，不重抓元数据语义由 Default 模式保证）：
                // 探测必经 IMediaEncoder 缓存装饰器，命中则零出网。
                var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    EnableRemoteContentProbe = true,
                };
                await RefreshItemAsync(item, options, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("strm 入库真探完成 item={Item}", item.Name);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "strm 入库真探失败 itemId={ItemId}", itemId);
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
