using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.Test
{
    /// <summary>
    /// MoviePilot 优先通道离线单测（全 mock，无真实 MoviePilot 实例；缺真机环境时以此为准）。
    /// 覆盖：启用决策、REST/MCP 类型映射、D1/M1 详情、M2/M3 搜索识别、D2/D4 阵容人物、
    /// 空返回（未命中/限流）退避重试与有界次数、失败回退语义、未配置零请求。
    /// </summary>
    [TestClass]
    public class MoviePilotTests
    {
        private const string BaseUrl = "http://mp-test:3001";
        private const string Token = "unit-test-token";

        private const string D1MovieJson = "{\"title\":\"肖申克的救赎\",\"original_title\":\"The Shawshank Redemption\",\"en_title\":\"The Shawshank Redemption\",\"type\":\"电影\",\"year\":1994,\"douban_id\":\"1292052\",\"imdb_id\":\"tt0111161\",\"overview\":\"银行家安迪被冤入狱。\",\"vote_average\":9.7,\"poster_path\":\"https://img1.doubanio.com/view/photo/s_ratio_poster/public/p123.jpg\",\"release_date\":\"1994-09-10\",\"detail_link\":\"https://movie.douban.com/subject/1292052/\",\"genres\":[{\"name\":\"剧情\"},{\"name\":\"犯罪\"}],\"directors\":[{\"name\":\"弗兰克·德拉邦特\"}],\"actors\":[{\"name\":\"蒂姆·罗宾斯\",\"character\":\"安迪\",\"avatar\":\"https://img1.doubanio.com/view/celebrity/s_ratio_celebrity/public/a.jpg\",\"url\":\"https://movie.douban.com/celebrity/123/\"}]}";

        private const string M1TvJson = "{\"title\":\"狂飙\",\"type\":\"电视剧\",\"year\":\"2023\",\"douban_id\":3286794,\"overview\":\"刑警与黑恶势力较量。\",\"vote_average\":\"8.5\",\"poster_path\":\"https://img9.doubanio.com/view/photo/s_ratio_poster/public/p456.jpg\",\"release_date\":\"2023-01-14\",\"genres\":[\"剧情\",\"犯罪\"],\"directors\":[\"徐纪周\"],\"actors\":[{\"name\":\"张译\",\"character\":\"安欣\"}]}";

        private const string M2SearchJson = "[{\"title\":\"肖申克的救赎\",\"type\":\"电影\",\"year\":1994,\"douban_id\":\"1292052\",\"vote_average\":9.7},{\"title\":\"肖申克的救赎2\",\"type\":\"电影\",\"year\":2020,\"douban_id\":\"9999999\",\"vote_average\":5.0}]";

        private const string D2CreditsJson = "[{\"id\":\"1040520\",\"name\":\"蒂姆·罗宾斯\",\"character\":\"安迪\",\"avatar\":\"https://img1.doubanio.com/view/celebrity/s_ratio_celebrity/public/a.jpg\",\"url\":\"https://movie.douban.com/celebrity/1040520/\",\"latin_name\":\"Tim Robbins\",\"roles\":[\"演员\"]},{\"id\":\"1054526\",\"name\":\"摩根·弗里曼\",\"character\":\"瑞德\",\"avatar\":\"https://img1.doubanio.com/view/celebrity/s_ratio_celebrity/public/b.jpg\"}]";

        private const string D4PersonJson = "{\"id\":\"1040520\",\"name\":\"蒂姆·罗宾斯\",\"original_name\":\"Tim Robbins\",\"latin_name\":\"Tim Robbins\",\"biography\":\"美国演员。\",\"birthday\":\"1958年10月16日\",\"place_of_birth\":\"美国加利福尼亚\",\"imdb_id\":\"nm0000209\",\"url\":\"https://movie.douban.com/celebrity/1040520/\",\"avatar\":\"https://img1.doubanio.com/view/celebrity/s_ratio_celebrity/public/a.jpg\",\"also_known_as\":[\"Tim Robbins\"]}";

        private const string M2PersonSearchJson = "[{\"id\":\"1040520\",\"name\":\"蒂姆·罗宾斯\",\"avatar\":\"https://img1.doubanio.com/view/celebrity/s_ratio_celebrity/public/a.jpg\"}]";

        private sealed class FakeHandler : HttpMessageHandler
        {
            public readonly List<HttpRequestMessage> Requests = new List<HttpRequestMessage>();

            public readonly Queue<HttpResponseMessage> Responses = new Queue<HttpResponseMessage>();

            public Func<HttpRequestMessage, HttpResponseMessage>? Responder;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                if (Responder != null)
                {
                    return Task.FromResult(Responder(request));
                }

                if (Responses.Count > 0)
                {
                    return Task.FromResult(Responses.Dequeue());
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }

        private static MoviePilotApi CreateApi(FakeHandler handler, bool enabled = true, string baseUrl = BaseUrl, string token = Token)
        {
            var api = new MoviePilotApi(LoggerFactory.Create(_ => { }), handler);
            api.TestConfigOverride = (enabled, baseUrl, token);
            api.DelayAsync = (_, _) => Task.CompletedTask;
            return api;
        }

        private static void EnqueueJson(FakeHandler handler, string json)
        {
            handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }

        // ---------- 启用决策与默认配置 ----------

        [TestMethod]
        public void Defaults_AreDisabledAndEmpty_NoSecretCommitted()
        {
            var config = new PluginConfiguration();
            Assert.IsFalse(config.EnableMoviePilot);
            Assert.AreEqual(string.Empty, config.MoviePilotBaseUrl);
            Assert.AreEqual(string.Empty, config.MoviePilotApiToken);
            Assert.IsFalse(MoviePilotApi.ShouldUse(config.EnableMoviePilot, config.MoviePilotBaseUrl, config.MoviePilotApiToken));
        }

        [TestMethod]
        [DataRow(false, BaseUrl, Token, false)]
        [DataRow(true, "", Token, false)]
        [DataRow(true, "   ", Token, false)]
        [DataRow(true, BaseUrl, "", false)]
        [DataRow(true, BaseUrl, "   ", false)]
        [DataRow(true, "not-a-url", Token, false)]
        [DataRow(true, "ftp://mp-test:3001", Token, false)]
        [DataRow(true, BaseUrl, Token, true)]
        [DataRow(true, BaseUrl + "/", Token, true)]
        public void ShouldUse_RequiresEnabledBaseUrlAndToken(bool enabled, string baseUrl, string token, bool expected)
        {
            Assert.AreEqual(expected, MoviePilotApi.ShouldUse(enabled, baseUrl, token));
        }

        [TestMethod]
        public async Task Unconfigured_MakesNoRequest_ReturnsNull()
        {
            var handler = new FakeHandler();
            using var api = CreateApi(handler, enabled: false);
            Assert.IsFalse(api.IsConfigured());

            Assert.IsNull(await api.GetDoubanDetailAsync("1292052", CancellationToken.None));
            Assert.AreEqual(0, (await api.SearchMediaAsync("肖申克", true, CancellationToken.None)).Count);
            Assert.AreEqual(0, handler.Requests.Count);
        }

        // ---------- 类型映射 ----------

        [TestMethod]
        public void RestTypeMapping_Roundtrips_AndRejectsEnglish()
        {
            Assert.AreEqual("电影", MoviePilotApi.ToRestTypeName(true));
            Assert.AreEqual("电视剧", MoviePilotApi.ToRestTypeName(false));
            Assert.IsTrue(MoviePilotApi.TryParseRestTypeName("电影", out var movie) && movie);
            Assert.IsTrue(MoviePilotApi.TryParseRestTypeName("电视剧", out var tv) && !tv);
            Assert.IsFalse(MoviePilotApi.TryParseRestTypeName("movie", out _));
            Assert.IsFalse(MoviePilotApi.TryParseRestTypeName("tv", out _));
            Assert.IsFalse(MoviePilotApi.TryParseRestTypeName(string.Empty, out _));
            Assert.IsFalse(MoviePilotApi.TryParseRestTypeName(null, out _));
        }

        [TestMethod]
        public void McpTypeMapping_Roundtrips_CaseInsensitive_AndRejectsChinese()
        {
            Assert.AreEqual("movie", MoviePilotApi.ToMcpTypeName(true));
            Assert.AreEqual("tv", MoviePilotApi.ToMcpTypeName(false));
            Assert.IsTrue(MoviePilotApi.TryParseMcpTypeName("movie", out var movie) && movie);
            Assert.IsTrue(MoviePilotApi.TryParseMcpTypeName("TV", out var tv) && !tv);
            Assert.IsTrue(MoviePilotApi.TryParseMcpTypeName("Movie", out _));
            Assert.IsFalse(MoviePilotApi.TryParseMcpTypeName("电影", out _));
            Assert.IsFalse(MoviePilotApi.TryParseMcpTypeName("电视剧", out _));
            Assert.IsFalse(MoviePilotApi.TryParseMcpTypeName(string.Empty, out _));
        }

        // ---------- D1/M1 详情 ----------

        [TestMethod]
        public async Task Detail_MapsD1Fields_AndSendsApiKeyHeader()
        {
            var handler = new FakeHandler();
            EnqueueJson(handler, D1MovieJson);
            using var api = CreateApi(handler);

            var subject = await api.GetDoubanDetailAsync("1292052", CancellationToken.None);

            Assert.IsNotNull(subject);
            Assert.AreEqual("1292052", subject!.Sid);
            Assert.AreEqual("肖申克的救赎", subject.Name);
            Assert.AreEqual("The Shawshank Redemption", subject.OriginalName);
            Assert.AreEqual(1994, subject.Year);
            Assert.AreEqual("tt0111161", subject.Imdb);
            Assert.AreEqual("电影", subject.Category);
            Assert.AreEqual("剧情 / 犯罪", subject.Genre);
            Assert.AreEqual("弗兰克·德拉邦特", subject.Director);
            Assert.AreEqual("1994-09-10", subject.Screen);
            Assert.IsTrue(subject.Rating > 9);

            Assert.AreEqual(1, handler.Requests.Count);
            var request = handler.Requests[0];
            Assert.IsTrue(request.RequestUri!.ToString().EndsWith("/api/v1/douban/1292052", StringComparison.Ordinal));
            Assert.IsTrue(request.Headers.Contains("X-API-KEY"));
            Assert.AreEqual(Token, string.Join(",", request.Headers.GetValues("X-API-KEY")));
            // secret 不进 URL
            Assert.IsFalse(request.RequestUri.ToString().Contains(Token, StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Detail_M1_UsesChineseTypeName()
        {
            var handler = new FakeHandler();
            EnqueueJson(handler, M1TvJson);
            using var api = CreateApi(handler);

            var subject = await api.GetMediaDetailAsync("3286794", false, CancellationToken.None);

            Assert.IsNotNull(subject);
            Assert.AreEqual("3286794", subject!.Sid);
            Assert.AreEqual("电视剧", subject.Category);
            Assert.AreEqual(2023, subject.Year);
            Assert.AreEqual(1, handler.Requests.Count);
            // OriginalString 保留百分号编码形态（Uri.ToString 会解码回中文，故不用它断言编码）
            var url = handler.Requests[0].RequestUri!.OriginalString;
            Assert.IsTrue(url.Contains("/api/v1/media/douban:3286794?", StringComparison.Ordinal));
            Assert.IsTrue(url.Contains(Uri.EscapeDataString("电视剧"), StringComparison.Ordinal));
            Assert.IsTrue(url.Contains("type_name=", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Detail_PrefersM1_FallsBackToD1()
        {
            var handler = new FakeHandler();
            handler.Responder = request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.ToString().Contains("/api/v1/media/", StringComparison.Ordinal) ? "{}" : D1MovieJson),
            };
            using var api = CreateApi(handler);
            api.MaxAttempts = 1;

            var subject = await api.GetDetailAsync("1292052", true, CancellationToken.None);

            Assert.IsNotNull(subject);
            Assert.AreEqual("1292052", subject!.Sid);
            Assert.AreEqual(2, handler.Requests.Count);
            Assert.IsTrue(handler.Requests[0].RequestUri!.ToString().Contains("/api/v1/media/", StringComparison.Ordinal));
            Assert.IsTrue(handler.Requests[1].RequestUri!.ToString().EndsWith("/api/v1/douban/1292052", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Detail_McpEnglishType_ToleratedAsCategory()
        {
            var handler = new FakeHandler();
            EnqueueJson(handler, "{\"title\":\"演示剧\",\"type\":\"tv\",\"year\":2024,\"douban_id\":\"111\"}");
            using var api = CreateApi(handler);

            var subject = await api.GetDoubanDetailAsync("111", CancellationToken.None);

            Assert.IsNotNull(subject);
            Assert.AreEqual("电视剧", subject!.Category);
        }

        [TestMethod]
        public async Task Detail_V2Envelope_Tolerated()
        {
            var handler = new FakeHandler();
            EnqueueJson(handler, "{\"success\":true,\"message\":\"\",\"data\":" + D1MovieJson + "}");
            using var api = CreateApi(handler);

            var subject = await api.GetDoubanDetailAsync("1292052", CancellationToken.None);

            Assert.IsNotNull(subject);
            Assert.AreEqual("1292052", subject!.Sid);
        }

        // ---------- 空返回 / 失败 / 重试 ----------

        [TestMethod]
        public async Task Detail_PersistentEmpty_ReturnsNull_WithBoundedRetries()
        {
            var handler = new FakeHandler();
            EnqueueJson(handler, "{}");
            EnqueueJson(handler, "{}");
            EnqueueJson(handler, "{}");
            EnqueueJson(handler, "{}");
            using var api = CreateApi(handler);

            var subject = await api.GetDoubanDetailAsync("1292052", CancellationToken.None);

            Assert.IsNull(subject);
            Assert.AreEqual(3, handler.Requests.Count);
        }

        [TestMethod]
        public async Task Detail_EmptyThenHit_RetriesAndReturns()
        {
            var handler = new FakeHandler();
            EnqueueJson(handler, "{}");
            EnqueueJson(handler, D1MovieJson);
            using var api = CreateApi(handler);
            var delays = 0;
            api.DelayAsync = (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            };

            var subject = await api.GetDoubanDetailAsync("1292052", CancellationToken.None);

            Assert.IsNotNull(subject);
            Assert.AreEqual(2, handler.Requests.Count);
            Assert.AreEqual(1, delays);
        }

        [TestMethod]
        public async Task Detail_AuthFailure_ReturnsNull_WithoutRetry()
        {
            var handler = new FakeHandler();
            handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(D1MovieJson) });
            using var api = CreateApi(handler);

            Assert.IsNull(await api.GetDoubanDetailAsync("1292052", CancellationToken.None));
            Assert.AreEqual(1, handler.Requests.Count);
        }

        [TestMethod]
        public async Task Detail_TransientThenHit_Retries()
        {
            var handler = new FakeHandler();
            handler.Responses.Enqueue(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            EnqueueJson(handler, D1MovieJson);
            using var api = CreateApi(handler);

            var subject = await api.GetDoubanDetailAsync("1292052", CancellationToken.None);

            Assert.IsNotNull(subject);
            Assert.AreEqual(2, handler.Requests.Count);
        }

        [TestMethod]
        public async Task Detail_NetworkError_ReturnsNull_ForFallback()
        {
            var handler = new FakeHandler();
            handler.Responder = _ => throw new HttpRequestException("boom");
            using var api = CreateApi(handler);

            Assert.IsNull(await api.GetDoubanDetailAsync("1292052", CancellationToken.None));
            Assert.AreEqual(3, handler.Requests.Count);
        }

        // ---------- M2/M3 搜索识别 ----------

        [TestMethod]
        public async Task Search_MapsM2List_AndScopesDoubanMedia()
        {
            var handler = new FakeHandler();
            EnqueueJson(handler, M2SearchJson);
            using var api = CreateApi(handler);

            var list = await api.SearchMediaAsync("肖申克", true, CancellationToken.None);

            Assert.AreEqual(2, list.Count);
            Assert.AreEqual("1292052", list[0].Sid);
            Assert.AreEqual("电影", list[0].Category);
            var url = handler.Requests[0].RequestUri!.ToString();
            Assert.IsTrue(url.Contains("/api/v1/media/search?", StringComparison.Ordinal));
            Assert.IsTrue(url.Contains("type=media", StringComparison.Ordinal));
            Assert.IsTrue(url.Contains("source=douban", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Search_EmptyArray_ReturnsEmpty_WithBoundedRetries()
        {
            var handler = new FakeHandler();
            EnqueueJson(handler, "[]");
            EnqueueJson(handler, "[]");
            EnqueueJson(handler, "[]");
            using var api = CreateApi(handler);

            var list = await api.SearchMediaAsync("不存在的片名xyz", true, CancellationToken.None);

            Assert.AreEqual(0, list.Count);
            Assert.AreEqual(3, handler.Requests.Count);
        }

        [TestMethod]
        public async Task Recognize_ReturnsFirstHit()
        {
            var handler = new FakeHandler();
            EnqueueJson(handler, D1MovieJson);
            using var api = CreateApi(handler);

            var subject = await api.RecognizeAsync("Shawshank Redemption 1994 BluRay", true, CancellationToken.None);

            Assert.IsNotNull(subject);
            Assert.AreEqual("1292052", subject!.Sid);
            Assert.IsTrue(handler.Requests[0].RequestUri!.ToString().Contains("/api/v1/media/recognize?", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Recognize_ContextEnvelope_Unwrapped()
        {
            var handler = new FakeHandler();
            EnqueueJson(handler, "{\"media_info\":" + D1MovieJson + ",\"torrent_info\":{}}");
            using var api = CreateApi(handler);

            var subject = await api.RecognizeAsync("Shawshank 1994", null, CancellationToken.None);

            Assert.IsNotNull(subject);
            Assert.AreEqual("1292052", subject!.Sid);
        }

        // ---------- D2/D4 阵容人物 ----------

        [TestMethod]
        public async Task Credits_MapsD2_AndUsesChineseTypeName()
        {
            var handler = new FakeHandler();
            EnqueueJson(handler, D2CreditsJson);
            using var api = CreateApi(handler);

            var list = await api.GetCreditsAsync("1292052", true, CancellationToken.None);

            Assert.AreEqual(2, list.Count);
            Assert.AreEqual("蒂姆·罗宾斯", list[0].Name);
            Assert.AreEqual("安迪", list[0].Role);
            Assert.IsTrue(list[0].Img.StartsWith("https://img1.doubanio.com", StringComparison.Ordinal));
            var url = handler.Requests[0].RequestUri!.OriginalString;
            Assert.IsTrue(url.Contains("/api/v1/douban/credits/1292052/", StringComparison.Ordinal));
            Assert.IsTrue(url.Contains(Uri.EscapeDataString("电影"), StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Person_MapsD4Fields()
        {
            var handler = new FakeHandler();
            EnqueueJson(handler, D4PersonJson);
            using var api = CreateApi(handler);

            var person = await api.GetPersonAsync("1040520", CancellationToken.None);

            Assert.IsNotNull(person);
            Assert.AreEqual("1040520", person!.Id);
            Assert.AreEqual("蒂姆·罗宾斯", person.Name);
            Assert.AreEqual("美国加利福尼亚", person.Birthplace);
            Assert.AreEqual("nm0000209", person.Imdb);
            Assert.AreEqual("https://movie.douban.com/celebrity/1040520/", person.Site);
            Assert.IsTrue(handler.Requests[0].RequestUri!.ToString().EndsWith("/api/v1/douban/person/1040520", StringComparison.Ordinal));
        }

        [TestMethod]
        public async Task Person_EmptyObject_ReturnsNull_WithBoundedRetries()
        {
            var handler = new FakeHandler();
            EnqueueJson(handler, "{}");
            EnqueueJson(handler, "{}");
            EnqueueJson(handler, "{}");
            using var api = CreateApi(handler);

            Assert.IsNull(await api.GetPersonAsync("0", CancellationToken.None));
            Assert.AreEqual(3, handler.Requests.Count);
        }

        [TestMethod]
        public async Task SearchPerson_UsesPersonType()
        {
            var handler = new FakeHandler();
            EnqueueJson(handler, M2PersonSearchJson);
            using var api = CreateApi(handler);

            var list = await api.SearchPersonAsync("蒂姆", CancellationToken.None);

            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("1040520", list[0].Id);
            Assert.IsTrue(handler.Requests[0].RequestUri!.ToString().Contains("type=person", StringComparison.Ordinal));
        }
    }
}
