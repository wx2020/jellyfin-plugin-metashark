using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// MetaShark 代理源票据签发/校验（HMAC-SHA256 + 过期，纯逻辑可单测）。
/// 票据载荷绑定 <c>itemId + 过期时间</c>，密钥为服务端本地随机 secret（落 DataPath）。
/// 端点匿名放行但只认自签票据：密钥缺失或为空即视为禁用（fail-closed）。
/// </summary>
public sealed class StrmProxyTokenService
{
    private readonly Func<string> _secretProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmProxyTokenService"/> class.
    /// </summary>
    /// <param name="secretProvider">签名密钥提供器（惰性读取，返回空串表示票据体系停用）。</param>
    public StrmProxyTokenService(Func<string> secretProvider)
    {
        _secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
    }

    /// <summary>
    /// 为指定条目签发票据。密钥不可用时返回 false（调用方不下发代理源）。
    /// </summary>
    /// <param name="itemId">条目 Id。</param>
    /// <param name="nowUtc">当前 UTC 时间。</param>
    /// <param name="expiresAtUnix">过期时间（Unix 秒）。</param>
    /// <param name="signature">签名（小写十六进制）。</param>
    /// <returns>签发成功返回 true。</returns>
    public bool TryCreateTicket(Guid itemId, DateTime nowUtc, out long expiresAtUnix, out string signature)
    {
        expiresAtUnix = 0;
        signature = string.Empty;
        try
        {
            if (itemId == Guid.Empty)
            {
                return false;
            }

            var secret = _secretProvider() ?? string.Empty;
            if (secret.Length == 0)
            {
                return false;
            }

            var expires = nowUtc.Add(StrmProbeConstants.StrmProxyTicketTtl).ToUnixTimeSeconds();
            if (expires <= 0)
            {
                return false;
            }

            signature = ComputeSignature(secret, BuildPayload(itemId, expires));
            expiresAtUnix = expires;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 校验票据：条目绑定正确、未过期、签名匹配。任一步失败返回 false（fail-closed）。
    /// </summary>
    /// <param name="itemId">条目 Id。</param>
    /// <param name="expiresAtUnix">票据过期时间（Unix 秒，字符串形式）。</param>
    /// <param name="signature">票据签名。</param>
    /// <param name="nowUtc">当前 UTC 时间。</param>
    /// <returns>有效返回 true。</returns>
    public bool Validate(Guid itemId, string? expiresAtUnix, string? signature, DateTime nowUtc)
    {
        try
        {
            if (itemId == Guid.Empty || string.IsNullOrWhiteSpace(signature))
            {
                return false;
            }

            if (!long.TryParse(expiresAtUnix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expires) || expires <= 0)
            {
                return false;
            }

            if (DateTimeOffset.FromUnixTimeSeconds(expires).UtcDateTime <= nowUtc)
            {
                return false;
            }

            var secret = _secretProvider() ?? string.Empty;
            if (secret.Length == 0)
            {
                return false;
            }

            var expected = ComputeSignature(secret, BuildPayload(itemId, expires));
            return FixedTimeEqualsHex(expected, signature.Trim());
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 构造签名载荷（条目 + 过期时间，二者任一被篡改即失配）。
    /// </summary>
    /// <param name="itemId">条目 Id。</param>
    /// <param name="expiresAtUnix">过期时间（Unix 秒）。</param>
    /// <returns>载荷字符串。</returns>
    internal static string BuildPayload(Guid itemId, long expiresAtUnix)
    {
        return itemId.ToString("N") + "|" + expiresAtUnix.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 计算 HMAC-SHA256 签名（小写十六进制）。
    /// </summary>
    /// <param name="secret">签名密钥。</param>
    /// <param name="payload">载荷。</param>
    /// <returns>签名。</returns>
    internal static string ComputeSignature(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool FixedTimeEqualsHex(string expected, string actual)
    {
        if (expected.Length != actual.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(actual));
    }
}
