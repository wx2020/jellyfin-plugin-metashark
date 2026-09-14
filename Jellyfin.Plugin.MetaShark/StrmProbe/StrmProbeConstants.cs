using System;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// strm 探针共享常量。
/// </summary>
public static class StrmProbeConstants
{
    /// <summary>独立库文件名（插件自带，不碰服务端数据库）。</summary>
    public const string DbFileName = "metashark-strm-probe.db";

    /// <summary>ffprobe 缓存默认有效期（天）。缓存 key 由 URL 派生，URL 变化即天然失效；该 TTL 仅作同 URL 换内容的安全阀。</summary>
    public const int DefaultCacheTtlDays = 90;

    /// <summary>永不过期的哨兵时间（UTC）。刻意不用 DateTime.MaxValue：本机时区偏移为负时 ToUniversalTime() 会溢出。</summary>
    public static readonly DateTime NeverExpireUtc = new DateTime(9000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>后台探测最大并发数（入库媒体探测/每日探测共用，避免打满网盘站）。</summary>
    public const int MaxConcurrentWarmups = 2;

    /// <summary>入库真探去抖延迟：新增条目入库后等待刮削完成再做完整远程探测。</summary>
    public static readonly TimeSpan LibraryRefreshDebounce = TimeSpan.FromMinutes(5);
}
