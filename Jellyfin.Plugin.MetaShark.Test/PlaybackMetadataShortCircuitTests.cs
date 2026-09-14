using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
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

        private static MovieInfo ScrapedDouban()
        {
            return new MovieInfo
            {
                Name = "已刮削电影",
                MetadataLanguage = "zh",
                ProviderIds = new Dictionary<string, string>
                {
                    { BaseProvider.DoubanProviderId, "1234567" },
                    { Plugin.ProviderId, $"{MetaSource.Douban}_1234567" },
                },
            };
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
        [DataRow(false, true, true, false)]
        [DataRow(true, false, true, false)]
        [DataRow(true, true, false, false)]
        [DataRow(true, true, true, true)]
        public void ShouldSkipOnlineMetadata_TruthTable(bool enabled, bool isPlaybackInfo, bool scraped, bool expected)
        {
            var info = scraped ? ScrapedDouban() : Unscraped();
            Assert.AreEqual(expected, BaseProvider.ShouldSkipOnlineMetadata(enabled, isPlaybackInfo, info));
        }

        [TestMethod]
        public async Task ScrapedMovie_OnPlaybackInfo_WithSwitchOn_ReturnsEmptyWithoutNetwork()
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Path = "/Items/00000000-0000-0000-0000-000000000001/PlaybackInfo";
            var accessor = new Mock<IHttpContextAccessor>();
            accessor.Setup(a => a.HttpContext).Returns(ctx);

            var provider = new MovieProvider(
                new DefaultHttpClientFactory(),
                LoggerFactory,
                new Mock<ILibraryManager>().Object,
                accessor.Object,
                new DoubanApi(LoggerFactory),
                new TmdbApi(LoggerFactory),
                new OmdbApi(LoggerFactory),
                new ImdbApi(LoggerFactory));
            provider.TestConfigOverride = true;

            var result = await provider.GetMetadata(ScrapedDouban(), CancellationToken.None);

            Assert.IsFalse(result.HasMetadata, "短路应返回空结果，core 保留库内数据");
            Assert.IsNull(result.Item, "短路不应返回任何新元数据");
        }

        [TestMethod]
        public void UnscrapedMovie_OnPlaybackInfo_WithSwitchOn_IsNotShortCircuited()
        {
            // 未刮削条目不应被短路；用纯函数验证判定（避免测试真实出网）
            Assert.IsFalse(BaseProvider.ShouldSkipOnlineMetadata(true, true, Unscraped()));
        }
    }
}
