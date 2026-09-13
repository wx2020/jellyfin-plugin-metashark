using System;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// 探针缓存存储接口（插件自带 SQLite 独立库文件实现）。
/// </summary>
public interface IStrmProbeCacheStore : IDisposable
{
    /// <summary>
    /// 按 key 读取缓存。命中且未过期返回条目；未命中或已过期返回 null（过期条目会被清理）。
    /// </summary>
    /// <param name="key">缓存 key。</param>
    /// <param name="nowUtc">当前 UTC 时间。</param>
    /// <returns>缓存条目或 null。</returns>
    StrmProbeCacheEntry? TryGet(string key, DateTime nowUtc);

    /// <summary>
    /// 写入缓存（存在则覆盖）。
    /// </summary>
    /// <param name="entry">缓存条目。</param>
    void Set(StrmProbeCacheEntry entry);

    /// <summary>
    /// 清理过期条目。
    /// </summary>
    /// <param name="nowUtc">当前 UTC 时间。</param>
    /// <returns>清理条数。</returns>
    int RemoveExpired(DateTime nowUtc);
}
