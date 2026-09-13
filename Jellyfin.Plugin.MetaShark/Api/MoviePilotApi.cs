using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaShark.Model;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.Api
{
    /// <summary>
    /// MoviePilot 豆瓣数据优先通道客户端（独立于 <see cref="DoubanApi"/> 直连抓取）。
    /// 接口依据：仓库内 moviepilot-douban-openapi-for-llm.md（REST /api/v1，鉴权 X-API-KEY）。
    /// 未启用或未配置时调用方应直接走原有 DoubanApi 链路，本类不改变 DoubanApi 的任何行为。
    /// 空返回（[] / 空对象）按“未命中或被限流”语义处理：有限退避重试，不紧循环；耗尽后返回 null/空列表由调用方回退直连。
    /// </summary>
    public class MoviePilotApi : IDisposable
    {
        public const string RestMovieType = "电影";
        public const string RestTvType = "电视剧";
        public const string McpMovieType = "movie";
        public const string McpTvType = "tv";

        private const int DefaultMaxAttempts = 3;

        private readonly ILogger<MoviePilotApi> _logger;
        private readonly HttpClient _httpClient;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        /// <summary>
        /// 退避等待实现，单测可替换为无等待实现以保持离线确定性。
        /// </summary>
        internal Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

        internal int MaxAttempts { get; set; } = DefaultMaxAttempts;

        /// <summary>
        /// 单测注入配置（启用/地址/token），为 null 时读 Jellyfin 运行时插件配置。
        /// </summary>
        internal (bool Enabled, string? BaseUrl, string? ApiToken)? TestConfigOverride { get; set; }

        public MoviePilotApi(ILoggerFactory loggerFactory)
            : this(loggerFactory, new HttpClientHandler())
        {
        }

        public MoviePilotApi(ILoggerFactory loggerFactory, HttpMessageHandler handler)
        {
            _logger = loggerFactory.CreateLogger<MoviePilotApi>();
            _httpClient = new HttpClient(handler);
            _httpClient.Timeout = TimeSpan.FromSeconds(15);
        }

        /// <summary>
        /// REST 中文类型名（D1/D2/M1/M2/M3 均要求“电影/电视剧”，传 movie 会 422）。
        /// </summary>
        public static string ToRestTypeName(bool isMovie)
        {
            return isMovie ? RestMovieType : RestTvType;
        }

        /// <summary>
        /// MCP/Agent 英文类型名（query_media_detail 等要求 movie/tv）。
        /// </summary>
        public static string ToMcpTypeName(bool isMovie)
        {
            return isMovie ? McpMovieType : McpTvType;
        }

        public static bool TryParseRestTypeName(string? value, out bool isMovie)
        {
            if (string.Equals(value?.Trim(), RestMovieType, StringComparison.Ordinal))
            {
                isMovie = true;
                return true;
            }

            if (string.Equals(value?.Trim(), RestTvType, StringComparison.Ordinal))
            {
                isMovie = false;
                return true;
            }

            isMovie = false;
            return false;
        }

        public static bool TryParseMcpTypeName(string? value, out bool isMovie)
        {
            if (string.Equals(value?.Trim(), McpMovieType, StringComparison.OrdinalIgnoreCase))
            {
                isMovie = true;
                return true;
            }

            if (string.Equals(value?.Trim(), McpTvType, StringComparison.OrdinalIgnoreCase))
            {
                isMovie = false;
                return true;
            }

            isMovie = false;
            return false;
        }

        /// <summary>
        /// 是否启用且已配置（开关开 + BaseURL 合法 + Token 非空）。否则调用方走原链路。
        /// </summary>
        public bool IsConfigured()
        {
            if (TestConfigOverride.HasValue)
            {
                var test = TestConfigOverride.Value;
                return ShouldUse(test.Enabled, test.BaseUrl, test.ApiToken);
            }

            var config = Plugin.Instance?.Configuration;
            return ShouldUse(config?.EnableMoviePilot == true, config?.MoviePilotBaseUrl, config?.MoviePilotApiToken);
        }

        /// <summary>
        /// 纯决策函数：是否应走 MoviePilot 通道（可单测，不触碰网络与 secret）。
        /// </summary>
        public static bool ShouldUse(bool enabled, string? baseUrl, string? apiToken)
        {
            return enabled
                && NormalizeBaseUrl(baseUrl) != null
                && !string.IsNullOrWhiteSpace(apiToken);
        }

        internal static string? NormalizeBaseUrl(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var trimmed = raw.Trim().TrimEnd('/');
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                return null;
            }

            if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return trimmed;
        }

        private bool TryBuildClient(out string baseUrl, out string apiToken)
        {
            bool enabled;
            string? rawBase;
            string? rawToken;
            if (TestConfigOverride.HasValue)
            {
                enabled = TestConfigOverride.Value.Enabled;
                rawBase = TestConfigOverride.Value.BaseUrl;
                rawToken = TestConfigOverride.Value.ApiToken;
            }
            else
            {
                var config = Plugin.Instance?.Configuration;
                enabled = config?.EnableMoviePilot == true;
                rawBase = config?.MoviePilotBaseUrl;
                rawToken = config?.MoviePilotApiToken;
            }

            baseUrl = NormalizeBaseUrl(rawBase) ?? string.Empty;
            apiToken = rawToken?.Trim() ?? string.Empty;
            return ShouldUse(enabled, baseUrl, apiToken);
        }

        /// <summary>
        /// 详情（M1 优先，失败再试 D1）：M1 含跨源 ID 补全与补图，D1 为豆瓣原生荷载。
        /// </summary>
        public async Task<DoubanSubject?> GetDetailAsync(string doubanId, bool isMovie, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(doubanId))
            {
                return null;
            }

            var viaMedia = await GetMediaDetailAsync(doubanId, isMovie, cancellationToken).ConfigureAwait(false);
            if (viaMedia != null)
            {
                return viaMedia;
            }

            return await GetDoubanDetailAsync(doubanId, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// D1 豆瓣详情。
        /// </summary>
        public async Task<DoubanSubject?> GetDoubanDetailAsync(string doubanId, CancellationToken cancellationToken)
        {
            if (!TryBuildClient(out var baseUrl, out _))
            {
                return null;
            }

            var url = $"{baseUrl}/api/v1/douban/{Uri.EscapeDataString(doubanId.Trim())}";
            return await FetchAndParseAsync(
                url,
                body => ParseMediaInfoObject(body, null),
                subject => subject == null,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// M1 通用详情（必须带中文 type_name）。
        /// </summary>
        public async Task<DoubanSubject?> GetMediaDetailAsync(string doubanId, bool isMovie, CancellationToken cancellationToken)
        {
            if (!TryBuildClient(out var baseUrl, out _))
            {
                return null;
            }

            var url = $"{baseUrl}/api/v1/media/douban:{Uri.EscapeDataString(doubanId.Trim())}?type_name={Uri.EscapeDataString(ToRestTypeName(isMovie))}";
            return await FetchAndParseAsync(
                url,
                body => ParseMediaInfoObject(body, isMovie),
                subject => subject == null,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// M2 按名搜索（source=douban 限定豆瓣）。
        /// </summary>
        public async Task<List<DoubanSubject>> SearchMediaAsync(string keyword, bool? isMovie, CancellationToken cancellationToken)
        {
            if (!TryBuildClient(out var baseUrl, out _) || string.IsNullOrWhiteSpace(keyword))
            {
                return new List<DoubanSubject>();
            }

            var url = $"{baseUrl}/api/v1/media/search?title={Uri.EscapeDataString(keyword.Trim())}&type=media&page=1&count=5&source=douban";
            var list = await FetchAndParseAsync(
                url,
                body => ParseMediaInfoList(body, isMovie),
                l => l == null || l.Count == 0,
                cancellationToken).ConfigureAwait(false);
            return list ?? new List<DoubanSubject>();
        }

        /// <summary>
        /// M3 标题识别（种子名/文件名），返回首个命中。
        /// </summary>
        public async Task<DoubanSubject?> RecognizeAsync(string title, bool? isMovie, CancellationToken cancellationToken)
        {
            if (!TryBuildClient(out var baseUrl, out _) || string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var url = $"{baseUrl}/api/v1/media/recognize?title={Uri.EscapeDataString(title.Trim())}&source=douban";
            var list = await FetchAndParseAsync(
                url,
                body => ParseMediaInfoList(body, isMovie),
                l => l == null || l.Count == 0,
                cancellationToken).ConfigureAwait(false);
            return list?.FirstOrDefault();
        }

        /// <summary>
        /// D2 演员阵容（type_name 必填中文）。
        /// </summary>
        public async Task<List<DoubanCelebrity>> GetCreditsAsync(string doubanId, bool isMovie, CancellationToken cancellationToken)
        {
            if (!TryBuildClient(out var baseUrl, out _) || string.IsNullOrWhiteSpace(doubanId))
            {
                return new List<DoubanCelebrity>();
            }

            var url = $"{baseUrl}/api/v1/douban/credits/{Uri.EscapeDataString(doubanId.Trim())}/{Uri.EscapeDataString(ToRestTypeName(isMovie))}";
            var list = await FetchAndParseAsync(
                url,
                ParseMediaPersonList,
                l => l == null || l.Count == 0,
                cancellationToken).ConfigureAwait(false);
            return list ?? new List<DoubanCelebrity>();
        }

        /// <summary>
        /// D4 人物详情。
        /// </summary>
        public async Task<DoubanCelebrity?> GetPersonAsync(string personId, CancellationToken cancellationToken)
        {
            if (!TryBuildClient(out var baseUrl, out _) || string.IsNullOrWhiteSpace(personId))
            {
                return null;
            }

            var url = $"{baseUrl}/api/v1/douban/person/{Uri.EscapeDataString(personId.Trim())}";
            return await FetchAndParseAsync(
                url,
                ParseMediaPersonObject,
                person => person == null,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// M2 人物搜索（type=person）。
        /// </summary>
        public async Task<List<DoubanCelebrity>> SearchPersonAsync(string keyword, CancellationToken cancellationToken)
        {
            if (!TryBuildClient(out var baseUrl, out _) || string.IsNullOrWhiteSpace(keyword))
            {
                return new List<DoubanCelebrity>();
            }

            var url = $"{baseUrl}/api/v1/media/search?title={Uri.EscapeDataString(keyword.Trim())}&type=person&page=1&count=5&source=douban";
            var list = await FetchAndParseAsync(
                url,
                ParseMediaPersonList,
                l => l == null || l.Count == 0,
                cancellationToken).ConfigureAwait(false);
            return list ?? new List<DoubanCelebrity>();
        }

        private async Task<T?> FetchAndParseAsync<T>(
            string url,
            Func<string, T?> parse,
            Func<T?, bool> isEmpty,
            CancellationToken cancellationToken)
            where T : class
        {
            var attempts = Math.Max(1, MaxAttempts);
            for (var attempt = 0; ; attempt++)
            {
                var fetch = await TryFetchOnceAsync(url, cancellationToken).ConfigureAwait(false);
                T? value = null;
                var parsed = false;
                if (fetch.Body != null)
                {
                    try
                    {
                        value = parse(fetch.Body);
                        parsed = true;
                    }
                    catch (JsonException ex)
                    {
                        // 非 JSON/结构损坏：重试无意义，直接按未命中处理，由调用方回退直连。
                        _logger.LogDebug(ex, "[MetaShark] MoviePilot 返回解析失败");
                        return null;
                    }
                }

                if (parsed && !isEmpty(value))
                {
                    return value;
                }

                // 空返回 = 未命中或被限流：退避后有限重试，不紧循环。
                var retryable = fetch.Body != null ? true : fetch.ShouldRetry;
                if (!retryable || attempt + 1 >= attempts)
                {
                    return value;
                }

                _logger.LogDebug("[MetaShark] MoviePilot 空返回，第 {Attempt}/{Max} 次退避重试", attempt + 1, attempts);
                await DelayAsync(TimeSpan.FromMilliseconds(400 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }

        private sealed class FetchOutcome
        {
            public string? Body { get; init; }

            public bool ShouldRetry { get; init; }
        }

        private async Task<FetchOutcome> TryFetchOnceAsync(string url, CancellationToken cancellationToken)
        {
            if (!TryBuildClient(out _, out var apiToken))
            {
                return new FetchOutcome { ShouldRetry = false };
            }

            HttpResponseMessage response;
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Add("X-API-KEY", apiToken);
                    response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("MoviePilot 请求被取消", ex, cancellationToken);
            }
            catch (Exception ex)
            {
                // 网络抖动/超时：可重试。
                _logger.LogDebug(ex, "[MetaShark] MoviePilot 请求异常");
                return new FetchOutcome { ShouldRetry = true };
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    // token 错误：重试无意义，不打循环，直接回退直连。
                    _logger.LogWarning("[MetaShark] MoviePilot 鉴权失败（401/403），请检查 API_TOKEN 配置");
                    return new FetchOutcome { ShouldRetry = false };
                }

                if ((int)response.StatusCode == 422)
                {
                    _logger.LogWarning("[MetaShark] MoviePilot 参数错误（422），请检查类型映射");
                    return new FetchOutcome { ShouldRetry = false };
                }

                if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                {
                    _logger.LogDebug("[MetaShark] MoviePilot 服务端限流/异常 {StatusCode}", (int)response.StatusCode);
                    return new FetchOutcome { ShouldRetry = true };
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new FetchOutcome { ShouldRetry = false };
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(body))
                {
                    return new FetchOutcome { ShouldRetry = true };
                }

                return new FetchOutcome { Body = body };
            }
        }

        private DoubanSubject? ParseMediaInfoObject(string body, bool? isMovieFallback)
        {
            using (var doc = JsonDocument.Parse(body))
            {
                var root = UnwrapEnvelope(doc.RootElement);
                if (root.ValueKind == JsonValueKind.Array)
                {
                    var first = root.EnumerateArray().FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Undefined || first.ValueKind == JsonValueKind.Null)
                    {
                        return null;
                    }

                    return MapMediaInfo(Deserialize<MediaInfoDto>(first), isMovieFallback);
                }

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                // M3 识别接口可能返回 Context 包络 {media_info: {...}}。
                if (root.TryGetProperty("media_info", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
                {
                    return MapMediaInfo(Deserialize<MediaInfoDto>(wrapped), isMovieFallback);
                }

                var dto = Deserialize<MediaInfoDto>(root);
                var subject = MapMediaInfo(dto, isMovieFallback);
                return subject != null && IsEmptySubject(subject) ? null : subject;
            }
        }

        private List<DoubanSubject> ParseMediaInfoList(string body, bool? isMovieFallback)
        {
            using (var doc = JsonDocument.Parse(body))
            {
                var root = UnwrapEnvelope(doc.RootElement);
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("media_info", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
                    {
                        var single = MapMediaInfo(Deserialize<MediaInfoDto>(wrapped), isMovieFallback);
                        return single == null || IsEmptySubject(single) ? new List<DoubanSubject>() : new List<DoubanSubject> { single };
                    }

                    var obj = MapMediaInfo(Deserialize<MediaInfoDto>(root), isMovieFallback);
                    return obj == null || IsEmptySubject(obj) ? new List<DoubanSubject>() : new List<DoubanSubject> { obj };
                }

                if (root.ValueKind != JsonValueKind.Array)
                {
                    return new List<DoubanSubject>();
                }

                var list = new List<DoubanSubject>();
                foreach (var element in root.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var subject = MapMediaInfo(Deserialize<MediaInfoDto>(element), isMovieFallback);
                    if (subject != null && !IsEmptySubject(subject))
                    {
                        list.Add(subject);
                    }
                }

                return list;
            }
        }

        private List<DoubanCelebrity> ParseMediaPersonList(string body)
        {
            using (var doc = JsonDocument.Parse(body))
            {
                var root = UnwrapEnvelope(doc.RootElement);
                if (root.ValueKind == JsonValueKind.Object)
                {
                    var single = MapMediaPerson(Deserialize<MediaPersonDto>(root));
                    return single == null || string.IsNullOrEmpty(single.Name) ? new List<DoubanCelebrity>() : new List<DoubanCelebrity> { single };
                }

                if (root.ValueKind != JsonValueKind.Array)
                {
                    return new List<DoubanCelebrity>();
                }

                var list = new List<DoubanCelebrity>();
                foreach (var element in root.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var person = MapMediaPerson(Deserialize<MediaPersonDto>(element));
                    if (person != null && !string.IsNullOrEmpty(person.Name))
                    {
                        list.Add(person);
                    }
                }

                return list;
            }
        }

        private DoubanCelebrity? ParseMediaPersonObject(string body)
        {
            using (var doc = JsonDocument.Parse(body))
            {
                var root = UnwrapEnvelope(doc.RootElement);
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var person = MapMediaPerson(Deserialize<MediaPersonDto>(root));
                return person != null && !string.IsNullOrEmpty(person.Name) ? person : null;
            }
        }

        /// <summary>
        /// 兼容 v2 成功包络 {"success":true,"data":...}：仅当根对象不含业务字段时下钻。
        /// </summary>
        private static JsonElement UnwrapEnvelope(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out var data)
                && !root.TryGetProperty("title", out _)
                && !root.TryGetProperty("douban_id", out _)
                && !root.TryGetProperty("name", out _)
                && !root.TryGetProperty("media_info", out _))
            {
                return data;
            }

            return root;
        }

        private T? Deserialize<T>(JsonElement element)
            where T : class
        {
            var raw = element.GetRawText();
            return JsonSerializer.Deserialize<T>(raw, _jsonOptions);
        }

        private static bool IsEmptySubject(DoubanSubject? subject)
        {
            return subject == null
                || (string.IsNullOrWhiteSpace(subject.Name)
                    && string.IsNullOrWhiteSpace(subject.Sid)
                    && string.IsNullOrWhiteSpace(subject.Imdb));
        }

        private static DoubanSubject? MapMediaInfo(MediaInfoDto? dto, bool? isMovieFallback)
        {
            if (dto == null)
            {
                return null;
            }

            var sid = GetString(dto.DoubanId);
            var name = dto.Title ?? dto.OriginalTitle ?? dto.EnTitle ?? string.Empty;
            var subject = new DoubanSubject
            {
                Sid = sid,
                Name = name.Trim(),
                OriginalName = (dto.OriginalTitle ?? dto.EnTitle ?? name ?? string.Empty).Trim(),
                Rating = GetFloat(dto.VoteAverage),
                Img = dto.PosterPath ?? string.Empty,
                Year = GetInt(dto.Year),
                Genre = JoinNames(dto.Genres),
                Category = MapCategory(dto.Type, isMovieFallback),
                Director = JoinNames(dto.Directors),
                Actor = JoinNames(dto.Actors),
                Country = string.Empty,
                Language = string.Empty,
                Duration = string.Empty,
                Subname = string.Empty,
                Writer = string.Empty,
                Screen = dto.ReleaseDate ?? string.Empty,
                Site = dto.DetailLink ?? string.Empty,
                Imdb = GetString(dto.ImdbId),
                Intro = dto.Overview ?? string.Empty,
                Celebrities = new List<DoubanCelebrity>(),
            };

            if (dto.Directors != null)
            {
                foreach (var element in dto.Directors)
                {
                    var directorName = GetName(element);
                    if (string.IsNullOrEmpty(directorName))
                    {
                        continue;
                    }

                    subject.Celebrities.Add(new DoubanCelebrity
                    {
                        Name = directorName,
                        Role = "导演",
                        RoleType = "导演",
                        Img = GetPropertyString(element, "avatar") ?? GetPropertyString(element, "profile_path") ?? string.Empty,
                        Id = GetPropertyString(element, "id") ?? string.Empty,
                    });
                }
            }

            if (dto.Actors != null)
            {
                foreach (var element in dto.Actors)
                {
                    var actorName = GetName(element);
                    if (string.IsNullOrEmpty(actorName))
                    {
                        continue;
                    }

                    subject.Celebrities.Add(new DoubanCelebrity
                    {
                        Name = actorName,
                        Role = GetPropertyString(element, "character") ?? string.Empty,
                        RoleType = "演员",
                        Img = GetPropertyString(element, "avatar") ?? GetPropertyString(element, "profile_path") ?? string.Empty,
                        Id = string.Empty,
                    });
                }
            }

            return subject;
        }

        private static string MapCategory(string? mediaType, bool? isMovieFallback)
        {
            if (TryParseRestTypeName(mediaType, out var restIsMovie))
            {
                return ToRestTypeName(restIsMovie);
            }

            if (TryParseMcpTypeName(mediaType, out var mcpIsMovie))
            {
                return ToRestTypeName(mcpIsMovie);
            }

            return isMovieFallback.HasValue ? ToRestTypeName(isMovieFallback.Value) : (mediaType ?? string.Empty);
        }

        private static DoubanCelebrity? MapMediaPerson(MediaPersonDto? dto)
        {
            if (dto == null)
            {
                return null;
            }

            var roles = GetStringArray(dto.Roles);
            var alsoKnownAs = GetStringArray(dto.AlsoKnownAs);
            return new DoubanCelebrity
            {
                Id = GetString(dto.Id),
                Name = (dto.Name ?? string.Empty).Trim(),
                Img = dto.Avatar ?? dto.ProfilePath ?? string.Empty,
                Role = dto.Character ?? (roles.Length > 0 ? string.Join(" / ", roles) : string.Empty),
                RoleType = string.Empty,
                Intro = dto.Biography ?? string.Empty,
                Gender = string.Empty,
                Constellation = string.Empty,
                Birthdate = dto.Birthday ?? string.Empty,
                Enddate = dto.Deathday ?? string.Empty,
                Birthplace = dto.PlaceOfBirth ?? string.Empty,
                NickName = alsoKnownAs.Length > 0 ? string.Join(" / ", alsoKnownAs) : string.Empty,
                EnglishName = dto.OriginalName ?? dto.LatinName ?? string.Empty,
                Imdb = GetString(dto.ImdbId),
                Site = dto.Url ?? string.Empty,
            };
        }

        private static string JoinNames(List<JsonElement>? elements)
        {
            if (elements == null || elements.Count == 0)
            {
                return string.Empty;
            }

            return string.Join(" / ", elements.Select(GetName).Where(x => !string.IsNullOrEmpty(x)));
        }

        private static string GetName(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                return element.GetString()?.Trim() ?? string.Empty;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                return GetPropertyString(element, "name")?.Trim() ?? string.Empty;
            }

            return string.Empty;
        }

        private static string? GetPropertyString(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!element.TryGetProperty(propertyName, out var value))
            {
                return null;
            }

            return GetString(value);
        }

        private static string GetString(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    return element.GetString()?.Trim() ?? string.Empty;
                case JsonValueKind.Number:
                    return element.GetRawText().Trim();
                default:
                    return string.Empty;
            }
        }

        private static string[] GetStringArray(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var single = element.GetString()?.Trim();
                return string.IsNullOrEmpty(single) ? Array.Empty<string>() : new[] { single };
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in element.EnumerateArray())
                {
                    var text = GetString(item);
                    if (!string.IsNullOrEmpty(text))
                    {
                        list.Add(text);
                    }
                }

                return list.ToArray();
            }

            return Array.Empty<string>();
        }

        private static int GetInt(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
            {
                return number;
            }

            var text = GetString(element);
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            // 年份可能是 "2001(美国)" 之类，取首个四位年份。
            var match = System.Text.RegularExpressions.Regex.Match(text, @"([12][890][0-9][0-9])");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var year))
            {
                return year;
            }

            return 0;
        }

        private static float GetFloat(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number))
            {
                return (float)number;
            }

            var text = GetString(element);
            if (float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            return 0;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _httpClient.Dispose();
            }
        }

        private sealed class MediaInfoDto
        {
            [JsonPropertyName("title")]
            public string? Title { get; set; }

            [JsonPropertyName("en_title")]
            public string? EnTitle { get; set; }

            [JsonPropertyName("original_title")]
            public string? OriginalTitle { get; set; }

            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("year")]
            public JsonElement Year { get; set; }

            [JsonPropertyName("douban_id")]
            public JsonElement DoubanId { get; set; }

            [JsonPropertyName("tmdb_id")]
            public JsonElement TmdbId { get; set; }

            [JsonPropertyName("imdb_id")]
            public JsonElement ImdbId { get; set; }

            [JsonPropertyName("overview")]
            public string? Overview { get; set; }

            [JsonPropertyName("vote_average")]
            public JsonElement VoteAverage { get; set; }

            [JsonPropertyName("poster_path")]
            public string? PosterPath { get; set; }

            [JsonPropertyName("backdrop_path")]
            public string? BackdropPath { get; set; }

            [JsonPropertyName("release_date")]
            public string? ReleaseDate { get; set; }

            [JsonPropertyName("detail_link")]
            public string? DetailLink { get; set; }

            [JsonPropertyName("genres")]
            public List<JsonElement>? Genres { get; set; }

            [JsonPropertyName("directors")]
            public List<JsonElement>? Directors { get; set; }

            [JsonPropertyName("actors")]
            public List<JsonElement>? Actors { get; set; }
        }

        private sealed class MediaPersonDto
        {
            [JsonPropertyName("id")]
            public JsonElement Id { get; set; }

            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("character")]
            public string? Character { get; set; }

            [JsonPropertyName("type")]
            public string? Type { get; set; }

            [JsonPropertyName("profile_path")]
            public string? ProfilePath { get; set; }

            [JsonPropertyName("original_name")]
            public string? OriginalName { get; set; }

            [JsonPropertyName("biography")]
            public string? Biography { get; set; }

            [JsonPropertyName("birthday")]
            public string? Birthday { get; set; }

            [JsonPropertyName("deathday")]
            public string? Deathday { get; set; }

            [JsonPropertyName("place_of_birth")]
            public string? PlaceOfBirth { get; set; }

            [JsonPropertyName("imdb_id")]
            public JsonElement ImdbId { get; set; }

            [JsonPropertyName("also_known_as")]
            public JsonElement AlsoKnownAs { get; set; }

            [JsonPropertyName("url")]
            public string? Url { get; set; }

            [JsonPropertyName("avatar")]
            public string? Avatar { get; set; }

            [JsonPropertyName("latin_name")]
            public string? LatinName { get; set; }

            [JsonPropertyName("roles")]
            public JsonElement Roles { get; set; }

            [JsonPropertyName("title")]
            public string? Title { get; set; }
        }
    }
}
