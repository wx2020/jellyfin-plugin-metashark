using System;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.StrmProbe;

/// <summary>
/// 基于 SQLite 独立库文件的 ffprobe 结果缓存实现，与 strm 探针缓存共用同一库文件、独立表。
/// </summary>
public sealed class SqliteMediaInfoProbeCacheStore : IMediaInfoProbeCacheStore
{
    private const string CreateTableSql =
        "CREATE TABLE IF NOT EXISTS mediainfo_cache ("
        + "url_hash TEXT PRIMARY KEY, "
        + "url TEXT NOT NULL, "
        + "mediainfo_json TEXT NOT NULL, "
        + "probed_at_utc TEXT NOT NULL, "
        + "expires_at_utc TEXT NOT NULL)";

    private readonly string _dbFilePath;
    private readonly ILogger<SqliteMediaInfoProbeCacheStore> _logger;
    private readonly SqliteConnection _connection;
    private readonly object _lock = new object();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteMediaInfoProbeCacheStore"/> class.
    /// </summary>
    /// <param name="dbFilePath">独立库文件路径（与 strm 探针缓存共用）。</param>
    /// <param name="logger">日志。</param>
    public SqliteMediaInfoProbeCacheStore(string dbFilePath, ILogger<SqliteMediaInfoProbeCacheStore> logger)
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
    public MediaInfoProbeCacheEntry? TryGet(string key, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT url_hash, url, mediainfo_json, probed_at_utc, expires_at_utc FROM mediainfo_cache WHERE url_hash = $key LIMIT 1";
            cmd.Parameters.AddWithValue("$key", key);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                _logger.LogDebug("ffprobe 缓存未命中 key={CacheKey}", key);
                return null;
            }

            var entry = new MediaInfoProbeCacheEntry
            {
                Key = reader.GetString(0),
                Url = reader.GetString(1),
                MediaInfoJson = reader.GetString(2),
                ProbedAtUtc = DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                ExpiresAtUtc = DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            };

            if (entry.ExpiresAtUtc <= nowUtc)
            {
                _logger.LogDebug("ffprobe 缓存已过期 key={CacheKey}，自动清理并视为未命中", key);
                reader.Close();
                using var del = _connection.CreateCommand();
                del.CommandText = "DELETE FROM mediainfo_cache WHERE url_hash = $key";
                del.Parameters.AddWithValue("$key", key);
                del.ExecuteNonQuery();
                return null;
            }

            _logger.LogDebug("ffprobe 缓存命中 key={CacheKey}", key);
            return entry;
        }
    }

    /// <inheritdoc />
    public void Set(MediaInfoProbeCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrEmpty(entry.Key))
        {
            throw new ArgumentException("缓存 key 不能为空", nameof(entry));
        }

        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO mediainfo_cache (url_hash, url, mediainfo_json, probed_at_utc, expires_at_utc)"
                + " VALUES ($key, $url, $json, $probed, $expires)"
                + " ON CONFLICT(url_hash) DO UPDATE SET url=excluded.url,"
                + " mediainfo_json=excluded.mediainfo_json,"
                + " probed_at_utc=excluded.probed_at_utc, expires_at_utc=excluded.expires_at_utc";
            cmd.Parameters.AddWithValue("$key", entry.Key);
            cmd.Parameters.AddWithValue("$url", entry.Url);
            cmd.Parameters.AddWithValue("$json", entry.MediaInfoJson);
            cmd.Parameters.AddWithValue("$probed", entry.ProbedAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$expires", entry.ExpiresAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
            _logger.LogDebug("ffprobe 缓存写入 key={CacheKey}", entry.Key);
        }
    }

    /// <inheritdoc />
    public int RemoveExpired(DateTime nowUtc)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM mediainfo_cache WHERE expires_at_utc <= $now";
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
