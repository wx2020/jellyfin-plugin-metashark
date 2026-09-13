using System.IO;
using System.Net.Http;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.StrmProbe;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
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
            serviceCollection.AddSingleton<IStrmProbeCacheStore>((ctx) =>
            {
                var appHost = ctx.GetRequiredService<IServerApplicationHost>();
                var dbPath = Path.Combine(appHost.ApplicationPaths.DataPath, "metashark", StrmProbeConstants.DbFileName);
                return new SqliteStrmProbeCacheStore(dbPath, ctx.GetRequiredService<ILogger<SqliteStrmProbeCacheStore>>());
            });
            serviceCollection.AddSingleton<IStrmProber>((ctx) =>
            {
                return new HttpStrmProber(
                    ctx.GetRequiredService<IHttpClientFactory>(),
                    ctx.GetRequiredService<ILogger<HttpStrmProber>>());
            });
            serviceCollection.AddSingleton<IMediaSourceProvider, StrmProbeMediaSourceProvider>();
            serviceCollection.AddHostedService<StrmProbeWarmupService>();
        }
    }
}
