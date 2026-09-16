using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Controllers;
using Jellyfin.Plugin.MetaShark.StrmProbe;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class StrmProxyTests
    {
        private const string Secret = "metashark-proxy-test-secret-0123456789ABCDEF";
        private const string StrmUrl = "https://openlist.example.com:5001/d/189/movie/test.mkv?sign=abc123";
        private const string UpstreamUrl = "http://upstream.local/189/movie/test.mkv";
        private const long FileSize = 288;
        private const string Signature = "639244000000000000";
        private static readonly Guid ItemId = Guid.Parse("c9e3b155-d11a-63e3-5949-d45d0decd90c");
        private const string ProxyUrl = "http://jellyfin.local:8096/plugin/metashark/strm/proxy/00000000000000000000000000000000?exp=1&sig=abc";

        private sealed class TestLogger<T> : ILogger<T>
        {
            IDisposable ILogger.BeginScope<TState>(TState state) => throw new NotSupportedException();

            bool ILogger.IsEnabled(LogLevel logLevel) => false;

            void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
            }
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

            public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            {
                _handler = handler;
            }

            public HttpRequestMessage? LastRequest { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                return Task.FromResult(_handler(request));
            }
        }

        private sealed class StubHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpMessageHandler _handler;

            public StubHttpClientFactory(HttpMessageHandler handler)
            {
                _handler = handler;
            }

            public HttpClient CreateClient(string name) => new HttpClient(_handler, disposeHandler: false);
        }

        private static StrmProxyTokenService NewTokenService() => new StrmProxyTokenService(() => Secret);

        private static string WriteStrm(string content)
        {
            var dir = Path.Combine(Path.GetTempPath(), "metashark-proxy-test");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".strm");
            File.WriteAllText(path, content);
            return path;
        }

        private static MediaBrowser.Controller.Entities.Movies.Movie NewMovie(Guid id, string path)
        {
            var movie = (MediaBrowser.Controller.Entities.Movies.Movie)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(MediaBrowser.Controller.Entities.Movies.Movie));
            movie.Id = id;
            movie.Name = "proxy-target";
            movie.Path = path;
            return movie;
        }

        private static IHttpContextAccessor NewAccessor(string? client)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Scheme = "http";
            ctx.Request.Host = new HostString("jellyfin.local", 8096);
            if (client != null)
            {
                var identity = new ClaimsIdentity("test");
                identity.AddClaim(new Claim(StrmClientResolver.ClientClaimType, client));
                ctx.User = new ClaimsPrincipal(identity);
            }

            return new HttpContextAccessor { HttpContext = ctx };
        }

        private static PluginConfiguration ProxyConfig(bool enabled = true)
        {
            return new PluginConfiguration
            {
                EnableStrmProxySource = enabled,
                StrmProbeClientWhitelist = "Yamby;Lenna",
            };
        }

        private static string ValidTicket(out long exp)
        {
            NewTokenService().TryCreateTicket(ItemId, DateTime.UtcNow, out exp, out var sig);
            return sig;
        }

        private static byte[] ReadBody(DefaultHttpContext ctx)
        {
            ctx.Response.Body.Position = 0;
            using var buffer = new MemoryStream();
            ctx.Response.Body.CopyTo(buffer);
            return buffer.ToArray();
        }

        // ---------- token 签发/校验 ----------

        [TestMethod]
        public void Ticket_RoundTrip_Validates()
        {
            var service = NewTokenService();
            var now = DateTime.UtcNow;
            Assert.IsTrue(service.TryCreateTicket(ItemId, now, out var exp, out var sig));
            Assert.AreEqual(StrmProxyTokenService.ToUnixSeconds(now.Add(StrmProbeConstants.StrmProxyTicketTtl)), exp);
            Assert.IsTrue(service.Validate(ItemId, exp.ToString(), sig, now));
        }

        [TestMethod]
        public void Ticket_TamperedSignature_Rejected()
        {
            var service = NewTokenService();
            var now = DateTime.UtcNow;
            service.TryCreateTicket(ItemId, now, out var exp, out var sig);
            var tampered = (sig[0] == 'a' ? "b" : "a") + sig.Substring(1);
            Assert.IsFalse(service.Validate(ItemId, exp.ToString(), tampered, now));
        }

        [TestMethod]
        public void Ticket_Expired_Rejected()
        {
            var service = NewTokenService();
            var now = DateTime.UtcNow;
            service.TryCreateTicket(ItemId, now, out var exp, out var sig);
            Assert.IsFalse(service.Validate(ItemId, exp.ToString(), sig, now.Add(StrmProbeConstants.StrmProxyTicketTtl).Add(TimeSpan.FromSeconds(1))));
        }

        [TestMethod]
        public void Ticket_WrongItem_Rejected()
        {
            var service = NewTokenService();
            var now = DateTime.UtcNow;
            service.TryCreateTicket(ItemId, now, out var exp, out var sig);
            Assert.IsFalse(service.Validate(Guid.NewGuid(), exp.ToString(), sig, now));
        }

        [TestMethod]
        public void Ticket_EmptyOrMissingParams_Rejected()
        {
            var service = NewTokenService();
            Assert.IsFalse(service.Validate(ItemId, null, null, DateTime.UtcNow));
            Assert.IsFalse(service.Validate(ItemId, "abc", "sig", DateTime.UtcNow));
            Assert.IsFalse(service.Validate(Guid.Empty, "1", "sig", DateTime.UtcNow));
        }

        [TestMethod]
        public void Ticket_EmptySecret_FailsClosed()
        {
            var service = new StrmProxyTokenService(() => string.Empty);
            Assert.IsFalse(service.TryCreateTicket(ItemId, DateTime.UtcNow, out _, out _));
            Assert.IsFalse(service.Validate(ItemId, "9999999999", "deadbeef", DateTime.UtcNow));
        }

        [TestMethod]
        public void ProxyUrl_RelativeAndAbsolute()
        {
            Assert.AreEqual(
                "/plugin/metashark/strm/proxy/" + ItemId.ToString("N") + "?exp=123&sig=abc",
                StrmProxyUrl.BuildRelativePath(ItemId, 123, "abc"));

            var ctx = new DefaultHttpContext();
            ctx.Request.Scheme = "https";
            ctx.Request.Host = new HostString("jellyfin.example.com", 5001);
            Assert.AreEqual(
                "https://jellyfin.example.com:5001/plugin/metashark/strm/proxy/" + ItemId.ToString("N") + "?exp=123&sig=abc",
                StrmProxyUrl.BuildAbsoluteUrl(ctx.Request, ItemId, 123, "abc"));
        }

        // ---------- provider ----------

        [TestMethod]
        public void Provider_WhitelistedClient_ReturnsProxySource()
        {
            var strmPath = WriteStrm(StrmUrl);
            var movie = NewMovie(ItemId, strmPath);
            var provider = new StrmProxyMediaSourceProvider(
                NewAccessor("Lenna"),
                NewTokenService(),
                new TestLogger<StrmProxyMediaSourceProvider>())
            {
                TestConfigOverride = ProxyConfig(),
            };

            var sources = provider.BuildSources(movie).ToList();

            Assert.AreEqual(1, sources.Count);
            var source = sources[0];
            Assert.AreEqual(StrmProxySourceFactory.SourceName, source.Name);
            Assert.IsFalse(source.SupportsTranscoding);
            Assert.IsTrue(source.SupportsDirectPlay);
            Assert.IsTrue(source.SupportsDirectStream);
            Assert.IsTrue(source.IsRemote);
            Assert.AreEqual("mkv", source.Container);
            Assert.IsTrue(source.Path.StartsWith("http://jellyfin.local:8096/plugin/metashark/strm/proxy/" + ItemId.ToString("N") + "?", StringComparison.Ordinal));
            Assert.IsTrue(source.Path.Contains("exp=", StringComparison.Ordinal));
            Assert.IsTrue(source.Path.Contains("sig=", StringComparison.Ordinal));
            Assert.AreEqual(StrmSourceKey.DeriveStableId(source.ETag), source.Id);
        }

        [TestMethod]
        public void Provider_Id_IsDeterministicAcrossCalls()
        {
            var strmPath = WriteStrm(StrmUrl);
            var movie = NewMovie(ItemId, strmPath);
            var provider = new StrmProxyMediaSourceProvider(
                NewAccessor("Yamby"),
                NewTokenService(),
                new TestLogger<StrmProxyMediaSourceProvider>())
            {
                TestConfigOverride = ProxyConfig(),
            };

            var first = provider.BuildSources(movie).Single();
            var second = provider.BuildSources(movie).Single();

            Assert.AreEqual(first.Id, second.Id);
            var info = new FileInfo(strmPath);
            Assert.AreEqual(
                StrmSourceKey.DeriveStableId(StrmSourceKey.Compute(StrmUrl, info.Length, info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                first.Id);
        }

        [TestMethod]
        public void Provider_Disabled_ReturnsEmpty()
        {
            var strmPath = WriteStrm(StrmUrl);
            var movie = NewMovie(ItemId, strmPath);
            var provider = new StrmProxyMediaSourceProvider(
                NewAccessor("Lenna"),
                NewTokenService(),
                new TestLogger<StrmProxyMediaSourceProvider>())
            {
                TestConfigOverride = ProxyConfig(enabled: false),
            };

            Assert.AreEqual(0, provider.BuildSources(movie).Count());
        }

        [TestMethod]
        [DataRow(null)]
        [DataRow("Jellyfin Web")]
        [DataRow("Infuse")]
        public void Provider_NonWhitelistedClient_ReturnsEmpty(string? client)
        {
            var strmPath = WriteStrm(StrmUrl);
            var movie = NewMovie(ItemId, strmPath);
            var provider = new StrmProxyMediaSourceProvider(
                NewAccessor(client),
                NewTokenService(),
                new TestLogger<StrmProxyMediaSourceProvider>())
            {
                TestConfigOverride = ProxyConfig(),
            };

            Assert.AreEqual(0, provider.BuildSources(movie).Count());
        }

        [TestMethod]
        public void Provider_NonStrmOrNullItem_ReturnsEmpty()
        {
            var movie = NewMovie(ItemId, "/media/plain.mkv");
            var provider = new StrmProxyMediaSourceProvider(
                NewAccessor("Lenna"),
                NewTokenService(),
                new TestLogger<StrmProxyMediaSourceProvider>())
            {
                TestConfigOverride = ProxyConfig(),
            };

            Assert.AreEqual(0, provider.BuildSources(movie).Count());
            Assert.AreEqual(0, provider.BuildSources(null).Count());
        }

        [TestMethod]
        public void Provider_StrmFileMissing_ReturnsEmpty()
        {
            var movie = NewMovie(ItemId, Path.Combine(Path.GetTempPath(), "metashark-proxy-test", Guid.NewGuid().ToString("N") + ".strm"));
            var provider = new StrmProxyMediaSourceProvider(
                NewAccessor("Lenna"),
                NewTokenService(),
                new TestLogger<StrmProxyMediaSourceProvider>())
            {
                TestConfigOverride = ProxyConfig(),
            };

            Assert.AreEqual(0, provider.BuildSources(movie).Count());
        }

        [TestMethod]
        public void Provider_EmptySecret_ReturnsEmpty()
        {
            var strmPath = WriteStrm(StrmUrl);
            var movie = NewMovie(ItemId, strmPath);
            var provider = new StrmProxyMediaSourceProvider(
                NewAccessor("Lenna"),
                new StrmProxyTokenService(() => string.Empty),
                new TestLogger<StrmProxyMediaSourceProvider>())
            {
                TestConfigOverride = ProxyConfig(),
            };

            Assert.AreEqual(0, provider.BuildSources(movie).Count());
        }

        // ---------- filter 按源分流 ----------

        private static Dictionary<string, object?> BaseArgs(string? mediaSourceId = null)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["itemId"] = ItemId,
                ["container"] = "mkv",
                ["static"] = true,
                ["mediaSourceId"] = mediaSourceId ?? ItemId.ToString("D"),
            };
        }

        private static IReadOnlyList<string> Whitelist(params string[] names) => StrmClientPolicy.ParseWhitelist(string.Join(";", names));

        private static string ProxyId() => StrmSourceKey.DeriveStableId(StrmSourceKey.Compute(StrmUrl, FileSize, Signature));

        [TestMethod]
        public void Decide_NativeId_ProxyEnabled_StillRedirectsToOpenlist()
        {
            var decision = StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", BaseArgs(),
                "Lenna", Whitelist("Lenna"), ItemId, StrmUrl, FileSize, Signature,
                proxyEnabled: true, proxyUrlFactory: () => ProxyUrl);

            Assert.IsNotNull(decision);
            Assert.AreEqual(StrmUrl, decision.TargetUrl);
            Assert.AreEqual("native", decision.SourceKind);
            Assert.IsNull(decision.RewriteMediaSourceId);
        }

        [TestMethod]
        public void Decide_ProxyId_ProxyEnabled_RedirectsToSignedEndpoint()
        {
            var decision = StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStreamByContainer", BaseArgs(ProxyId()),
                "Lenna", Whitelist("Lenna"), ItemId, StrmUrl, FileSize, Signature,
                proxyEnabled: true, proxyUrlFactory: () => ProxyUrl);

            Assert.IsNotNull(decision);
            Assert.AreEqual(ProxyUrl, decision.TargetUrl);
            Assert.AreEqual("proxy", decision.SourceKind);
            Assert.IsNull(decision.RewriteMediaSourceId);
        }

        [TestMethod]
        public void Decide_ProxyId_ProxyDisabled_LegacyOpenlistRedirect()
        {
            var decision = StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", BaseArgs(ProxyId()),
                "Lenna", Whitelist("Lenna"), ItemId, StrmUrl, FileSize, Signature);

            Assert.IsNotNull(decision);
            Assert.AreEqual(StrmUrl, decision.TargetUrl);
            Assert.AreEqual("virtual", decision.SourceKind);
        }

        [TestMethod]
        public void Decide_ProxyId_TranscodeArgs_RewritesToNative()
        {
            var args = BaseArgs(ProxyId());
            args["videoCodec"] = "h264";

            var decision = StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", args,
                "Lenna", Whitelist("Lenna"), ItemId, StrmUrl, FileSize, Signature,
                proxyEnabled: true, proxyUrlFactory: () => ProxyUrl);

            Assert.IsNotNull(decision);
            Assert.AreEqual(string.Empty, decision.TargetUrl);
            Assert.AreEqual(ItemId, decision.RewriteMediaSourceId);
        }

        [TestMethod]
        public void Decide_NativeId_TranscodeArgs_NoAction()
        {
            var args = BaseArgs();
            args["videoCodec"] = "h264";

            Assert.IsNull(StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", args,
                "Lenna", Whitelist("Lenna"), ItemId, StrmUrl, FileSize, Signature,
                proxyEnabled: true, proxyUrlFactory: () => ProxyUrl));
        }

        [TestMethod]
        public void Decide_ProxyId_NonWhitelisted_NoAction()
        {
            var args = BaseArgs(ProxyId());
            args["videoCodec"] = "h264";

            Assert.IsNull(StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", args,
                "Infuse", Whitelist("Lenna"), ItemId, StrmUrl, FileSize, Signature,
                proxyEnabled: true, proxyUrlFactory: () => ProxyUrl));
        }

        [TestMethod]
        public void Decide_ProxyFactoryEmpty_FallsBackToOpenlist()
        {
            var decision = StrmDirectRedirectFilter.Decide(
                "GET", "Videos", "GetVideoStream", BaseArgs(ProxyId()),
                "Lenna", Whitelist("Lenna"), ItemId, StrmUrl, FileSize, Signature,
                proxyEnabled: true, proxyUrlFactory: () => string.Empty);

            Assert.IsNotNull(decision);
            Assert.AreEqual(StrmUrl, decision.TargetUrl);
            Assert.AreEqual("virtual", decision.SourceKind);
        }

        // ---------- 端点 Range 透传 ----------

        private static (StrmProxyController Controller, DefaultHttpContext Http) NewController(
            IHttpClientFactory factory,
            string? itemPath,
            string method = "GET",
            string? range = null)
        {
            var lib = new Mock<ILibraryManager>();
            lib.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns(itemPath == null ? (MediaBrowser.Controller.Entities.BaseItem?)null : NewMovie(ItemId, itemPath));

            var ctx = new DefaultHttpContext();
            ctx.Request.Method = method;
            if (range != null)
            {
                ctx.Request.Headers.Range = range;
            }

            ctx.Response.Body = new MemoryStream();

            var controller = new StrmProxyController(lib.Object, NewTokenService(), factory, new TestLogger<StrmProxyController>())
            {
                ControllerContext = new ControllerContext { HttpContext = ctx },
            };
            return (controller, ctx);
        }

        [TestMethod]
        public async Task Endpoint_Range206_PassesThrough()
        {
            var strmPath = WriteStrm(UpstreamUrl);
            var payload = new byte[] { 1, 2, 3, 4, 5 };
            var handler = new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(payload),
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/x-matroska");
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 4, 1000);
                return response;
            });

            var (controller, ctx) = NewController(new StubHttpClientFactory(handler), strmPath, "GET", "bytes=0-4");
            var sig = ValidTicket(out var exp);

            await controller.Proxy(ItemId, exp.ToString(), sig);

            Assert.AreEqual(206, ctx.Response.StatusCode);
            Assert.AreEqual("video/x-matroska", ctx.Response.ContentType);
            Assert.AreEqual("bytes 0-4/1000", ctx.Response.Headers["Content-Range"].ToString());
            Assert.AreEqual(5L, ctx.Response.ContentLength);
            CollectionAssert.AreEqual(payload, ReadBody(ctx));
            Assert.AreEqual("bytes=0-4", handler.LastRequest!.Headers.Range!.ToString());
            Assert.AreEqual(UpstreamUrl, handler.LastRequest.RequestUri!.ToString());
        }

        [TestMethod]
        public async Task Endpoint_NoRange200_PassesThrough()
        {
            var strmPath = WriteStrm(UpstreamUrl);
            var payload = new byte[] { 9, 8, 7 };
            var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });

            var (controller, ctx) = NewController(new StubHttpClientFactory(handler), strmPath);
            var sig = ValidTicket(out var exp);

            await controller.Proxy(ItemId, exp.ToString(), sig);

            Assert.AreEqual(200, ctx.Response.StatusCode);
            Assert.AreEqual(3L, ctx.Response.ContentLength);
            CollectionAssert.AreEqual(payload, ReadBody(ctx));
            Assert.IsNull(handler.LastRequest!.Headers.Range);
        }

        [TestMethod]
        public async Task Endpoint_Range416_PassesThrough()
        {
            var strmPath = WriteStrm(UpstreamUrl);
            var handler = new StubHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>()),
                };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(1000);
                return response;
            });

            var (controller, ctx) = NewController(new StubHttpClientFactory(handler), strmPath, "GET", "bytes=5000-6000");
            var sig = ValidTicket(out var exp);

            await controller.Proxy(ItemId, exp.ToString(), sig);

            Assert.AreEqual(416, ctx.Response.StatusCode);
            Assert.AreEqual("bytes */1000", ctx.Response.Headers["Content-Range"].ToString());
        }

        [TestMethod]
        public async Task Endpoint_InvalidTicket_Forbidden()
        {
            var strmPath = WriteStrm(UpstreamUrl);
            var called = false;
            var handler = new StubHandler(_ =>
            {
                called = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

            var (controller, ctx) = NewController(new StubHttpClientFactory(handler), strmPath);
            await controller.Proxy(ItemId, "1", "not-a-valid-signature");

            Assert.AreEqual(403, ctx.Response.StatusCode);
            Assert.IsFalse(called);
        }

        [TestMethod]
        public async Task Endpoint_MissingOrNonStrmItem_NotFound()
        {
            var (controller, ctx) = NewController(new StubHttpClientFactory(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))), null);
            var sig = ValidTicket(out var exp);
            await controller.Proxy(ItemId, exp.ToString(), sig);
            Assert.AreEqual(404, ctx.Response.StatusCode);

            var (controller2, ctx2) = NewController(new StubHttpClientFactory(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))), "/media/plain.mkv");
            var sig2 = ValidTicket(out var exp2);
            await controller2.Proxy(ItemId, exp2.ToString(), sig2);
            Assert.AreEqual(404, ctx2.Response.StatusCode);
        }

        // ---------- 密钥存储 ----------

        [TestMethod]
        public void SecretStore_LoadOrCreate_PersistsAndReuses()
        {
            var dir = Path.Combine(Path.GetTempPath(), "metashark-proxy-secret-test", Guid.NewGuid().ToString("N"));
            var path = Path.Combine(dir, StrmProbeConstants.StrmProxySecretFileName);

            var first = StrmProxySecretStore.LoadOrCreate(path, null);
            Assert.IsTrue(first.Length >= 32);
            Assert.IsTrue(File.Exists(path));

            var second = StrmProxySecretStore.LoadOrCreate(path, null);
            Assert.AreEqual(first, second);
        }
    }
}
