using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// MetaShark 代理源签名密钥的本地存储：首次使用生成随机密钥并写入插件数据目录。
/// 任何异常都只记日志并返回空串（票据体系按停用处理，fail-closed）。
/// </summary>
public static class StrmProxySecretStore
{
    private const int SecretBytes = 48;

    /// <summary>
    /// 读取或创建签名密钥。
    /// </summary>
    /// <param name="secretFilePath">密钥文件路径。</param>
    /// <param name="logger">日志。</param>
    /// <returns>密钥；不可用时返回空串。</returns>
    public static string LoadOrCreate(string secretFilePath, ILogger? logger)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(secretFilePath) && File.Exists(secretFilePath))
            {
                var existing = File.ReadAllText(secretFilePath).Trim();
                if (existing.Length >= 32)
                {
                    return existing;
                }
            }

            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytes));
            if (!string.IsNullOrWhiteSpace(secretFilePath))
            {
                var dir = Path.GetDirectoryName(secretFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(secretFilePath, secret);
            }

            return secret;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "MetaShark 代理源签名密钥不可用，代理源将停用");
            return string.Empty;
        }
    }
}
