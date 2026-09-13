using System;
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
    /// <param name="key">缓存 key（写入 ETag）。</param>
    /// <param name="directUrl">可直连 URL。</param>
    /// <param name="size">内容长度（可为空）。</param>
    /// <param name="contentType">内容类型（可为空，用于推断容器）。</param>
    /// <returns>虚拟 MediaSource。</returns>
    public static MediaSourceInfo Build(string key, string directUrl, long? size, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(directUrl))
        {
            throw new ArgumentException("直连 URL 不能为空", nameof(directUrl));
        }

        var safeKey = key ?? string.Empty;
        var shortKey = safeKey.Length > 16 ? safeKey.Substring(0, 16) : safeKey;
        return new MediaSourceInfo
        {
            Id = StrmProbeConstants.VirtualSourceIdPrefix + shortKey,
            Protocol = MediaProtocol.Http,
            Type = MediaSourceType.Default,
            Path = directUrl,
            IsRemote = true,
            Name = "MetaShark 直连",
            ETag = safeKey,
            Size = size,
            Container = GuessContainer(directUrl, contentType),
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
