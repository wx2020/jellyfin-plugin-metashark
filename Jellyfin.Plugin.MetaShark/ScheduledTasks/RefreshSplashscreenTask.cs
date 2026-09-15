using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaShark.Splashscreen;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.ScheduledTasks
{
    /// <summary>
    /// 刷新启动画面：单独重新生成 <c>&lt;DataPath&gt;/splashscreen.png</c>，无需跑完整媒体库扫描。
    /// 选料与筛选逻辑与插件对 core <c>SplashscreenPostScanTask</c> 的装饰保持一致
    /// （过滤开启时只用白名单库图片，关闭时取全库图片）。core 原有的"扫描媒体库后自动生成"逻辑保留不变。
    /// 默认无触发器（仅手动运行）；如需定时可在控制台的计划任务页自行添加触发器。
    /// </summary>
    public class RefreshSplashscreenTask : IScheduledTask
    {
        private readonly SplashscreenLibraryFilterProxy _proxy;
        private readonly ILogger<RefreshSplashscreenTask> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RefreshSplashscreenTask"/> class.
        /// </summary>
        /// <param name="proxy">启动画面生成器（IImageEncoder 装饰实例）。</param>
        /// <param name="logger">日志。</param>
        public RefreshSplashscreenTask(
            SplashscreenLibraryFilterProxy proxy,
            ILogger<RefreshSplashscreenTask> logger)
        {
            _proxy = proxy;
            _logger = logger;
        }

        /// <inheritdoc />
        public string Name => "刷新启动画面";

        /// <inheritdoc />
        public string Key => $"{Plugin.PluginName}RefreshSplashscreen";

        /// <inheritdoc />
        public string Description => "单独重新生成 Jellyfin 启动画面（登录页背景），无需跑完整媒体库扫描。过滤开启时只用白名单媒体库的图片；关闭时使用全库图片。默认仅手动运行。";

        /// <inheritdoc />
        public string Category => Plugin.PluginName;

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // 默认无触发器：仅手动运行，避免在用户不知情时反复重写启动图。
            return Array.Empty<TaskTriggerInfo>();
        }

        /// <inheritdoc />
        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            try
            {
                _proxy.Generate();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "刷新启动画面失败");
            }

            progress.Report(100);
            return Task.CompletedTask;
        }
    }
}
