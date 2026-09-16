using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// strm 源标识派生（纯逻辑，可单元测试）：
/// 缓存 key = SHA256(url + 文件大小 + mtime 签名)；确定性 Guid Id = MD5(key) 的 32 位无连字符形式。
/// 与历史虚拟直连源口径一致：同一文件跨请求/重启稳定，且 Guid 能被 core <c>Guid.Parse</c> 解析
/// （混流/转码路径 <c>StreamingHelpers.GetStreamingState</c> 会解析 MediaSourceId）。
/// </summary>
public static class StrmSourceKey
{
    /// <summary>
    /// 计算缓存 key（SHA256 十六进制小写，url+size+sig 三要素）。
    /// </summary>
    /// <param name="url">strm 首行直链。</param>
    /// <param name="fileSize">strm 文件大小。</param>
    /// <param name="signature">strm 文件 mtime 签名。</param>
    /// <returns>缓存 key。</returns>
    public static string Compute(string? url, long fileSize, string? signature)
    {
        var normalizedUrl = (url ?? string.Empty).Trim();
        var normalizedSig = (signature ?? string.Empty).Trim();
        var raw = normalizedUrl + "\n" + fileSize.ToString(CultureInfo.InvariantCulture) + "\n" + normalizedSig;
        var bytes = Encoding.UTF8.GetBytes(raw);
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    /// <summary>
    /// 由缓存 key 派生确定性 Guid 形式 Id（MD5→"N" 32 位）。
    /// </summary>
    /// <param name="key">缓存 key。</param>
    /// <returns>32 位无连字符的 Guid 字符串。</returns>
    public static string DeriveStableId(string? key)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key ?? string.Empty));
        return new Guid(hash).ToString("N");
    }

    /// <summary>
    /// 由缓存 key 派生确定性 Guid。
    /// </summary>
    /// <param name="key">缓存 key。</param>
    /// <returns>确定性 Guid。</returns>
    public static Guid DeriveStableGuid(string? key)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key ?? string.Empty));
        return new Guid(hash);
    }
}
