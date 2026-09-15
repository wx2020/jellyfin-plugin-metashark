using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MetaShark.Splashscreen;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class SplashscreenLibraryFilterTests
    {
        private sealed class NullLogger<T> : ILogger<T>
        {
            IDisposable ILogger.BeginScope<TState>(TState state) => new NullScope();

            bool ILogger.IsEnabled(LogLevel logLevel) => false;

            void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
            }

            private sealed class NullScope : IDisposable
            {
                public void Dispose()
                {
                }
            }
        }

        private static readonly Guid MovieLibraryId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private static readonly Guid AdultLibraryId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        private static List<VirtualFolderInfo> Folders()
        {
            return new List<VirtualFolderInfo>
            {
                new VirtualFolderInfo { Name = "电影", ItemId = MovieLibraryId.ToString() },
                new VirtualFolderInfo { Name = "xxx", ItemId = AdultLibraryId.ToString() },
            };
        }

        [TestMethod]
        public void Tokenize_SplitsOnDelimitersAndTrims()
        {
            CollectionAssert.AreEqual(new[] { "电影", "电视剧", "abc" }, SplashscreenLibraryFilterProxy.Tokenize(" 电影 ; 电视剧 ,\r\n abc "));
            Assert.AreEqual(0, SplashscreenLibraryFilterProxy.Tokenize("").Length);
            Assert.AreEqual(0, SplashscreenLibraryFilterProxy.Tokenize(null).Length);
            Assert.AreEqual(0, SplashscreenLibraryFilterProxy.Tokenize(" ; , \n ").Length);
        }

        [TestMethod]
        public void ResolveWhitelist_ByGuid_ResolvesToFolderItemId()
        {
            var resolved = SplashscreenLibraryFilterProxy.ResolveWhitelist(MovieLibraryId.ToString(), Folders());
            CollectionAssert.AreEqual(new[] { MovieLibraryId }, resolved);
        }

        [TestMethod]
        public void ResolveWhitelist_ByName_IsCaseInsensitiveAndDedupes()
        {
            var resolved = SplashscreenLibraryFilterProxy.ResolveWhitelist("电影;电影;XXX", Folders());
            CollectionAssert.AreEqual(new[] { MovieLibraryId, AdultLibraryId }, resolved);
        }

        [TestMethod]
        public void ResolveWhitelist_MixedAndUnknown_IgnoresUnmatched()
        {
            var raw = $"电影,{AdultLibraryId},{Guid.NewGuid()}";
            var resolved = SplashscreenLibraryFilterProxy.ResolveWhitelist(raw, Folders());
            CollectionAssert.AreEqual(new[] { MovieLibraryId, AdultLibraryId }, resolved);

            Assert.AreEqual(0, SplashscreenLibraryFilterProxy.ResolveWhitelist("不存在", Folders()).Length);
            Assert.AreEqual(0, SplashscreenLibraryFilterProxy.ResolveWhitelist("电影", new List<VirtualFolderInfo>()).Length);
            Assert.AreEqual(0, SplashscreenLibraryFilterProxy.ResolveWhitelist("", Folders()).Length);
        }

        [TestMethod]
        public void BuildQuery_UsesTopParentIdsAndNoParentalRatingFilter()
        {
            var query = SplashscreenLibraryFilterProxy.BuildQuery(ImageType.Primary, new[] { MovieLibraryId });

            Assert.AreEqual(true, query.Recursive);
            Assert.AreEqual(false, query.CollapseBoxSetItems);
            Assert.AreEqual(30, query.Limit);
            Assert.IsNull(query.MaxParentalRating);
            CollectionAssert.AreEqual(new[] { MovieLibraryId }, query.TopParentIds);
            CollectionAssert.AreEqual(new[] { ImageType.Primary }, query.ImageTypes);
            CollectionAssert.AreEqual(new[] { BaseItemKind.Movie, BaseItemKind.Series }, query.IncludeItemTypes);
            Assert.AreEqual(1, query.OrderBy.Count);
            Assert.AreEqual(ItemSortBy.Random, query.OrderBy[0].OrderBy);
            Assert.AreEqual(SortOrder.Ascending, query.OrderBy[0].SortOrder);
        }

        [TestMethod]
        public void Disabled_ForwardsOriginalListsWithoutFiltering()
        {
            var posters = new List<string> { "p1", "p2" };
            var backdrops = new List<string> { "b1" };
            IReadOnlyList<string>? gotPosters = null;
            IReadOnlyList<string>? gotBackdrops = null;
            var mock = new Mock<IImageEncoder>(MockBehavior.Strict);
            mock.Setup(m => m.CreateSplashscreen(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>()))
                .Callback<IReadOnlyList<string>, IReadOnlyList<string>>((p, b) =>
                {
                    gotPosters = p;
                    gotBackdrops = b;
                });

            var proxy = SplashscreenLibraryFilterProxy.CreateForTest(mock.Object, new NullLogger<SplashscreenLibraryFilterProxy>());
            ((SplashscreenLibraryFilterProxy)(object)proxy).TestConfigOverride = (false, "电影");
            proxy.CreateSplashscreen(posters, backdrops);

            CollectionAssert.AreEqual(posters, gotPosters!.ToList());
            CollectionAssert.AreEqual(backdrops, gotBackdrops!.ToList());
        }

        [TestMethod]
        public void Enabled_EmptyWhitelist_FailsSafeToEmpty()
        {
            IReadOnlyList<string>? gotPosters = null;
            IReadOnlyList<string>? gotBackdrops = null;
            var mock = new Mock<IImageEncoder>(MockBehavior.Strict);
            mock.Setup(m => m.CreateSplashscreen(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>()))
                .Callback<IReadOnlyList<string>, IReadOnlyList<string>>((p, b) =>
                {
                    gotPosters = p;
                    gotBackdrops = b;
                });

            var proxy = SplashscreenLibraryFilterProxy.CreateForTest(mock.Object, new NullLogger<SplashscreenLibraryFilterProxy>());
            var decorator = (SplashscreenLibraryFilterProxy)(object)proxy;
            decorator.TestConfigOverride = (true, string.Empty);
            decorator.FoldersOverride = Folders;

            proxy.CreateSplashscreen(new List<string> { "leak" }, new List<string> { "leak" });

            Assert.AreEqual(0, gotPosters!.Count);
            Assert.AreEqual(0, gotBackdrops!.Count);
        }

        [TestMethod]
        public void Enabled_ResolvesWhitelistAndForwardsFilteredPaths()
        {
            IReadOnlyList<string>? gotPosters = null;
            IReadOnlyList<string>? gotBackdrops = null;
            Guid[]? capturedIds = null;
            var mock = new Mock<IImageEncoder>(MockBehavior.Strict);
            mock.Setup(m => m.CreateSplashscreen(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>()))
                .Callback<IReadOnlyList<string>, IReadOnlyList<string>>((p, b) =>
                {
                    gotPosters = p;
                    gotBackdrops = b;
                });

            var proxy = SplashscreenLibraryFilterProxy.CreateForTest(mock.Object, new NullLogger<SplashscreenLibraryFilterProxy>());
            var decorator = (SplashscreenLibraryFilterProxy)(object)proxy;
            decorator.TestConfigOverride = (true, "电影");
            decorator.FoldersOverride = Folders;
            decorator.PathSourceOverride = (type, ids) =>
            {
                capturedIds = ids;
                return type == ImageType.Primary
                    ? new List<string> { "allowed-poster" }
                    : new List<string> { "allowed-thumb" };
            };

            proxy.CreateSplashscreen(new List<string> { "leak" }, new List<string> { "leak" });

            CollectionAssert.AreEqual(new[] { MovieLibraryId }, capturedIds);
            CollectionAssert.AreEqual(new[] { "allowed-poster" }, gotPosters!.ToList());
            CollectionAssert.AreEqual(new[] { "allowed-thumb" }, gotBackdrops!.ToList());
        }

        [TestMethod]
        public void Enabled_BackdropFallback_WhenNoThumbs()
        {
            var calls = new List<ImageType>();
            IReadOnlyList<string>? gotBackdrops = null;
            var mock = new Mock<IImageEncoder>(MockBehavior.Strict);
            mock.Setup(m => m.CreateSplashscreen(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>()))
                .Callback<IReadOnlyList<string>, IReadOnlyList<string>>((p, b) => gotBackdrops = b);

            var proxy = SplashscreenLibraryFilterProxy.CreateForTest(mock.Object, new NullLogger<SplashscreenLibraryFilterProxy>());
            var decorator = (SplashscreenLibraryFilterProxy)(object)proxy;
            decorator.TestConfigOverride = (true, "电影");
            decorator.FoldersOverride = Folders;
            decorator.PathSourceOverride = (type, ids) =>
            {
                calls.Add(type);
                return type == ImageType.Thumb
                    ? new List<string>()
                    : new List<string> { "from-backdrop" };
            };

            proxy.CreateSplashscreen(new List<string> { "leak" }, new List<string> { "leak" });

            CollectionAssert.AreEqual(new[] { ImageType.Primary, ImageType.Thumb, ImageType.Backdrop }, calls);
            CollectionAssert.AreEqual(new[] { "from-backdrop" }, gotBackdrops!.ToList());
        }
    }
}
