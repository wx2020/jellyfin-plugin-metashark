using System;
using System.IO;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// MetaShark 代理源构造器（纯构造逻辑，可单测；只新增虚拟源，不改媒体库数据）。
/// Path 指向插件签名端点，服务端按 Range 从 .strm 首行直链中转字节。
/// </summary>
public static class StrmProxySourceFactory
{
    /// <summary>代理源显示名。</summary>
    public const string SourceName = "MetaShark 代理";

    /// <summary>
    /// 构造代理 Virtual MediaSource。
    /// </summary>
    /// <param name="key">缓存 key（写入 ETag，并派生确定性 Guid Id）。</param>
    /// <param name="proxyPath">签名端点 URL/路径。</param>
    /// <param name="originalUrl">.strm 首行直链（仅用于推断容器，不直接下发）。</param>
    /// <returns>代理 MediaSource。</returns>
    public static MediaSourceInfo Build(string key, string proxyPath, string originalUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyPath))
        {
            throw new ArgumentException("代理端点路径不能为空", nameof(proxyPath));
        }

        var safeKey = key ?? string.Empty;
        return new MediaSourceInfo
        {
            Id = StrmSourceKey.DeriveStableId(safeKey),
            Protocol = MediaProtocol.Http,
            Type = MediaSourceType.Default,
            Path = proxyPath,
            IsRemote = true,
            Name = SourceName,
            ETag = safeKey,
            Container = GuessContainer(originalUrl),
            SupportsTranscoding = false,
            SupportsDirectStream = true,
            SupportsDirectPlay = true,
            SupportsProbing = false,
            RequiresOpening = false,
            RequiresClosing = false,
            RequiresLooping = false,
            ReadAtNativeFramerate = false,
            MediaStreams = Array.Empty<MediaStream>(),
        };
    }

    private static string? GuessContainer(string? directUrl)
    {
        if (string.IsNullOrWhiteSpace(directUrl))
        {
            return null;
        }

        try
        {
            var path = new Uri(directUrl.Trim()).AbsolutePath;
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            return string.IsNullOrEmpty(ext) ? null : ext;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
