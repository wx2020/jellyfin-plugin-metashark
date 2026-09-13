using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaShark.StrmProbe;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class MediaInfoProbeCacheTests
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

        private sealed class FakeMediaInfoStore : IMediaInfoProbeCacheStore
        {
            private readonly Dictionary<string, MediaInfoProbeCacheEntry> _map = new Dictionary<string, MediaInfoProbeCacheEntry>(StringComparer.Ordinal);

            public void Dispose()
            {
            }

            public MediaInfoProbeCacheEntry? TryGet(string key, DateTime nowUtc)
            {
                if (_map.TryGetValue(key, out var e) && e.ExpiresAtUtc > nowUtc)
                {
                    return e;
                }

                return null;
            }

            public void Set(MediaInfoProbeCacheEntry entry)
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

        private static MediaInfo SampleMediaInfo()
        {
            return new MediaInfo
            {
                Container = "mkv",
                Bitrate = 8000000,
                MediaStreams = new List<MediaStream>
                {
                    new MediaStream { Codec = "hevc", Type = MediaStreamType.Video, Index = 0, Width = 1920, Height = 1080 },
                    new MediaStream { Codec = "truehd", Type = MediaStreamType.Audio, Index = 1 },
                    new MediaStream { Codec = "PGSSUB", Type = MediaStreamType.Subtitle, Index = 2 },
                },
            };
        }

        private static MediaInfoRequest HttpRequest(string url)
        {
            return new MediaInfoRequest
            {
                MediaSource = new MediaSourceInfo { Path = url, Protocol = MediaProtocol.Http },
            };
        }

        private static IMediaEncoder BuildEncoder(Mock<IMediaEncoder> mock, IMediaInfoProbeCacheStore store)
        {
            return CachingMediaEncoderProxy.Create(mock.Object, store, new NullLogger<CachingMediaEncoderProxy>());
        }

        [TestMethod]
        public void Policy_LocalFile_Passthrough_NoStore()
        {
            var store = new FakeMediaInfoStore();
            var mock = new Mock<IMediaEncoder>(MockBehavior.Strict);
            mock.Setup(m => m.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(SampleMediaInfo());
            var encoder = BuildEncoder(mock, store);

            var local = encoder.GetMediaInfo(
                new MediaInfoRequest { MediaSource = new MediaSourceInfo { Path = "/media/movie.mkv", Protocol = MediaProtocol.File } },
                CancellationToken.None).Result;
            Assert.AreEqual("mkv", local.Container);

            var empty = encoder.GetMediaInfo(new MediaInfoRequest(), CancellationToken.None).Result;
            Assert.AreEqual("mkv", empty.Container);

            Assert.AreEqual(0, store.Count);
            mock.Verify(m => m.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        }

        [TestMethod]
        public async Task Miss_CallsInner_Stores_And_Hit_Skips_Inner()
        {
            var store = new FakeMediaInfoStore();
            var mock = new Mock<IMediaEncoder>(MockBehavior.Strict);
            mock.Setup(m => m.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(SampleMediaInfo());
            var encoder = BuildEncoder(mock, store);

            var first = await encoder.GetMediaInfo(HttpRequest("https://pan.example.com/a.mkv?sign=1"), CancellationToken.None);
            Assert.AreEqual("mkv", first.Container);
            Assert.AreEqual(1, store.Count);

            // 第二次命中：inner 不再被调用，返回反序列化副本且内容一致
            var second = await encoder.GetMediaInfo(HttpRequest("https://pan.example.com/a.mkv?sign=1"), CancellationToken.None);
            Assert.AreEqual("mkv", second.Container);
            Assert.AreEqual(3, second.MediaStreams.Count);
            Assert.AreEqual("hevc", second.MediaStreams[0].Codec);
            Assert.AreEqual(1920, second.MediaStreams[0].Width);
            mock.Verify(m => m.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task InnerThrows_Propagates_And_Stores_Nothing()
        {
            var store = new FakeMediaInfoStore();
            var mock = new Mock<IMediaEncoder>(MockBehavior.Strict);
            mock.Setup(m => m.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("ffprobe boom"));
            var encoder = BuildEncoder(mock, store);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                encoder.GetMediaInfo(HttpRequest("https://pan.example.com/b.mkv"), CancellationToken.None));
            Assert.AreEqual(0, store.Count);
        }

        [TestMethod]
        public async Task ExpiredEntry_Reprobes()
        {
            var store = new FakeMediaInfoStore();
            var key = CachingMediaEncoderProxy.ComputeKey("https://pan.example.com/c.mkv");
            store.Set(new MediaInfoProbeCacheEntry
            {
                Key = key,
                Url = "https://pan.example.com/c.mkv",
                MediaInfoJson = "{\"Container\":\"mkv\"}",
                ProbedAtUtc = DateTime.UtcNow.AddDays(-8),
                ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1),
            });

            var mock = new Mock<IMediaEncoder>(MockBehavior.Strict);
            mock.Setup(m => m.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(SampleMediaInfo());
            var encoder = BuildEncoder(mock, store);

            var info = await encoder.GetMediaInfo(HttpRequest("https://pan.example.com/c.mkv"), CancellationToken.None);
            Assert.AreEqual("mkv", info.Container);
            mock.Verify(m => m.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [TestMethod]
        public async Task ConcurrentSameUrl_SingleFlights_Inner_Once()
        {
            var store = new FakeMediaInfoStore();
            var gate = new TaskCompletionSource<MediaInfo>();
            var mock = new Mock<IMediaEncoder>(MockBehavior.Strict);
            mock.Setup(m => m.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
                .Returns(gate.Task);
            var encoder = BuildEncoder(mock, store);

            var t1 = encoder.GetMediaInfo(HttpRequest("https://pan.example.com/d.mkv"), CancellationToken.None);
            var t2 = encoder.GetMediaInfo(HttpRequest("https://pan.example.com/d.mkv"), CancellationToken.None);
            await Task.Delay(100);
            gate.SetResult(SampleMediaInfo());
            await Task.WhenAll(t1, t2);

            mock.Verify(m => m.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.AreEqual(1, store.Count);
        }

        [TestMethod]
        public void Key_Stable_And_Url_Driven()
        {
            var a = CachingMediaEncoderProxy.ComputeKey("https://pan.example.com/e.mkv?sign=1");
            var b = CachingMediaEncoderProxy.ComputeKey("https://pan.example.com/e.mkv?sign=1");
            var c = CachingMediaEncoderProxy.ComputeKey("https://pan.example.com/e.mkv?sign=2");
            Assert.AreEqual(a, b);
            Assert.AreEqual(64, a.Length);
            Assert.AreNotEqual(a, c);
        }

        [TestMethod]
        public void SqliteStore_Roundtrip_Hit_Miss_Expired()
        {
            var db = Path.Combine(Path.GetTempPath(), "metashark-mif-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using var store = new SqliteMediaInfoProbeCacheStore(db, new NullLogger<SqliteMediaInfoProbeCacheStore>());
                var now = DateTime.UtcNow;
                Assert.IsNull(store.TryGet("nope", now));

                store.Set(new MediaInfoProbeCacheEntry
                {
                    Key = "k1",
                    Url = "https://pan.example.com/f.mkv",
                    MediaInfoJson = "{\"Container\":\"mkv\"}",
                    ProbedAtUtc = now,
                    ExpiresAtUtc = now.AddHours(1),
                });
                var hit = store.TryGet("k1", now);
                Assert.IsNotNull(hit);
                Assert.AreEqual("https://pan.example.com/f.mkv", hit!.Url);
                Assert.AreEqual("{\"Container\":\"mkv\"}", hit.MediaInfoJson);

                store.Set(new MediaInfoProbeCacheEntry
                {
                    Key = "k2",
                    Url = "https://pan.example.com/g.mkv",
                    MediaInfoJson = "{}",
                    ProbedAtUtc = now.AddHours(-2),
                    ExpiresAtUtc = now.AddSeconds(-1),
                });
                Assert.IsNull(store.TryGet("k2", now));
                Assert.AreEqual(0, store.RemoveExpired(now));
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
    }
}
