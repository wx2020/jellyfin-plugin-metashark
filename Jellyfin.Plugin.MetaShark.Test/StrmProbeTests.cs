using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaShark.ScheduledTasks;
using Jellyfin.Plugin.MetaShark.StrmProbe;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class StrmProbeTests
    {
        // ---------- client policy ----------

        [TestMethod]
        [DataRow("Jellyfin Web")]
        [DataRow("Jellyfin Android")]
        [DataRow("Jellyfin iOS")]
        [DataRow("Jellyfin Media Player")]
        [DataRow("jellyfin-web")]
        public void NativeClients_AlwaysNative(string client)
        {
            Assert.IsTrue(StrmClientPolicy.IsNativeClient(client));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        public void UnknownClients_TreatedAsNative(string? client)
        {
            Assert.IsTrue(StrmClientPolicy.IsNativeClient(client));
        }

        [TestMethod]
        [DataRow("Yamby")]
        [DataRow("Infuse")]
        [DataRow("VidHub")]
        public void ThirdPartyClients_AreNotNative(string client)
        {
            Assert.IsFalse(StrmClientPolicy.IsNativeClient(client));
        }

        [TestMethod]
        public void Whitelist_Default_Parses_To_Yamby_Only()
        {
            var list = StrmClientPolicy.ParseWhitelist("Yamby");
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("Yamby", list[0]);
        }

        [TestMethod]
        public void Whitelist_Parsing_Supports_Comma_Semicolon_Newline_And_CaseInsensitive()
        {
            var list = StrmClientPolicy.ParseWhitelist(" Yamby, infuse;VIDHUB\nYamby\r\n");
            Assert.AreEqual(3, list.Count);
            Assert.IsTrue(StrmClientPolicy.IsWhitelistedThirdParty("yamby", list));
            Assert.IsTrue(StrmClientPolicy.IsWhitelistedThirdParty("INFUSE", list));
            Assert.IsFalse(StrmClientPolicy.IsWhitelistedThirdParty("Other", list));
        }

        [TestMethod]
        public void Whitelist_NativeClient_EvenIfListed_IsNotWhitelisted()
        {
            var list = StrmClientPolicy.ParseWhitelist("Jellyfin Web,Yamby");
            Assert.IsFalse(StrmClientPolicy.IsWhitelistedThirdParty("Jellyfin Web", list));
            Assert.IsTrue(StrmClientPolicy.IsWhitelistedThirdParty("Yamby", list));
        }

        [TestMethod]
        [DataRow("MediaBrowser Client=\"Yamby\", Device=\"TV\", Version=\"1.0\"", "Yamby")]
        [DataRow("MediaBrowser Client=\"Jellyfin Web\", Device=\"Chrome\"", "Jellyfin Web")]
        public void ExtractClientName_Parses_Authorization_Header(string header, string expected)
        {
            Assert.AreEqual(expected, StrmClientPolicy.ExtractClientName(header));
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("garbage-without-client")]
        public void ExtractClientName_Missing_Returns_Null(string? header)
        {
            Assert.IsNull(StrmClientPolicy.ExtractClientName(header));
        }

        // ---------- client resolver (AuthorizationInfo 优先，仅作 claims 之外的兼容回退) ----------

        [TestMethod]
        public void Resolver_Prefers_AuthorizationInfo_Over_Headers()
        {
            var ctx = new DefaultHttpContext();
            ctx.Items[StrmClientResolver.AuthorizationInfoItemsKey] = new AuthorizationInfo { Client = "Yamby" };
            ctx.Request.Headers["X-Emby-Authorization"] = "MediaBrowser Client=\"Other\", Device=\"x\"";

            Assert.AreEqual("Yamby", StrmClientResolver.Resolve(ctx.Request, ctx.Items));
        }

        [TestMethod]
        public void Resolver_Items_Without_Headers_Still_Resolves_Yamby()
        {
            var ctx = new DefaultHttpContext();
            ctx.Items[StrmClientResolver.AuthorizationInfoItemsKey] = new AuthorizationInfo { Client = "Yamby" };

            var client = StrmClientResolver.Resolve(ctx.Request, ctx.Items);
            Assert.AreEqual("Yamby", client);
            Assert.IsTrue(StrmClientPolicy.IsWhitelistedThirdParty(client, StrmClientPolicy.ParseWhitelist("Yamby")));
        }

        [TestMethod]
        public void Resolver_FallsBack_To_Authorization_Header()
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers["Authorization"] = "MediaBrowser Client=\"Infuse\", Device=\"x\"";

            Assert.AreEqual("Infuse", StrmClientResolver.Resolve(ctx.Request, ctx.Items));
        }

        [TestMethod]
        public void Resolver_FallsBack_To_XEmbyAuthorization_Header()
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers["X-Emby-Authorization"] = "Emby Client=\"Yamby\", Device=\"x\"";

            Assert.AreEqual("Yamby", StrmClientResolver.Resolve(ctx.Request, ctx.Items));
        }

        [TestMethod]
        public void Resolver_FallsBack_To_QueryString()
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.QueryString = QueryString.Create("X-Emby-Authorization", "MediaBrowser Client=\"Yamby\"");

            Assert.AreEqual("Yamby", StrmClientResolver.Resolve(ctx.Request, ctx.Items));
        }

        [TestMethod]
        public void Resolver_Ignores_Whitespace_Client_In_Items()
        {
            var ctx = new DefaultHttpContext();
            ctx.Items[StrmClientResolver.AuthorizationInfoItemsKey] = new AuthorizationInfo { Client = "   " };
            ctx.Request.Headers["X-Emby-Authorization"] = "MediaBrowser Client=\"Yamby\"";

            Assert.AreEqual("Yamby", StrmClientResolver.Resolve(ctx.Request, ctx.Items));
        }

        [TestMethod]
        public void Resolver_Returns_Null_When_Nothing_Resolvable()
        {
            var ctx = new DefaultHttpContext();
            Assert.IsNull(StrmClientResolver.Resolve(ctx.Request, ctx.Items));
            Assert.IsNull(StrmClientResolver.Resolve(null, null));
            Assert.IsTrue(StrmClientPolicy.IsNativeClient(StrmClientResolver.Resolve(null, null)));
        }

        // ---------- warmup true probe (入库真探) ----------

        private sealed class TestGenericLogger<T> : ILogger<T>
        {
            IDisposable ILogger.BeginScope<TState>(TState state) => throw new NotSupportedException();

            bool ILogger.IsEnabled(LogLevel logLevel) => false;

            void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
            }
        }

        private static MediaBrowser.Controller.Entities.Movies.Movie NewStrmMovie(Guid id, string path)
        {
            var movie = (MediaBrowser.Controller.Entities.Movies.Movie)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MediaBrowser.Controller.Entities.Movies.Movie));
            movie.Id = id;
            movie.Name = "probe-target";
            movie.Path = path;
            return movie;
        }

        private static StrmProbeWarmupService NewTrueProbeService(
            MediaBrowser.Controller.Library.ILibraryManager libraryManager,
            List<(TimeSpan Delay, bool Refreshed, bool ProbeEnabled)> calls,
            bool refreshThrow = false)
        {
            var service = new StrmProbeWarmupService(
                libraryManager,
                new Mock<MediaBrowser.Model.IO.IFileSystem>().Object,
                new TestGenericLogger<StrmProbeWarmupService>());
            service.TestConfigOverride = true;
            service.DelayAsync = (delay, ct) =>
            {
                lock (calls)
                {
                    calls.Add((delay, false, false));
                }

                return Task.CompletedTask;
            };
            service.RefreshItemAsync = (item, options, ct) =>
            {
                lock (calls)
                {
                    calls.Add((TimeSpan.Zero, true, options.EnableRemoteContentProbe));
                }

                if (refreshThrow)
                {
                    throw new InvalidOperationException("refresh boom");
                }

                return Task.CompletedTask;
            };
            return service;
        }

        [TestMethod]
        public async Task TrueProbe_Disabled_Skips_Refresh()
        {
            var id = Guid.NewGuid();
            var movie = NewStrmMovie(id, "/strm/a.strm");
            var lib = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
            lib.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns(movie);
            var calls = new List<(TimeSpan Delay, bool Refreshed, bool ProbeEnabled)>();
            var service = NewTrueProbeService(lib.Object, calls);
            service.TestConfigOverride = false;

            await service.DelayedTrueProbeAsync(id, CancellationToken.None);

            Assert.AreEqual(0, calls.FindAll(c => c.Refreshed).Count);
        }

        [TestMethod]
        public async Task TrueProbe_Enabled_Waits_Debounce_Then_Refreshes_With_Probe()
        {
            var id = Guid.NewGuid();
            var movie = NewStrmMovie(id, "/strm/b.strm");
            var lib = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
            lib.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns(movie);
            var calls = new List<(TimeSpan Delay, bool Refreshed, bool ProbeEnabled)>();
            var service = NewTrueProbeService(lib.Object, calls);

            await service.DelayedTrueProbeAsync(id, CancellationToken.None);

            Assert.AreEqual(2, calls.Count);
            Assert.AreEqual(StrmProbeConstants.LibraryRefreshDebounce, calls[0].Delay);
            Assert.IsFalse(calls[0].Refreshed);
            Assert.IsTrue(calls[1].Refreshed);
            Assert.IsTrue(calls[1].ProbeEnabled);
        }

        [TestMethod]
        public async Task TrueProbe_ItemGone_Or_NotStrm_Skips_Refresh()
        {
            var lib = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
            lib.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((MediaBrowser.Controller.Entities.BaseItem?)null);
            var calls = new List<(TimeSpan Delay, bool Refreshed, bool ProbeEnabled)>();
            var service = NewTrueProbeService(lib.Object, calls);

            await service.DelayedTrueProbeAsync(Guid.NewGuid(), CancellationToken.None);

            var id2 = Guid.NewGuid();
            var lib2 = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
            lib2.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns(NewStrmMovie(id2, "/media/plain.mkv"));
            var service2 = NewTrueProbeService(lib2.Object, calls);

            await service2.DelayedTrueProbeAsync(id2, CancellationToken.None);

            Assert.AreEqual(0, calls.FindAll(c => c.Refreshed).Count);
        }

        [TestMethod]
        public void OnItemAdded_NonStrm_Does_Not_Schedule_DelayedProbe()
        {
            var strmMovie = NewStrmMovie(Guid.NewGuid(), "/strm/g.strm");
            var plainMovie = NewStrmMovie(Guid.NewGuid(), "/media/plain2.mkv");
            var lib = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
            lib.Setup(l => l.GetItemById(strmMovie.Id)).Returns(strmMovie);
            lib.Setup(l => l.GetItemById(plainMovie.Id)).Returns(plainMovie);

            var calls = new List<(TimeSpan Delay, bool Refreshed, bool ProbeEnabled)>();

            // 非 strm：不应排任何延迟任务
            var nonStrmService = NewTrueProbeService(lib.Object, calls);
            nonStrmService.OnItemAdded(null, new MediaBrowser.Controller.Library.ItemChangeEventArgs { Item = plainMovie });
            Assert.AreEqual(0, calls.Count);

            // strm：排延迟任务（测试里 DelayAsync 同步完成，可即时断言）
            var strmService = NewTrueProbeService(lib.Object, calls);
            strmService.OnItemAdded(null, new MediaBrowser.Controller.Library.ItemChangeEventArgs { Item = strmMovie });
            Assert.IsTrue(calls.Count >= 1);
        }

        // ---------- daily task (每日定时探测) ----------
        [TestMethod]
        [DataRow("03:00", 3, 0)]
        [DataRow("23:59", 23, 59)]
        [DataRow("00:00", 0, 0)]
        [DataRow("3:00", 3, 0)]
        [DataRow("9:05", 9, 5)]
        public void DailyTrigger_ValidTime_Parses(string input, int h, int m)
        {
            var trigger = StrmMediaProbeDailyTask.BuildDailyTrigger(input);
            Assert.IsNotNull(trigger);
            Assert.AreEqual(TaskTriggerInfoType.DailyTrigger, trigger.Type);
            Assert.AreEqual(new TimeSpan(h, m, 0).Ticks, trigger.TimeOfDayTicks);
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow("24:00")]
        [DataRow("ab:cd")]
        public void DailyTrigger_InvalidOrEmpty_ReturnsNull(string? input)
        {
            Assert.IsNull(StrmMediaProbeDailyTask.BuildDailyTrigger(input));
        }

        [TestMethod]
        public void NeedsVideoProbe_MissingOrNoVideo_ReturnsTrue()
        {
            Assert.IsTrue(StrmMediaProbeDailyTask.NeedsVideoProbe(null));
            Assert.IsTrue(StrmMediaProbeDailyTask.NeedsVideoProbe(new MediaStream[0]));
            Assert.IsTrue(StrmMediaProbeDailyTask.NeedsVideoProbe(new[]
            {
                new MediaStream { Type = MediaStreamType.Audio },
                new MediaStream { Type = MediaStreamType.Subtitle },
            }));
        }

        [TestMethod]
        public void NeedsVideoProbe_WithVideo_ReturnsFalse()
        {
            Assert.IsFalse(StrmMediaProbeDailyTask.NeedsVideoProbe(new[]
            {
                new MediaStream { Type = MediaStreamType.Video },
                new MediaStream { Type = MediaStreamType.Audio },
            }));
        }

        private static StrmMediaProbeDailyTask NewDailyTask(
            MediaBrowser.Controller.Library.ILibraryManager lib,
            Mock<MediaBrowser.Controller.Library.IMediaSourceManager> msm,
            StrmProbeWarmupService warmup,
            IMediaInfoProbeCacheStore? store = null)
        {
            return new StrmMediaProbeDailyTask(
                lib,
                msm.Object,
                warmup,
                store ?? new Mock<IMediaInfoProbeCacheStore>().Object,
                new TestGenericLogger<StrmMediaProbeDailyTask>());
        }

        [TestMethod]
        public async Task DailyTask_ProbeDisabled_Skips_All()
        {
            var id = Guid.NewGuid();
            var movie = NewStrmMovie(id, "/strm/d.strm");
            var lib = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
            lib.Setup(l => l.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
                .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { movie });
            var msm = new Mock<MediaBrowser.Controller.Library.IMediaSourceManager>();
            msm.Setup(m => m.GetMediaStreams(It.IsAny<Guid>())).Returns(new MediaStream[0]);

            var calls = new List<(TimeSpan Delay, bool Refreshed, bool ProbeEnabled)>();
            var warmup = NewTrueProbeService(lib.Object, calls);
            warmup.TestConfigOverride = false;
            var task = NewDailyTask(lib.Object, msm, warmup);
            task.TestConfigOverride = false;

            var progress = new Progress<double>();
            await task.ExecuteAsync(progress, CancellationToken.None);

            Assert.AreEqual(0, calls.FindAll(c => c.Refreshed).Count);
        }

        [TestMethod]
        public async Task DailyTask_Enabled_Refreshes_Only_Strm_Without_Video()
        {
            var strmNoVideo = NewStrmMovie(Guid.NewGuid(), "/strm/e.strm");
            var strmWithVideo = NewStrmMovie(Guid.NewGuid(), "/strm/f.strm");
            var plainFile = NewStrmMovie(Guid.NewGuid(), "/media/plain.mkv");
            var lib = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
            lib.Setup(l => l.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
                .Returns(new List<MediaBrowser.Controller.Entities.BaseItem> { strmNoVideo, strmWithVideo, plainFile });
            lib.Setup(l => l.GetItemById(strmNoVideo.Id)).Returns(strmNoVideo);
            var msm = new Mock<MediaBrowser.Controller.Library.IMediaSourceManager>();
            msm.Setup(m => m.GetMediaStreams(strmNoVideo.Id)).Returns(new MediaStream[0]);
            msm.Setup(m => m.GetMediaStreams(strmWithVideo.Id)).Returns(new[] { new MediaStream { Type = MediaStreamType.Video } });
            msm.Setup(m => m.GetMediaStreams(plainFile.Id)).Returns(new MediaStream[0]);

            var calls = new List<(TimeSpan Delay, bool Refreshed, bool ProbeEnabled)>();
            var warmup = NewTrueProbeService(lib.Object, calls);
            var task = NewDailyTask(lib.Object, msm, warmup);
            task.TestConfigOverride = true;

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            Assert.AreEqual(1, calls.FindAll(c => c.Refreshed).Count);
        }

        [TestMethod]
        public async Task DailyTask_Enabled_Cleans_Expired_ProbeCache()
        {
            var lib = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
            lib.Setup(l => l.GetItemList(It.IsAny<MediaBrowser.Controller.Entities.InternalItemsQuery>()))
                .Returns(new List<MediaBrowser.Controller.Entities.BaseItem>());
            var msm = new Mock<MediaBrowser.Controller.Library.IMediaSourceManager>();
            var calls = new List<(TimeSpan Delay, bool Refreshed, bool ProbeEnabled)>();
            var warmup = NewTrueProbeService(lib.Object, calls);
            var store = new Mock<IMediaInfoProbeCacheStore>();
            var task = NewDailyTask(lib.Object, msm, warmup, store.Object);
            task.TestConfigOverride = true;

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

            store.Verify(s => s.RemoveExpired(It.IsAny<DateTime>()), Times.Once);
        }

        [TestMethod]
        public async Task TrueProbe_RefreshThrows_Does_Not_Throw()
        {
            var id = Guid.NewGuid();
            var lib = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
            lib.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns(NewStrmMovie(id, "/strm/c.strm"));
            var calls = new List<(TimeSpan Delay, bool Refreshed, bool ProbeEnabled)>();
            var service = NewTrueProbeService(lib.Object, calls, refreshThrow: true);

            await service.DelayedTrueProbeAsync(id, CancellationToken.None);

            Assert.AreEqual(1, calls.FindAll(c => c.Refreshed).Count);
        }

        // ---------- 虚拟季孤儿集重绑（扫描后兜底） ----------

        private static Episode NewStrmEpisode(Guid id, string path, int? parentIndex, Guid seasonId)
        {
            var episode = (Episode)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Episode));
            episode.Id = id;
            episode.Name = "relink-target";
            episode.Path = path;
            episode.ParentIndexNumber = parentIndex;
            episode.SeasonId = seasonId;
            return episode;
        }

        private static StrmSeasonRelinkPostScanTask NewRelinkTask(
            MediaBrowser.Controller.Library.ILibraryManager libraryManager,
            List<(bool Refreshed, MetadataRefreshMode Mode)> calls,
            bool refreshThrow = false)
        {
            var task = new StrmSeasonRelinkPostScanTask(
                libraryManager,
                new Mock<IProviderManager>().Object,
                new Mock<MediaBrowser.Model.IO.IFileSystem>().Object,
                new TestGenericLogger<StrmSeasonRelinkPostScanTask>());
            task.RefreshItemAsync = (item, options, ct) =>
            {
                lock (calls)
                {
                    calls.Add((true, options.MetadataRefreshMode));
                }

                if (refreshThrow)
                {
                    throw new InvalidOperationException("relink boom");
                }

                return Task.CompletedTask;
            };
            return task;
        }

        [TestMethod]
        public void SeasonRelink_ScanOrphans_Filters()
        {
            var candidate = NewStrmEpisode(Guid.NewGuid(), "/strm/a.strm", 1, Guid.Empty);
            var bound = NewStrmEpisode(Guid.NewGuid(), "/strm/b.strm", 1, Guid.NewGuid());
            var nonStrm = NewStrmEpisode(Guid.NewGuid(), "/media/c.mkv", 1, Guid.Empty);
            var noSeason = NewStrmEpisode(Guid.NewGuid(), "/strm/d.strm", null, Guid.Empty);

            var lib = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
            lib.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { candidate, bound, nonStrm, noSeason });

            var task = NewRelinkTask(lib.Object, new List<(bool, MetadataRefreshMode)>());

            var orphans = task.ScanOrphans();

            Assert.AreEqual(1, orphans.Count);
            Assert.AreEqual(candidate.Id, orphans[0].Id);
        }

        [TestMethod]
        public async Task SeasonRelink_Disabled_Skips()
        {
            var candidate = NewStrmEpisode(Guid.NewGuid(), "/strm/a.strm", 1, Guid.Empty);
            var lib = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
            lib.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { candidate });

            var calls = new List<(bool Refreshed, MetadataRefreshMode Mode)>();
            var task = NewRelinkTask(lib.Object, calls);
            task.TestConfigOverride = false;

            await task.Run(new Progress<double>(), CancellationToken.None);

            Assert.AreEqual(0, calls.Count);
        }

        [TestMethod]
        public async Task SeasonRelink_Refreshes_Orphans_With_None_Mode()
        {
            var candidate = NewStrmEpisode(Guid.NewGuid(), "/strm/a.strm", 1, Guid.Empty);
            var lib = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
            lib.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { candidate });

            var calls = new List<(bool Refreshed, MetadataRefreshMode Mode)>();
            var task = NewRelinkTask(lib.Object, calls);
            task.TestConfigOverride = true;

            await task.Run(new Progress<double>(), CancellationToken.None);

            Assert.AreEqual(1, calls.Count);
            Assert.AreEqual(MetadataRefreshMode.None, calls[0].Mode);
        }

        [TestMethod]
        public async Task SeasonRelink_RefreshThrows_DoesNotThrow()
        {
            var candidate = NewStrmEpisode(Guid.NewGuid(), "/strm/a.strm", 1, Guid.Empty);
            var lib = new Mock<MediaBrowser.Controller.Library.ILibraryManager>();
            lib.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { candidate });

            var calls = new List<(bool Refreshed, MetadataRefreshMode Mode)>();
            var task = NewRelinkTask(lib.Object, calls, refreshThrow: true);
            task.TestConfigOverride = true;

            await task.Run(new Progress<double>(), CancellationToken.None);

            Assert.AreEqual(1, calls.Count);
        }

        // ---------- DI 注册回归（core 反射发现计划任务并经 ActivatorUtilities 构造） ----------

        [TestMethod]
        public void DailyTask_Resolvable_From_Production_ServiceRegistrations()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            new ServiceRegistrator().RegisterServices(
                services,
                new Mock<MediaBrowser.Controller.IServerApplicationHost>().Object);

            var appPaths = new Mock<IApplicationPaths>();
            appPaths.SetupGet(p => p.DataPath).Returns(Path.Combine(Path.GetTempPath(), "metashark-di-test"));
            services.AddSingleton(appPaths.Object);
            services.AddSingleton(new Mock<MediaBrowser.Controller.Library.ILibraryManager>().Object);
            services.AddSingleton(new Mock<MediaBrowser.Controller.Library.IMediaSourceManager>().Object);
            services.AddSingleton(new Mock<ICollectionManager>().Object);
            services.AddSingleton(new Mock<MediaBrowser.Model.IO.IFileSystem>().Object);
            services.AddSingleton(new Mock<IProviderManager>().Object);

            using var provider = services.BuildServiceProvider();

            // 生产注册必须能构造该任务：core 反射发现后经 ActivatorUtilities 构造，
            // 解析失败会丢弃任务并把整个插件标记 Malfunctioned。
            var task = ActivatorUtilities.CreateInstance<StrmMediaProbeDailyTask>(provider);
            Assert.IsNotNull(task);

            // 扫描后重绑任务同样由 core 反射发现，必须可解析。
            var relink = ActivatorUtilities.CreateInstance<StrmSeasonRelinkPostScanTask>(provider);
            Assert.IsNotNull(relink);

            // hosted service 与任务注入的 warmup 必须是同一实例（共享并发闸门与 ItemAdded 订阅）。
            var warmup = provider.GetRequiredService<StrmProbeWarmupService>();
            var hosted = provider.GetServices<IHostedService>().OfType<StrmProbeWarmupService>().Single();
            Assert.AreSame(warmup, hosted);
        }
    }
}
