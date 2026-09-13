using System;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// strm 探针共享常量。
/// </summary>
public static class StrmProbeConstants
{
    /// <summary>独立库文件名（插件自带，不碰服务端数据库）。</summary>
    public const string DbFileName = "metashark-strm-probe.db";

    /// <summary>缓存默认有效期（7 天）。过期视为未命中并自动重探。</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(7);

    /// <summary>后台预热最大并发数（避免打满网盘站）。</summary>
    public const int MaxConcurrentWarmups = 2;

    /// <summary>入库真探去抖延迟：新增条目入库后等待刮削完成再做完整远程探测。</summary>
    public static readonly TimeSpan LibraryRefreshDebounce = TimeSpan.FromMinutes(5);
}
