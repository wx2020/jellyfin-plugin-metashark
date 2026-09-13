using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaShark.StrmProbe;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class StrmProbeTests
    {
        // ---------- fakes ----------

        private sealed class FakeStore : IStrmProbeCacheStore
        {
            private readonly Dictionary<string, StrmProbeCacheEntry> _map = new Dictionary<string, StrmProbeCacheEntry>(StringComparer.Ordinal);

            public void Dispose()
            {
            }

            public StrmProbeCacheEntry? TryGet(string key, DateTime nowUtc)
            {
                if (_map.TryGetValue(key, out var e))
                {
                    if (e.ExpiresAtUtc <= nowUtc)
                    {
                        _map.Remove(key);
                        return null;
                    }

                    return e;
                }

                return null;
            }

            public void Set(StrmProbeCacheEntry entry)
            {
                _map[entry.Key] = entry;
            }

            public int RemoveExpired(DateTime nowUtc)
            {
                var dead = new List<string>();
                foreach (var kv in _map)
                {
                    if (kv.Value.ExpiresAtUtc <= nowUtc)
                    {
                        dead.Add(kv.Key);
                    }
                }

                foreach (var k in dead)
                {
                    _map.Remove(k);
                }

                return dead.Count;
            }

            public int Count => _map.Count;
        }

        private sealed class FakeProber : IStrmProber
        {
            public int Calls;
            public string DirectUrl = "https://cdn.example.com/video.mp4";
            public bool Throw;

            public Task<StrmProbeResult> ProbeAsync(string url, CancellationToken cancellationToken)
            {
                Calls++;
                if (Throw)
                {
                    throw new System.Net.Http.HttpRequestException("probe failed");
                }

                return Task.FromResult(new StrmProbeResult
                {
                    Url = url,
                    DirectUrl = DirectUrl,
                    ContentType = "video/mp4",
                    ContentLength = 12345,
                    ProbedAtUtc = DateTime.UtcNow,
                });
            }
        }

        private sealed class TestLogger : ILogger
        {
            public readonly List<(LogLevel Level, string Message)> Entries = new List<(LogLevel, string)>();

            IDisposable ILogger.BeginScope<TState>(TState state) => NullScope.Instance;

            bool ILogger.IsEnabled(LogLevel logLevel) => true;

            void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new NullScope();

                public void Dispose()
                {
                }
            }
        }

        private sealed class TestStoreLogger : ILogger<SqliteStrmProbeCacheStore>
        {
            IDisposable ILogger.BeginScope<TState>(TState state) => throw new NotSupportedException();

            bool ILogger.IsEnabled(LogLevel logLevel) => false;

            void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
            }
        }

        // ---------- key ----------

        [TestMethod]
        public void CacheKey_SameInput_SameKey()
        {
            var a = StrmProbeCacheKey.Compute("https://pan.example.com/f/1", 42, "123");
            var b = StrmProbeCacheKey.Compute("https://pan.example.com/f/1", 42, "123");
            Assert.AreEqual(a, b);
            Assert.AreEqual(64, a.Length);
        }

        [TestMethod]
        public void CacheKey_Differs_By_Url_Size_Signature()
        {
            var baseKey = StrmProbeCacheKey.Compute("https://pan.example.com/f/1", 42, "123");
            Assert.AreNotEqual(baseKey, StrmProbeCacheKey.Compute("https://pan.example.com/f/2", 42, "123"));
            Assert.AreNotEqual(baseKey, StrmProbeCacheKey.Compute("https://pan.example.com/f/1", 43, "123"));
            Assert.AreNotEqual(baseKey, StrmProbeCacheKey.Compute("https://pan.example.com/f/1", 42, "124"));
        }

        // ---------- sqlite store ----------

        [TestMethod]
        public void SqliteStore_Roundtrip_Hit_Miss_Expired()
        {
            var db = Path.Combine(Path.GetTempPath(), "metashark-test-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using var store = new SqliteStrmProbeCacheStore(db, new TestStoreLogger());
                var now = DateTime.UtcNow;
                Assert.IsNull(store.TryGet("nope", now));

                store.Set(new StrmProbeCacheEntry
                {
                    Key = "k1",
                    Url = "https://pan.example.com/f/1",
                    FileSize = 10,
                    Signature = "1",
                    DirectUrl = "https://cdn.example.com/a.mp4",
                    ContentType = "video/mp4",
                    ContentLength = 100,
                    ProbedAtUtc = now,
                    ExpiresAtUtc = now.AddHours(1),
                });
                var hit = store.TryGet("k1", now);
                Assert.IsNotNull(hit);
                Assert.AreEqual("https://cdn.example.com/a.mp4", hit!.DirectUrl);

                store.Set(new StrmProbeCacheEntry
                {
                    Key = "k2",
                    Url = "https://pan.example.com/f/2",
                    FileSize = 10,
                    Signature = "1",
                    DirectUrl = "https://cdn.example.com/b.mp4",
                    ProbedAtUtc = now.AddHours(-2),
                    ExpiresAtUtc = now.AddSeconds(-1),
                });
                // 过期视为未命中并自动清理
                Assert.IsNull(store.TryGet("k2", now));
                Assert.IsNull(store.TryGet("k2", now));
            }
            finally
            {
                try
                {
                    File.Delete(db);
                }
                catch (IOException)
                {
                }
            }
        }

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

        // ---------- switch combinations ----------

        [TestMethod]
        public void MasterOff_Disables_Everything_Even_On_Hit()
        {
            var whitelist = StrmClientPolicy.ParseWhitelist("Yamby");
            var d = StrmClientPolicy.Resolve(false, true, "Yamby", whitelist, cacheHit: true);
            Assert.AreEqual(StrmPlaybackDecisionKind.Native, d.Kind);
            Assert.IsFalse(d.ShouldReadCache);
            Assert.IsFalse(d.ServeVirtual);
            Assert.IsFalse(d.EnqueueWarmup);
        }

        [TestMethod]
        public void Whitelisted_Hit_Returns_VirtualCached()
        {
            var whitelist = StrmClientPolicy.ParseWhitelist("Yamby");
            var d = StrmClientPolicy.Resolve(true, true, "Yamby", whitelist, cacheHit: true);
            Assert.AreEqual(StrmPlaybackDecisionKind.VirtualCached, d.Kind);
            Assert.IsTrue(d.ShouldReadCache);
            Assert.IsTrue(d.ServeVirtual);
            Assert.IsFalse(d.EnqueueWarmup);
        }

        [TestMethod]
        public void Whitelisted_Miss_WithDirectOn_Returns_VirtualUnprobed_And_Warmup()
        {
            var whitelist = StrmClientPolicy.ParseWhitelist("Yamby");
            var d = StrmClientPolicy.Resolve(true, true, "Yamby", whitelist, cacheHit: false);
            Assert.AreEqual(StrmPlaybackDecisionKind.VirtualUnprobed, d.Kind);
            Assert.IsTrue(d.ServeVirtual);
            Assert.IsTrue(d.EnqueueWarmup);
        }

        [TestMethod]
        public void Whitelisted_Miss_WithDirectOff_Returns_Native_But_Warmup()
        {
            var whitelist = StrmClientPolicy.ParseWhitelist("Yamby");
            var d = StrmClientPolicy.Resolve(true, false, "Yamby", whitelist, cacheHit: false);
            Assert.AreEqual(StrmPlaybackDecisionKind.NativeWithWarmup, d.Kind);
            Assert.IsFalse(d.ServeVirtual);
            Assert.IsTrue(d.EnqueueWarmup);
            Assert.IsTrue(d.ShouldReadCache);
        }

        [TestMethod]
        public void NonWhitelisted_ThirdParty_Gets_Native_Without_CacheRead_Or_Warmup()
        {
            var whitelist = StrmClientPolicy.ParseWhitelist("Yamby");
            var d = StrmClientPolicy.Resolve(true, true, "Infuse", whitelist, cacheHit: false);
            Assert.AreEqual(StrmPlaybackDecisionKind.Native, d.Kind);
            Assert.IsFalse(d.ShouldReadCache);
            Assert.IsFalse(d.EnqueueWarmup);
        }

        [TestMethod]
        [DataRow("Jellyfin Web", true, true)]
        [DataRow("Jellyfin Android", false, false)]
        [DataRow("Jellyfin iOS", true, false)]
        [DataRow("Jellyfin Media Player", false, true)]
        [DataRow(null, true, true)]
        [DataRow("", false, false)]
        public void Official_And_Unknown_Clients_NeverChange_Regardless_Of_Switches(
            string? client, bool directOn, bool hit)
        {
            var whitelist = StrmClientPolicy.ParseWhitelist("Yamby");
            var d = StrmClientPolicy.Resolve(true, directOn, client, whitelist, hit);
            Assert.AreEqual(StrmPlaybackDecisionKind.Native, d.Kind);
            Assert.IsFalse(d.ShouldReadCache);
            Assert.IsFalse(d.ServeVirtual);
            Assert.IsFalse(d.EnqueueWarmup);
        }

        // ---------- warmup writes ----------

        [TestMethod]
        public async Task Warmup_Writes_Cache_And_Second_Run_Hits_Without_Reprobe()
        {
            var dir = Path.Combine(Path.GetTempPath(), "metashark-strm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var strm = Path.Combine(dir, "movie.strm");
            File.WriteAllText(strm, "https://pan.example.com/f/1\n");
            try
            {
                var store = new FakeStore();
                var prober = new FakeProber();
                var logger = new TestLogger();

                var ok = await StrmProbeWarmupService.WarmupSingleFileAsync(strm, store, prober, logger, CancellationToken.None);
                Assert.IsTrue(ok);
                Assert.AreEqual(1, prober.Calls);
                Assert.AreEqual(1, store.Count);

                var ok2 = await StrmProbeWarmupService.WarmupSingleFileAsync(strm, store, prober, logger, CancellationToken.None);
                Assert.IsTrue(ok2);
                Assert.AreEqual(1, prober.Calls, "缓存命中不应再次探针");
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                }
            }
        }

        [TestMethod]
        public async Task Warmup_Skips_NonStrm_And_Missing_Files_Without_Probing()
        {
            var store = new FakeStore();
            var prober = new FakeProber();
            var logger = new TestLogger();

            Assert.IsFalse(await StrmProbeWarmupService.WarmupSingleFileAsync("/tmp/does-not-exist-xyz.strm", store, prober, logger, CancellationToken.None));

            var dir = Path.Combine(Path.GetTempPath(), "metashark-strm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // 空 strm（仅空白/注释）验证失败路径
                var empty = Path.Combine(dir, "empty.strm");
                File.WriteAllText(empty, "   \n# comment\n");
                Assert.IsFalse(await StrmProbeWarmupService.WarmupSingleFileAsync(empty, store, prober, logger, CancellationToken.None));
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                }
            }

            Assert.AreEqual(0, prober.Calls);
            Assert.AreEqual(0, store.Count);
        }

        [TestMethod]
        public async Task Warmup_ProbeFailure_Does_Not_Throw_And_Writes_Nothing()
        {
            var dir = Path.Combine(Path.GetTempPath(), "metashark-strm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var strm = Path.Combine(dir, "movie.strm");
            File.WriteAllText(strm, "https://pan.example.com/f/9\n");
            try
            {
                var store = new FakeStore();
                var prober = new FakeProber { Throw = true };
                var logger = new TestLogger();
                Assert.IsFalse(await StrmProbeWarmupService.WarmupSingleFileAsync(strm, store, prober, logger, CancellationToken.None));
                Assert.AreEqual(0, store.Count);
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                }
            }
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
                new FakeStore(),
                new FakeProber(),
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

        // ---------- virtual source ----------

        [TestMethod]
        public void VirtualSource_Is_Additive_Remote_Http_Without_Opening()
        {
            var src = StrmVirtualSourceFactory.Build(new string('a', 64), "https://cdn.example.com/movie.mp4", 12345, "video/mp4");
            // Id 必须是纯 Guid（"N" 格式）：服务端混流/转码路径会 Guid.Parse(mediaSourceId)
            Assert.IsTrue(Guid.TryParseExact(src.Id, "N", out _));
            Assert.AreEqual(MediaProtocol.Http, src.Protocol);
            Assert.IsTrue(src.IsRemote);
            Assert.IsFalse(src.RequiresOpening);
            Assert.IsFalse(src.SupportsTranscoding);
            Assert.IsTrue(src.SupportsDirectPlay);
            Assert.AreEqual("https://cdn.example.com/movie.mp4", src.Path);
            Assert.AreEqual("mp4", src.Container);
        }

        [TestMethod]
        public void VirtualSource_Id_Is_Deterministic_And_Key_Derived()
        {
            var key = new string('a', 64);
            var a = StrmVirtualSourceFactory.Build(key, "https://pan.example.com/a.mkv?sign=abc", null, null);
            var b = StrmVirtualSourceFactory.Build(key, "https://pan.example.com/a.mkv?sign=abc", null, null);
            var c = StrmVirtualSourceFactory.Build(new string('b', 64), "https://pan.example.com/a.mkv?sign=abc", null, null);

            Assert.AreEqual(a.Id, b.Id);
            Assert.AreNotEqual(a.Id, c.Id);
            Assert.AreEqual(key, a.ETag);

            // 容器从扩展名解析需容忍 query 串
            Assert.AreEqual("mkv", a.Container);
        }

        [TestMethod]
        public void VirtualSource_Build_Rejects_Empty_Url()
        {
            Assert.ThrowsException<ArgumentException>(() => StrmVirtualSourceFactory.Build("k", " ", null, null));
        }

        // ---------- client resolver (AuthorizationInfo 优先) ----------

        [TestMethod]
        public void Resolver_Prefers_AuthorizationInfo_Over_Headers()
        {
            // 线上真机场景：Yamby 用 api_key 鉴权，请求无鉴权头，
            // 服务端已把 Client 回填进 HttpContext.Items，必须优先采用。
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

        // ---------- stream fallback (取流补身份) ----------

        private static (string Dir, string StrmPath, string Key) NewStrmFileWithKey(string url)
        {
            var dir = Path.Combine(Path.GetTempPath(), "metashark-streamfb-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var strm = Path.Combine(dir, "movie.strm");
            File.WriteAllText(strm, url + "\n");
            Assert.IsTrue(StrmFileHelper.TryReadStrmLink(strm, out var u, out var size, out var sig));
            return (dir, strm, StrmProbeCacheKey.Compute(u, size, sig));
        }

        private static void SeedHit(FakeStore store, string key, string url)
        {
            var now = DateTime.UtcNow;
            store.Set(new StrmProbeCacheEntry
            {
                Key = key,
                Url = url,
                FileSize = 10,
                Signature = "1",
                DirectUrl = "https://cdn.example.com/x.mkv",
                ContentType = "video/x-matroska",
                ContentLength = 999,
                ProbedAtUtc = now,
                ExpiresAtUtc = now.AddHours(1),
            });
        }

        private static DefaultHttpContext NewStreamContext(string path, string? mediaSourceId)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Path = path;
            if (mediaSourceId != null)
            {
                ctx.Request.QueryString = QueryString.Create("mediaSourceId", mediaSourceId);
            }

            return ctx;
        }

        [TestMethod]
        public void StreamFallback_Hit_WithMatchingId_ReturnsVirtual_WithoutClientIdentity()
        {
            var (dir, strm, key) = NewStrmFileWithKey("https://pan.example.com/f/stream1");
            try
            {
                var store = new FakeStore();
                SeedHit(store, key, "https://pan.example.com/f/stream1");
                var virtualId = StrmVirtualSourceFactory.DeriveStableId(key);

                // 纯 api_key 取流：无 Items、无鉴权头、无查询串身份
                var ctx = NewStreamContext("/Videos/c9e3b155d11a63e35949d45d0decd90c/stream.mkv", virtualId);
                var movie = NewStrmMovie(Guid.NewGuid(), strm);

                var src = StrmStreamFallback.TryResolve(ctx.Request, movie, store, DateTime.UtcNow);

                Assert.IsNotNull(src);
                Assert.AreEqual(virtualId, src!.Id);
                Assert.AreEqual("https://pan.example.com/f/stream1", src.Path);
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                }
            }
        }

        [TestMethod]
        public void StreamFallback_WrongId_ReturnsNull()
        {
            var (dir, strm, key) = NewStrmFileWithKey("https://pan.example.com/f/stream2");
            try
            {
                var store = new FakeStore();
                SeedHit(store, key, "https://pan.example.com/f/stream2");

                // 原生 itemId（带连字符 Guid）必然与派生虚拟 Id 不同
                var ctx = NewStreamContext("/Videos/c9e3b155d11a63e35949d45d0decd90c/stream.mkv", "c9e3b155d11a63e35949d45d0decd90c");
                var movie = NewStrmMovie(Guid.NewGuid(), strm);

                Assert.IsNull(StrmStreamFallback.TryResolve(ctx.Request, movie, store, DateTime.UtcNow));
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                }
            }
        }

        [TestMethod]
        public void StreamFallback_CacheMiss_ReturnsNull_WithoutWarmup()
        {
            var (dir, strm, key) = NewStrmFileWithKey("https://pan.example.com/f/stream3");
            try
            {
                // 故意不写缓存：取流路径只读，未命中走原生回退，不触发后台探针
                var store = new FakeStore();
                var virtualId = StrmVirtualSourceFactory.DeriveStableId(key);
                var ctx = NewStreamContext("/Videos/c9e3b155d11a63e35949d45d0decd90c/stream.mkv", virtualId);
                var movie = NewStrmMovie(Guid.NewGuid(), strm);

                Assert.IsNull(StrmStreamFallback.TryResolve(ctx.Request, movie, store, DateTime.UtcNow));
                Assert.AreEqual(0, store.Count);
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                }
            }
        }

        [TestMethod]
        public void StreamFallback_NonVideosPath_ReturnsNull()
        {
            var (dir, strm, key) = NewStrmFileWithKey("https://pan.example.com/f/stream4");
            try
            {
                var store = new FakeStore();
                SeedHit(store, key, "https://pan.example.com/f/stream4");
                var virtualId = StrmVirtualSourceFactory.DeriveStableId(key);

                // PlaybackInfo 路径不受此兜底影响（仍走白名单门控）
                var ctx = NewStreamContext("/Items/c9e3b155d11a63e35949d45d0decd90c/PlaybackInfo", virtualId);
                var movie = NewStrmMovie(Guid.NewGuid(), strm);

                Assert.IsNull(StrmStreamFallback.TryResolve(ctx.Request, movie, store, DateTime.UtcNow));
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                }
            }
        }

        [TestMethod]
        public void StreamFallback_MissingQuery_Or_NonStrm_ReturnsNull()
        {
            var (dir, strm, key) = NewStrmFileWithKey("https://pan.example.com/f/stream5");
            try
            {
                var store = new FakeStore();
                SeedHit(store, key, "https://pan.example.com/f/stream5");
                var movie = NewStrmMovie(Guid.NewGuid(), strm);

                // 缺 mediaSourceId 查询参数
                var noQuery = NewStreamContext("/Videos/c9e3b155d11a63e35949d45d0decd90c/stream.mkv", null);
                Assert.IsNull(StrmStreamFallback.TryResolve(noQuery.Request, movie, store, DateTime.UtcNow));

                // 非 strm 条目
                var plain = NewStrmMovie(Guid.NewGuid(), "/media/plain.mkv");
                var withId = NewStreamContext(
                    "/Videos/c9e3b155d11a63e35949d45d0decd90c/stream.mkv",
                    StrmVirtualSourceFactory.DeriveStableId(key));
                Assert.IsNull(StrmStreamFallback.TryResolve(withId.Request, plain, store, DateTime.UtcNow));

                // 空请求
                Assert.IsNull(StrmStreamFallback.TryResolve(null, movie, store, DateTime.UtcNow));
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
