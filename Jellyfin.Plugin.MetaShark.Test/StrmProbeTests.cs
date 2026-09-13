using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaShark.StrmProbe;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
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
            service.TestConfigOverride = (true, true);
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
            service.TestConfigOverride = (true, false);

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
    }
}
