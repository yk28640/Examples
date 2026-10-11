using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AutomationPlatform;

public sealed class DiscoveryHistoryService(IOptions<DiscoveryOptions> options)
{
    public ScanHistoryPage GetScans(int page, int pageSize, CancellationToken cancellationToken)
    {
        var databasePath = Path.GetFullPath(options.Value.ScanHistoryDatabasePath);
        if (!File.Exists(databasePath))
            return new ScanHistoryPage([], page, pageSize, 0);

        using var connection = OpenReadOnly(databasePath);
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM Scans;";
        var totalCount = (long)count.ExecuteScalar()!;

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.Id, s.ScannedAtUtc, s.NetworkInterface, s.Filter, s.Warning,
                   (SELECT COUNT(*) FROM ScanDevices WHERE ScanId = s.Id)
            FROM Scans s
            ORDER BY s.Id DESC
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", ((long)page - 1) * pageSize);
        using var reader = command.ExecuteReader();
        var scans = new List<ScanHistorySummary>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            scans.Add(new ScanHistorySummary(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)),
                reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5)));
        }

        return new ScanHistoryPage(scans, page, pageSize, totalCount);
    }

    public ScanHistoryDetail? GetScan(long id, CancellationToken cancellationToken)
    {
        var databasePath = Path.GetFullPath(options.Value.ScanHistoryDatabasePath);
        if (!File.Exists(databasePath))
            return null;

        using var connection = OpenReadOnly(databasePath);
        using var scan = connection.CreateCommand();
        scan.CommandText = "SELECT Id, ScannedAtUtc, NetworkInterface, Filter, Warning FROM Scans WHERE Id = $id;";
        scan.Parameters.AddWithValue("$id", id);
        long scanId;
        DateTimeOffset scannedAtUtc;
        string networkInterface;
        string filter;
        string? warning;
        using (var reader = scan.ExecuteReader())
        {
            if (!reader.Read())
                return null;

            scanId = reader.GetInt64(0);
            scannedAtUtc = DateTimeOffset.Parse(reader.GetString(1));
            networkInterface = reader.GetString(2);
            filter = reader.GetString(3);
            warning = reader.IsDBNull(4) ? null : reader.GetString(4);
        }

        using var device = connection.CreateCommand();
        device.CommandText = """
            SELECT IpAddress, MacAddress, ProfinetName, Description, ArticleNumber,
                   OperatingMode, StatusError, IsProtected
            FROM ScanDevices WHERE ScanId = $id ORDER BY Id;
            """;
        device.Parameters.AddWithValue("$id", id);
        using var devicesReader = device.ExecuteReader();
        var devices = new List<DiscoveredDevice>();
        while (devicesReader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            devices.Add(new DiscoveredDevice(devicesReader.GetString(0), devicesReader.GetString(1),
                devicesReader.GetString(2), devicesReader.GetString(3), devicesReader.GetString(4),
                devicesReader.IsDBNull(5) ? null : devicesReader.GetString(5),
                devicesReader.IsDBNull(6) ? null : devicesReader.GetString(6),
                devicesReader.IsDBNull(7) ? null : devicesReader.GetInt64(7) != 0));
        }

        return new ScanHistoryDetail(scanId, scannedAtUtc, networkInterface, filter, warning, devices);
    }

    private static SqliteConnection OpenReadOnly(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout = 10000;");
        return connection;
    }

    public void Save(DateTimeOffset scannedAtUtc, string networkInterface, string filter, DiscoveryScanResult result)
    {
        var maxDatabaseBytes = (long)options.Value.ScanHistoryMaxMegabytes * 1024 * 1024;
        var databasePath = Path.GetFullPath(options.Value.ScanHistoryDatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys = ON;");
        Execute(connection, "PRAGMA busy_timeout = 10000;");
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS Scans (
                Id INTEGER PRIMARY KEY,
                ScannedAtUtc TEXT NOT NULL,
                NetworkInterface TEXT NOT NULL,
                Filter TEXT NOT NULL,
                Warning TEXT
            );
            CREATE TABLE IF NOT EXISTS ScanDevices (
                Id INTEGER PRIMARY KEY,
                ScanId INTEGER NOT NULL REFERENCES Scans(Id) ON DELETE CASCADE,
                IpAddress TEXT NOT NULL,
                MacAddress TEXT NOT NULL,
                ProfinetName TEXT NOT NULL,
                Description TEXT NOT NULL,
                ArticleNumber TEXT NOT NULL,
                OperatingMode TEXT,
                StatusError TEXT,
                IsProtected INTEGER
            );
            CREATE INDEX IF NOT EXISTS IX_ScanDevices_ScanId ON ScanDevices(ScanId);
            """);

        var pruned = false;
        using (var transaction = connection.BeginTransaction())
        {
            using (var scan = connection.CreateCommand())
            {
                scan.Transaction = transaction;
                scan.CommandText = "INSERT INTO Scans (ScannedAtUtc, NetworkInterface, Filter, Warning) VALUES ($time, $network, $filter, $warning);";
                scan.Parameters.AddWithValue("$time", scannedAtUtc.ToUniversalTime().ToString("O"));
                scan.Parameters.AddWithValue("$network", networkInterface);
                scan.Parameters.AddWithValue("$filter", filter);
                scan.Parameters.AddWithValue("$warning", (object?)result.Warning ?? DBNull.Value);
                scan.ExecuteNonQuery();
            }

            var scanId = Scalar(connection, transaction, "SELECT last_insert_rowid();");
            using (var device = connection.CreateCommand())
            {
                device.Transaction = transaction;
                device.CommandText = """
                    INSERT INTO ScanDevices (ScanId, IpAddress, MacAddress, ProfinetName, Description,
                        ArticleNumber, OperatingMode, StatusError, IsProtected)
                    VALUES ($scan, $ip, $mac, $name, $description, $article, $mode, $error, $protected);
                    """;
                device.Parameters.AddWithValue("$scan", scanId);
                var ip = device.Parameters.Add("$ip", SqliteType.Text);
                var mac = device.Parameters.Add("$mac", SqliteType.Text);
                var name = device.Parameters.Add("$name", SqliteType.Text);
                var description = device.Parameters.Add("$description", SqliteType.Text);
                var article = device.Parameters.Add("$article", SqliteType.Text);
                var mode = device.Parameters.Add("$mode", SqliteType.Text);
                var error = device.Parameters.Add("$error", SqliteType.Text);
                var isProtected = device.Parameters.Add("$protected", SqliteType.Integer);
                foreach (var entry in result.Devices)
                {
                    ip.Value = entry.IpAddress ?? string.Empty;
                    mac.Value = entry.MacAddress ?? string.Empty;
                    name.Value = entry.ProfinetName ?? string.Empty;
                    description.Value = entry.Description ?? string.Empty;
                    article.Value = entry.ArticleNumber ?? string.Empty;
                    mode.Value = (object?)entry.OperatingMode ?? DBNull.Value;
                    error.Value = (object?)entry.StatusError ?? DBNull.Value;
                    isProtected.Value = entry.IsProtected.HasValue ? (entry.IsProtected.Value ? 1 : 0) : DBNull.Value;
                    device.ExecuteNonQuery();
                }
            }

            while (UsedBytes(connection, transaction) > maxDatabaseBytes)
            {
                using var oldest = connection.CreateCommand();
                oldest.Transaction = transaction;
                oldest.CommandText = "SELECT Id FROM Scans WHERE Id <> $current ORDER BY Id LIMIT 1;";
                oldest.Parameters.AddWithValue("$current", scanId);
                if (oldest.ExecuteScalar() is not long oldId)
                    throw new InvalidOperationException($"本次扫描结果超过 {options.Value.ScanHistoryMaxMegabytes} MB，无法保存到扫描历史数据库。");

                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM Scans WHERE Id = $id;";
                delete.Parameters.AddWithValue("$id", oldId);
                delete.ExecuteNonQuery();
                pruned = true;
            }

            transaction.Commit();
        }

        if (pruned || new FileInfo(databasePath).Length > maxDatabaseBytes)
            Execute(connection, "VACUUM;");
    }

    private static long UsedBytes(SqliteConnection connection, SqliteTransaction transaction) =>
        (Scalar(connection, transaction, "PRAGMA page_count;") - Scalar(connection, transaction, "PRAGMA freelist_count;")) *
        Scalar(connection, transaction, "PRAGMA page_size;");

    private static long Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

public sealed record ScanHistorySummary(long Id, DateTimeOffset ScannedAtUtc, string NetworkInterface,
    string Filter, string? Warning, long DeviceCount);

public sealed record ScanHistoryPage(IReadOnlyList<ScanHistorySummary> Items, int Page, int PageSize, long TotalCount);

public sealed record ScanHistoryDetail(long Id, DateTimeOffset ScannedAtUtc, string NetworkInterface,
    string Filter, string? Warning, IReadOnlyList<DiscoveredDevice> Devices);
