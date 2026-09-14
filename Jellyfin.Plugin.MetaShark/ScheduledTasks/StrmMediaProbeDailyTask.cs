using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.StrmProbe;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.ScheduledTasks
{
    /// <summary>
    /// 每日定时任务（参考 JellySTRMprobe 思路）：扫描库中没有媒体流信息的 strm 条目，
    /// 逐个后台真探补全（复用入库真探的并发闸门与注入点，探测经 ffprobe 缓存装饰器）。
    /// 执行受"入库媒体探测"开关门控；每日执行时间由 <c>StrmProbeDailyScanTime</c> 配置（留空只能手动运行）。
    /// </summary>
    public class StrmMediaProbeDailyTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly StrmProbeWarmupService _warmup;
        private readonly IMediaInfoProbeCacheStore _probeCacheStore;
        private readonly ILogger<StrmMediaProbeDailyTask> _logger;

        /// <summary>
        /// 测试用配置覆盖（沿用 <c>StrmProbeWarmupService.TestConfigOverride</c> 模式，保证单测离线确定性）。
        /// </summary>
        internal bool? TestConfigOverride { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="StrmMediaProbeDailyTask"/> class.
        /// </summary>
        public StrmMediaProbeDailyTask(
            ILibraryManager libraryManager,
            IMediaSourceManager mediaSourceManager,
            StrmProbeWarmupService warmup,
            IMediaInfoProbeCacheStore probeCacheStore,
            ILogger<StrmMediaProbeDailyTask> logger)
        {
            _libraryManager = libraryManager;
            _mediaSourceManager = mediaSourceManager;
            _warmup = warmup;
            _probeCacheStore = probeCacheStore;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "入库媒体探测（每日扫描缺失流信息）";

        /// <inheritdoc />
        public string Key => $"{Plugin.PluginName}StrmMediaProbeDaily";

        /// <inheritdoc />
        public string Description => "扫描库中没有媒体流信息的 strm 条目并逐个后台探测补全，使详情页/播放决策有完整流信息。需开启插件设置中的\"入库媒体探测\"开关；每日执行时间在插件设置中配置（留空则只能手动运行），修改后需重启生效。";

        /// <inheritdoc />
        public string Category => Plugin.PluginName;

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var trigger = BuildDailyTrigger(Plugin.Instance?.Configuration?.StrmProbeDailyScanTime);
            return trigger == null ? Enumerable.Empty<TaskTriggerInfo>() : new[] { trigger };
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var enabled = TestConfigOverride ?? (Plugin.Instance?.Configuration?.EnableStrmProbeLibraryRefresh ?? false);
            if (!enabled)
            {
                _logger.LogInformation("strm 每日探测：入库媒体探测开关未开启，跳过");
                progress.Report(100);
                return;
            }

            CleanupExpiredProbeCache();
            var candidates = ScanCandidates();
            var total = candidates.Count;
            if (total == 0)
            {
                _logger.LogInformation("strm 每日探测：没有缺媒体流信息的 strm 条目");
                progress.Report(100);
                return;
            }

            _logger.LogInformation("strm 每日探测：发现 {Count} 个缺媒体流信息的 strm 条目", total);
            var done = 0;
            foreach (var item in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await _warmup.ProbeNowAsync(item.Id, cancellationToken).ConfigureAwait(false);
                done++;
                progress.Report(done * 100.0 / total);

                if (done < total)
                {
                    // 温和限速，避免短时间打满网盘
                    await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                }
            }

            _logger.LogInformation("strm 每日探测：完成 {Count} 个条目", done);
        }

        /// <summary>
        /// 解析 "HH:mm" 为每日触发器；空/非法返回 null（不自动执行）。
        /// </summary>
        internal static TaskTriggerInfo? BuildDailyTrigger(string? hhmm)
        {
            if (string.IsNullOrWhiteSpace(hhmm))
            {
                return null;
            }

            // 同时接受 "HH:mm" 与 "H:mm"（如 "3:00"），避免用户漏写前导零导致任务静默不注册。
            if (!TimeOnly.TryParseExact(
                    hhmm.Trim(),
                    new[] { "HH:mm", "H:mm" },
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var time))
            {
                return null;
            }

            return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = time.ToTimeSpan().Ticks,
            };
        }

        /// <summary>
        /// 判定流信息是否缺失：无任何流，或没有视频流。
        /// </summary>
        internal static bool NeedsVideoProbe(IReadOnlyList<MediaStream>? streams)
        {
            return streams == null || streams.Count == 0 || !streams.Any(s => s.Type == MediaStreamType.Video);
        }

        private void CleanupExpiredProbeCache()
        {
            try
            {
                var removed = _probeCacheStore.RemoveExpired(DateTime.UtcNow);
                if (removed > 0)
                {
                    _logger.LogInformation("strm 每日探测：清理过期 ffprobe 缓存 {Count} 条", removed);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "strm 每日探测：清理过期 ffprobe 缓存失败");
            }
        }

        private List<BaseItem> ScanCandidates()
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                IsVirtualItem = false,
                IsMissing = false,
                Recursive = true,
            };

            var items = _libraryManager.GetItemList(query);
            return items
                .Where(item => StrmFileHelper.IsStrmPath(item.Path))
                .Where(item => NeedsVideoProbe(_mediaSourceManager.GetMediaStreams(item.Id)))
                .ToList();
        }
    }
}
