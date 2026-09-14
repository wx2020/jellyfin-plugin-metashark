using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class PlaybackMetadataShortCircuitTests
    {
        private static readonly ILoggerFactory LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder => { });

        private static MovieInfo ScrapedDouban(string? path = null)
        {
            var info = new MovieInfo
            {
                Name = "已刮削电影",
                MetadataLanguage = "zh",
                ProviderIds = new Dictionary<string, string>
                {
                    { BaseProvider.DoubanProviderId, "1234567" },
                    { Plugin.ProviderId, $"{MetaSource.Douban}_1234567" },
                },
            };
            if (path != null)
            {
                info.Path = path;
            }

            return info;
        }

        private static MovieInfo ScrapedTmdb()
        {
            return new MovieInfo
            {
                Name = "已刮削电影",
                ProviderIds = new Dictionary<string, string>
                {
                    { MetadataProvider.Tmdb.ToString(), "945664" },
                    { Plugin.ProviderId, $"{MetaSource.Tmdb}_945664" },
                },
            };
        }

        private static MovieInfo Unscraped()
        {
            return new MovieInfo { Name = "新电影" };
        }

        private static EpisodeInfo EpisodeWithProvenance(string path)
        {
            return new EpisodeInfo
            {
                Name = "已刮削剧集",
                Path = path,
                IndexNumber = 1,
                ParentIndexNumber = 1,
                ProviderIds = new Dictionary<string, string> { { Plugin.ProviderId, $"{MetaSource.Tmdb}_123" } },
            };
        }

        private static EpisodeInfo EpisodeWithoutProvenance(string path)
        {
            return new EpisodeInfo
            {
                Name = "存量剧集",
                Path = path,
                IndexNumber = 2,
                ParentIndexNumber = 1,
            };
        }

        private static IHttpContextAccessor PlaybackInfoAccessor()
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Path = "/Items/00000000-0000-0000-0000-000000000001/PlaybackInfo";
            var accessor = new Mock<IHttpContextAccessor>();
            accessor.Setup(a => a.HttpContext).Returns(ctx);
            return accessor.Object;
        }

        private static Episode StoredEpisodeWithMetadata()
        {
            var episode = (Episode)RuntimeHelpers.GetUninitializedObject(typeof(Episode));
            episode.Overview = "库内已有简介";
            return episode;
        }

        private static MovieProvider NewMovieProvider(IHttpContextAccessor accessor, ILibraryManager libraryManager, bool enabled)
        {
            var provider = new MovieProvider(
                new DefaultHttpClientFactory(),
                LoggerFactory,
                libraryManager,
                accessor,
                new DoubanApi(LoggerFactory),
                new TmdbApi(LoggerFactory),
                new OmdbApi(LoggerFactory),
                new ImdbApi(LoggerFactory));
            provider.TestConfigOverride = enabled;
            return provider;
        }

        private static EpisodeProvider NewEpisodeProvider(IHttpContextAccessor accessor, ILibraryManager libraryManager, bool enabled)
        {
            var provider = new EpisodeProvider(
                new DefaultHttpClientFactory(),
                LoggerFactory,
                libraryManager,
                accessor,
                new DoubanApi(LoggerFactory),
                new TmdbApi(LoggerFactory),
                new OmdbApi(LoggerFactory),
                new ImdbApi(LoggerFactory));
            provider.TestConfigOverride = enabled;
            return provider;
        }

        [TestMethod]
        public void IsScrapedByMetashark_Douban_ReturnsTrue()
        {
            Assert.IsTrue(BaseProvider.IsScrapedByMetashark(ScrapedDouban()));
        }

        [TestMethod]
        public void IsScrapedByMetashark_Tmdb_ReturnsTrue()
        {
            Assert.IsTrue(BaseProvider.IsScrapedByMetashark(ScrapedTmdb()));
        }

        [TestMethod]
        public void IsScrapedByMetashark_Unscraped_ReturnsFalse()
        {
            Assert.IsFalse(BaseProvider.IsScrapedByMetashark(Unscraped()));
        }

        [TestMethod]
        public void IsScrapedByMetashark_Null_ReturnsFalse()
        {
            Assert.IsFalse(BaseProvider.IsScrapedByMetashark(null!));
        }

        [TestMethod]
        public void HasMetasharkProvenance_WithMarker_ReturnsTrue()
        {
            Assert.IsTrue(BaseProvider.HasMetasharkProvenance(EpisodeWithProvenance("/strm/e.strm")));
        }

        [TestMethod]
        public void HasMetasharkProvenance_WithoutMarker_ReturnsFalse()
        {
            Assert.IsFalse(BaseProvider.HasMetasharkProvenance(EpisodeWithoutProvenance("/strm/e.strm")));
        }

        [TestMethod]
        public void GetScrapeState_Douban_ExposesDerivedFlags()
        {
            var state = BaseProvider.GetScrapeState(ScrapedDouban());
            Assert.IsTrue(state.HasDoubanMeta);
            Assert.IsFalse(state.HasTmdbMeta);
            Assert.IsTrue(state.IsScraped);
            Assert.AreEqual("1234567", state.Sid);
        }

        [TestMethod]
        [DataRow(false, true, true, true, false)]
        [DataRow(true, false, true, true, false)]
        [DataRow(true, true, false, true, false)]
        [DataRow(true, true, true, false, false)]
        [DataRow(true, true, true, true, true)]
        public void ShouldSkipOnlineMetadata_TruthTable(bool enabled, bool isPlaybackInfo, bool strm, bool scraped, bool expected)
        {
            var info = ScrapedDouban(strm ? "/strm/x.strm" : "/media/x.mkv");
            Assert.AreEqual(expected, BaseProvider.ShouldSkipOnlineMetadata(enabled, isPlaybackInfo, info, scraped));
        }

        [TestMethod]
        public async Task ScrapedMovie_OnStrmPlaybackInfo_WithSwitchOn_ReturnsEmptyWithoutNetwork()
        {
            var provider = NewMovieProvider(PlaybackInfoAccessor(), new Mock<ILibraryManager>().Object, enabled: true);

            var result = await provider.GetMetadata(ScrapedDouban("/strm/x.strm"), CancellationToken.None);

            Assert.IsFalse(result.HasMetadata, "短路应返回空结果，core 保留库内数据");
            Assert.IsNull(result.Item, "短路不应返回任何新元数据");
        }

        [TestMethod]
        public void ScrapedMovie_OnNonStrmPlaybackInfo_IsNotShortCircuited()
        {
            var info = ScrapedDouban("/media/x.mkv");

            // 非 strm 不短路（避免越界作用于缺流非 strm 条目）
            Assert.IsFalse(BaseProvider.ShouldSkipOnlineMetadata(true, true, info, BaseProvider.IsScrapedByMetashark(info)));
        }

        [TestMethod]
        public async Task Episode_WithProvenance_OnStrmPlaybackInfo_WithSwitchOn_ReturnsEmptyWithoutNetwork()
        {
            var provider = NewEpisodeProvider(PlaybackInfoAccessor(), new Mock<ILibraryManager>().Object, enabled: true);

            var result = await provider.GetMetadata(EpisodeWithProvenance("/strm/e.strm"), CancellationToken.None);

            Assert.IsFalse(result.HasMetadata);
            Assert.IsNull(result.Item);
        }

        [TestMethod]
        public async Task Episode_LegacyStoredMetadata_OnStrmPlaybackInfo_WithSwitchOn_ReturnsEmpty()
        {
            var path = "/strm/legacy.strm";
            var lib = new Mock<ILibraryManager>();
            lib.Setup(l => l.FindByPath(path, false)).Returns(StoredEpisodeWithMetadata());
            var provider = NewEpisodeProvider(PlaybackInfoAccessor(), lib.Object, enabled: true);

            var result = await provider.GetMetadata(EpisodeWithoutProvenance(path), CancellationToken.None);

            Assert.IsFalse(result.HasMetadata);
            Assert.IsNull(result.Item);
            lib.Verify(l => l.FindByPath(path, false), Times.Once);
        }

        [TestMethod]
        [DataRow(false, true, true)]   // 开关关
        [DataRow(true, true, false)]   // 非 strm
        public void Episode_NotShortCircuited_WhenGateFails(bool enabled, bool isPlaybackInfo, bool strm)
        {
            var path = strm ? "/strm/e.strm" : "/media/e.mkv";
            var info = EpisodeWithProvenance(path);

            // 门控判定（避免未短路时真实出网）
            Assert.IsFalse(BaseProvider.ShouldSkipOnlineMetadata(enabled, isPlaybackInfo, info, BaseProvider.HasMetasharkProvenance(info)));
        }
    }
}
