using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// 入库真探服务：订阅 ItemAdded，对新增 strm 条目去抖后做一次带远程探测的完整刷新，
/// 把流信息写入媒体库并经 ffprobe 缓存装饰器预填缓存。不阻塞扫描主流程，失败只记日志。
/// （原"预热写探针缓存"随虚拟直连源下线一并移除：该缓存已无读者。）
/// </summary>
public sealed class StrmProbeWarmupService : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<StrmProbeWarmupService> _logger;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(StrmProbeConstants.MaxConcurrentWarmups, StrmProbeConstants.MaxConcurrentWarmups);
    private bool _disposed;

    /// <summary>
    /// 测试用配置覆盖（沿用 <c>MoviePilotApi.TestConfigOverride</c> 模式，保证单测离线确定性）。
    /// </summary>
    internal bool? TestConfigOverride { get; set; }

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
    /// <param name="logger">日志。</param>
    public StrmProbeWarmupService(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        ILogger<StrmProbeWarmupService> logger)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
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
        if (config == null || !config.EnableStrmProbeLibraryRefresh)
        {
            return;
        }

        // 入库真探：去抖后在后台做一次完整远程探测，把流信息写入媒体库，
        // 探测必经 ffprobe 缓存装饰器，顺带预填缓存，使首次详情页也不再真实出网。
        if (e.Item != null)
        {
            _ = DelayedTrueProbeAsync(e.Item.Id, CancellationToken.None);
        }
    }

    private bool IsLibraryRefreshEnabled()
    {
        if (TestConfigOverride.HasValue)
        {
            return TestConfigOverride.Value;
        }

        var config = Plugin.Instance?.Configuration;
        return config != null && config.EnableStrmProbeLibraryRefresh;
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
}
