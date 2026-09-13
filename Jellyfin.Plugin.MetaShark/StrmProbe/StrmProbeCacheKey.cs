using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// 探针缓存 key 生成器：按 URL + 文件大小 + 签名/mtime 组合做 key。
/// </summary>
public static class StrmProbeCacheKey
{
    /// <summary>
    /// 计算缓存 key（SHA256 十六进制小写）。
    /// </summary>
    /// <param name="url">strm 文件内直链 URL。</param>
    /// <param name="fileSize">strm 文件大小（字节）。</param>
    /// <param name="signature">签名/mtime（建议用 strm 文件 LastWriteTimeUtc.Ticks 字符串）。</param>
    /// <returns>缓存 key。</returns>
    public static string Compute(string url, long fileSize, string signature)
    {
        var normalizedUrl = (url ?? string.Empty).Trim();
        var normalizedSig = (signature ?? string.Empty).Trim();
        var raw = normalizedUrl + "\n" + fileSize.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + normalizedSig;
        var bytes = Encoding.UTF8.GetBytes(raw);
#if NET9_0_OR_GREATER
        var hash = SHA256.HashData(bytes);
#else
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(bytes);
#endif
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
