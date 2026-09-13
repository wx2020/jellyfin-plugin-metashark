using System;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// ffprobe 探测结果缓存存储。
/// </summary>
public interface IMediaInfoProbeCacheStore : IDisposable
{
    /// <summary>
    /// 按 key 读取，未命中或已过期返回 null（过期条目会被自动清理）。
    /// </summary>
    /// <param name="key">缓存 key。</param>
    /// <param name="nowUtc">当前时间（UTC）。</param>
    /// <returns>缓存条目或 null。</returns>
    MediaInfoProbeCacheEntry? TryGet(string key, DateTime nowUtc);

    /// <summary>
    /// 写入（同 key 覆盖）。
    /// </summary>
    /// <param name="entry">缓存条目。</param>
    void Set(MediaInfoProbeCacheEntry entry);

    /// <summary>
    /// 删除所有过期条目。
    /// </summary>
    /// <param name="nowUtc">当前时间（UTC）。</param>
    /// <returns>删除条数。</returns>
    int RemoveExpired(DateTime nowUtc);
}
