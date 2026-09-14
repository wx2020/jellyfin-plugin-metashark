using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// 虚拟季孤儿集修复（扫描后兜底）：一次完整扫描结束后，把解析出季号、但仍未挂到任何季的 strm 剧集
/// 重刷一次以重绑到对应虚拟季。使用 <see cref="MetadataRefreshMode.None"/>，不跑任何在线 provider（零网络），
/// 仅触发 core <c>EpisodeMetadataService.BeforeSaveInternal</c> 重算 SeasonId。
/// 由 <c>EnableVirtualSeasonOrphanFix</c> 总开关控制，默认开启。
/// core 经反射发现 <see cref="ILibraryPostScanTask"/> 并用 ActivatorUtilities 构造，故依赖须为容器内具体服务。
/// </summary>
public sealed class StrmSeasonRelinkPostScanTask : ILibraryPostScanTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<StrmSeasonRelinkPostScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmSeasonRelinkPostScanTask"/> class.
    /// </summary>
    /// <param name="libraryManager">媒体库管理器。</param>
    /// <param name="providerManager">元数据提供者管理器（重刷单条目用）。</param>
    /// <param name="fileSystem">文件系统（构造按次目录服务用）。</param>
    /// <param name="logger">日志。</param>
    public StrmSeasonRelinkPostScanTask(
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem,
        ILogger<StrmSeasonRelinkPostScanTask> logger)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (providerManager is null)
        {
            throw new ArgumentNullException(nameof(providerManager));
        }

        // 仅重算季绑定：MetadataRefreshMode.None 仍会执行 core 的 BeforeSave/Save，
        // 但跳过所有 provider，不会触发豆瓣/TMDB 请求。
        RefreshItemAsync = (item, options, cancellationToken) => providerManager.RefreshSingleItem(item, options, cancellationToken);
    }

    /// <summary>
    /// 测试用配置覆盖（沿用 <c>StrmProbeWarmupService.TestConfigOverride</c> 模式，保证单测离线确定性）。
    /// </summary>
    internal bool? TestConfigOverride { get; set; }

    /// <summary>
    /// 单条目重刷实现，可注入以保证单测离线确定性。
    /// </summary>
    internal Func<BaseItem, MetadataRefreshOptions, CancellationToken, Task> RefreshItemAsync { get; set; }

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!IsEnabled())
        {
            return;
        }

        var orphans = ScanOrphans();
        if (orphans.Count == 0)
        {
            _logger.LogDebug("strm 虚拟季重绑：无需处理的孤儿集");
            progress?.Report(100);
            return;
        }

        _logger.LogInformation("strm 虚拟季重绑：发现 {Count} 个未挂季的 strm 剧集，开始重绑", orphans.Count);

        var done = 0;
        foreach (var episode in orphans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.None,
                };
                await RefreshItemAsync(episode, options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // fail-open：单个条目失败不影响其余重绑，也不影响扫描收尾。
                _logger.LogWarning(ex, "strm 虚拟季重绑失败 item={Item}", episode.Name);
            }

            done++;
            progress?.Report(done * 100.0 / orphans.Count);
        }

        _logger.LogInformation("strm 虚拟季重绑：完成 {Count} 个条目", done);
    }

    private bool IsEnabled()
    {
        if (TestConfigOverride.HasValue)
        {
            return TestConfigOverride.Value;
        }

        return Plugin.Instance?.Configuration?.EnableVirtualSeasonOrphanFix ?? true;
    }

    /// <summary>
    /// 扫描候选：非虚拟、非缺失的 strm 剧集，且已解析出季号但没有 SeasonId。
    /// </summary>
    /// <returns>待重绑剧集。</returns>
    internal List<Episode> ScanOrphans()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsVirtualItem = false,
            IsMissing = false,
            Recursive = true,
        };

        return _libraryManager.GetItemList(query)
            .OfType<Episode>()
            .Where(e => StrmFileHelper.IsStrmPath(e.Path))
            .Where(e => e.ParentIndexNumber.HasValue && e.SeasonId == Guid.Empty)
            .ToList();
    }
}
