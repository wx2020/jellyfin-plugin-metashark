using System;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// 基于 SQLite 独立库文件的探针缓存实现。库文件由插件自带，不触碰 Jellyfin 服务端自带数据库与媒体库数据。
/// </summary>
public sealed class SqliteStrmProbeCacheStore : IStrmProbeCacheStore
{
    private const string CreateTableSql =
        "CREATE TABLE IF NOT EXISTS probe_cache ("
        + "cache_key TEXT PRIMARY KEY, "
        + "url TEXT NOT NULL, "
        + "file_size INTEGER NOT NULL, "
        + "signature TEXT NOT NULL, "
        + "direct_url TEXT NOT NULL, "
        + "content_type TEXT NULL, "
        + "content_length INTEGER NULL, "
        + "probed_at_utc TEXT NOT NULL, "
        + "expires_at_utc TEXT NOT NULL)";

    private readonly string _dbFilePath;
    private readonly ILogger<SqliteStrmProbeCacheStore> _logger;
    private readonly SqliteConnection _connection;
    private readonly object _lock = new object();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteStrmProbeCacheStore"/> class.
    /// </summary>
    /// <param name="dbFilePath">独立库文件路径。</param>
    /// <param name="logger">日志。</param>
    public SqliteStrmProbeCacheStore(string dbFilePath, ILogger<SqliteStrmProbeCacheStore> logger)
    {
        _dbFilePath = dbFilePath ?? throw new ArgumentNullException(nameof(dbFilePath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var dir = Path.GetDirectoryName(_dbFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        };
        _connection = new SqliteConnection(builder.ToString());
        _connection.Open();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = CreateTableSql;
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public StrmProbeCacheEntry? TryGet(string key, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT cache_key, url, file_size, signature, direct_url, content_type, content_length, probed_at_utc, expires_at_utc FROM probe_cache WHERE cache_key = $key LIMIT 1";
            cmd.Parameters.AddWithValue("$key", key);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                _logger.LogDebug("探针缓存未命中 key={CacheKey}", key);
                return null;
            }

            var entry = new StrmProbeCacheEntry
            {
                Key = reader.GetString(0),
                Url = reader.GetString(1),
                FileSize = reader.GetInt64(2),
                Signature = reader.GetString(3),
                DirectUrl = reader.GetString(4),
                ContentType = reader.IsDBNull(5) ? null : reader.GetString(5),
                ContentLength = reader.IsDBNull(6) ? null : (long?)reader.GetInt64(6),
                ProbedAtUtc = DateTime.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                ExpiresAtUtc = DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            };

            if (entry.ExpiresAtUtc <= nowUtc)
            {
                _logger.LogInformation("探针缓存已过期 key={CacheKey}，自动清理并视为未命中", key);
                reader.Close();
                using var del = _connection.CreateCommand();
                del.CommandText = "DELETE FROM probe_cache WHERE cache_key = $key";
                del.Parameters.AddWithValue("$key", key);
                del.ExecuteNonQuery();
                return null;
            }

            _logger.LogDebug("探针缓存命中 key={CacheKey} direct={DirectUrl}", key, entry.DirectUrl);
            return entry;
        }
    }

    /// <inheritdoc />
    public void Set(StrmProbeCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrEmpty(entry.Key))
        {
            throw new ArgumentException("缓存 key 不能为空", nameof(entry));
        }

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO probe_cache (cache_key, url, file_size, signature, direct_url, content_type, content_length, probed_at_utc, expires_at_utc)"
                + " VALUES ($key, $url, $size, $sig, $direct, $ctype, $clen, $probed, $expires)"
                + " ON CONFLICT(cache_key) DO UPDATE SET url=excluded.url, file_size=excluded.file_size, signature=excluded.signature,"
                + " direct_url=excluded.direct_url, content_type=excluded.content_type, content_length=excluded.content_length,"
                + " probed_at_utc=excluded.probed_at_utc, expires_at_utc=excluded.expires_at_utc";
            cmd.Parameters.AddWithValue("$key", entry.Key);
            cmd.Parameters.AddWithValue("$url", entry.Url);
            cmd.Parameters.AddWithValue("$size", entry.FileSize);
            cmd.Parameters.AddWithValue("$sig", entry.Signature);
            cmd.Parameters.AddWithValue("$direct", entry.DirectUrl);
            cmd.Parameters.AddWithValue("$ctype", (object?)entry.ContentType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$clen", (object?)entry.ContentLength ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$probed", entry.ProbedAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$expires", entry.ExpiresAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
            _logger.LogInformation("探针缓存预热写入 key={CacheKey} direct={DirectUrl}", entry.Key, entry.DirectUrl);
        }
    }

    /// <inheritdoc />
    public int RemoveExpired(DateTime nowUtc)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM probe_cache WHERE expires_at_utc <= $now";
            cmd.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            return cmd.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
    }
}
