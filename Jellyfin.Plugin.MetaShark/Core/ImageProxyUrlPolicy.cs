using System;
using System.Web;
using Jellyfin.Plugin.MetaShark.Api;

namespace Jellyfin.Plugin.MetaShark.Core
{
    /// <summary>
    /// 图片地址构造策略。
    /// 只有豆瓣图床的图片才走插件的图片代理端点（该端点的白名单只放行 doubanio.com），
    /// 其它来源（例如 MoviePilot 返回的 TMDB 图片）直接使用原始地址，
    /// 否则会被代理端点判定为非法地址返回 404，导致识别/演员缩略图空白。
    /// </summary>
    public static class ImageProxyUrlPolicy
    {
        /// <summary>
        /// TMDB 图床基地址，用于补全 TMDB 相对图片路径（如 /xxxx.jpg）。
        /// </summary>
        public const string TmdbImageBaseUrl = "https://image.tmdb.org/t/p/original";

        /// <summary>
        /// 构造图片访问地址。
        /// </summary>
        /// <param name="baseUrl">插件图片代理端点的基地址（通常为 Jellyfin 自身地址或配置的图片代理地址）。</param>
        /// <param name="url">原始图片地址，允许为空。</param>
        /// <returns>豆瓣图床返回代理地址，其它来源返回原始（或补全后的）地址，空地址返回 <see cref="string.Empty"/>。</returns>
        public static string Build(string baseUrl, string? url)
        {
            var imageUrl = Normalize(url);
            if (string.IsNullOrEmpty(imageUrl))
            {
                return string.Empty;
            }

            // 非豆瓣图床不套代理，避免代理端点的豆瓣白名单校验直接 404
            if (!DoubanApi.IsDoubanImageUrl(imageUrl))
            {
                return imageUrl;
            }

            var encodedUrl = HttpUtility.UrlEncode(imageUrl);
            return $"{baseUrl}/plugin/metashark/proxy/image/?url={encodedUrl}";
        }

        /// <summary>
        /// 归一化图片地址：补全 TMDB 相对路径与协议相对地址，其余原样返回。
        /// </summary>
        /// <param name="url">原始图片地址，允许为空。</param>
        /// <returns>归一化后的地址，空地址返回 <see cref="string.Empty"/>。</returns>
        public static string Normalize(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            var imageUrl = url.Trim();

            // 协议相对地址：//image.tmdb.org/t/p/...
            if (imageUrl.StartsWith("//", StringComparison.Ordinal))
            {
                return $"https:{imageUrl}";
            }

            // TMDB 相对路径：/xxxx.jpg
            if (imageUrl.StartsWith("/", StringComparison.Ordinal))
            {
                return TmdbImageBaseUrl + imageUrl;
            }

            return imageUrl;
        }
    }
}
