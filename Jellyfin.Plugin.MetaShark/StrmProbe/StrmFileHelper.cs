using System;
using System.IO;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// strm 文件读取助手：解析首个非空行 URL，并采集文件大小 + mtime 签名。
/// </summary>
public static class StrmFileHelper
{
    /// <summary>
    /// 是否 strm 路径。
    /// </summary>
    /// <param name="path">条目路径。</param>
    /// <returns>strm 返回 true。</returns>
    public static bool IsStrmPath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && path.Trim().EndsWith(".strm", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 读取 strm 直链信息。失败返回 false（调用方走原生行为）。
    /// </summary>
    /// <param name="strmPath">strm 文件路径。</param>
    /// <param name="url">首个非空行 URL。</param>
    /// <param name="fileSize">文件大小。</param>
    /// <param name="signature">mtime 签名（LastWriteTimeUtc.Ticks）。</param>
    /// <returns>成功返回 true。</returns>
    public static bool TryReadStrmLink(string strmPath, out string url, out long fileSize, out string signature)
    {
        url = string.Empty;
        fileSize = 0;
        signature = string.Empty;
        try
        {
            var info = new FileInfo(strmPath);
            if (!info.Exists)
            {
                return false;
            }

            fileSize = info.Length;
            signature = info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var reader = new StreamReader(strmPath);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                url = line;
                break;
            }

            return url.Length > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
