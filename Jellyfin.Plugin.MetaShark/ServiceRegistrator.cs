using System.IO;
using System.Net.Http;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.StrmProbe;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark
{
    /// <inheritdoc />
    public class ServiceRegistrator : IPluginServiceRegistrator
    {
        /// <inheritdoc />
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddHttpContextAccessor();
            serviceCollection.AddHostedService<BoxSetManager>();
            serviceCollection.AddSingleton((ctx) =>
            {
                return new DoubanApi(ctx.GetRequiredService<ILoggerFactory>());
            });
            serviceCollection.AddSingleton((ctx) =>
            {
                return new TmdbApi(ctx.GetRequiredService<ILoggerFactory>());
            });
            serviceCollection.AddSingleton((ctx) =>
            {
                return new OmdbApi(ctx.GetRequiredService<ILoggerFactory>());
            });
            serviceCollection.AddSingleton((ctx) =>
            {
                return new ImdbApi(ctx.GetRequiredService<ILoggerFactory>());
            });
            serviceCollection.AddSingleton((ctx) =>
            {
                return new MoviePilotApi(ctx.GetRequiredService<ILoggerFactory>());
            });
            serviceCollection.AddSingleton<IStrmProbeCacheStore>((ctx) =>
            {
                var appPaths = ctx.GetRequiredService<IApplicationPaths>();
                var dbPath = Path.Combine(appPaths.DataPath, "metashark", StrmProbeConstants.DbFileName);
                return new SqliteStrmProbeCacheStore(dbPath, ctx.GetRequiredService<ILogger<SqliteStrmProbeCacheStore>>());
            });
            serviceCollection.AddSingleton<IStrmProber>((ctx) =>
            {
                return new HttpStrmProber(
                    ctx.GetRequiredService<IHttpClientFactory>(),
                    ctx.GetRequiredService<ILogger<HttpStrmProber>>());
            });
            serviceCollection.AddSingleton<IMediaSourceProvider, StrmProbeMediaSourceProvider>();
            // 取流 302 直跳：全局 ActionFilter（鉴权之后执行），只劫白名单客户端的纯静态取流，其余全部放行。
            serviceCollection.AddTransient<StrmDirectRedirectFilter>();
            serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<StrmDirectRedirectFilter>());
            serviceCollection.AddSingleton<IMediaInfoProbeCacheStore>((ctx) =>
            {
                var appPaths = ctx.GetRequiredService<IApplicationPaths>();
                var dbPath = Path.Combine(appPaths.DataPath, "metashark", StrmProbeConstants.DbFileName);
                return new SqliteMediaInfoProbeCacheStore(dbPath, ctx.GetRequiredService<ILogger<SqliteMediaInfoProbeCacheStore>>());
            });

            // B 方案：装饰 core 的 IMediaEncoder，只缓存 http/https 远程探测的 ffprobe 结果，
            // 使 strm 条目重复的远程内容探测不再真实出网。本地文件探测与其它方法全部直通。
            CachingMediaEncoderProxy.Decorate(serviceCollection);
            serviceCollection.AddHostedService<StrmProbeWarmupService>();
        }
    }
}
