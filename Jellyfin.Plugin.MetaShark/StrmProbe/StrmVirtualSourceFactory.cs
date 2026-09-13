using System;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// 虚拟直连 MediaSource 构造器（纯构造逻辑，便于测试；仅新增虚拟源，不改媒体库数据）。
/// </summary>
public static class StrmVirtualSourceFactory
{
    /// <summary>
    /// 构造虚拟直连源。
    /// </summary>
    /// <param name="key">缓存 key（写入 ETag，并派生确定性 Guid Id）。</param>
    /// <param name="url">直链 URL（openlist 原始签址或已验证直链）。</param>
    /// <param name="size">内容长度（可为空）。</param>
    /// <param name="contentType">内容类型（可为空，用于推断容器）。</param>
    /// <returns>虚拟 MediaSource。</returns>
    public static MediaSourceInfo Build(string key, string url, long? size, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("直连 URL 不能为空", nameof(url));
        }

        var safeKey = key ?? string.Empty;
        return new MediaSourceInfo
        {
            Id = DeriveStableId(safeKey),
            Protocol = MediaProtocol.Http,
            Type = MediaSourceType.Default,
            Path = url,
            IsRemote = true,
            Name = "MetaShark 直连",
            ETag = safeKey,
            Size = size,
            Container = GuessContainer(url, contentType),
            SupportsTranscoding = false,
            SupportsDirectStream = true,
            SupportsDirectPlay = true,
            SupportsProbing = false,
            RequiresOpening = false,
            RequiresClosing = false,
            RequiresLooping = false,
            ReadAtNativeFramerate = false,
        };
    }

    /// <summary>
    /// 由缓存 key 派生确定性 Guid 形式的 Id：同一文件跨请求/重启稳定，
    /// 且可被服务端 <c>Guid.Parse</c> 解析（混流/转码路径 <c>StreamingHelpers.GetStreamingState</c> 会解析 MediaSourceId）。
    /// </summary>
    /// <param name="key">缓存 key。</param>
    /// <returns>32 位无连字符的 Guid 字符串。</returns>
    private static string DeriveStableId(string key)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
        return new Guid(hash).ToString("N");
    }

    private static string? GuessContainer(string directUrl, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var ct = contentType.ToLowerInvariant();
            if (ct.Contains("mp4", StringComparison.Ordinal))
            {
                return "mp4";
            }

            if (ct.Contains("matroska", StringComparison.Ordinal) || ct.Contains("x-matroska", StringComparison.Ordinal))
            {
                return "mkv";
            }

            if (ct.Contains("mpegts", StringComparison.Ordinal) || ct.Contains("mp2t", StringComparison.Ordinal))
            {
                return "ts";
            }

            if (ct.Contains("x-msvideo", StringComparison.Ordinal))
            {
                return "avi";
            }
        }

        try
        {
            var path = new Uri(directUrl).AbsolutePath;
            var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            return string.IsNullOrEmpty(ext) ? null : ext;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
