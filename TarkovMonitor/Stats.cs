using Microsoft.Data.Sqlite;

namespace TarkovMonitor
{
    internal static class Stats
    {
        public static string DatabasePath => Path.Join(Application.UserAppDataPath, "..", "TarkovMonitor.db");
        private static readonly Lazy<StatsDatabase> Database = new(() => new StatsDatabase(DatabasePath));

        public static void ClearData() => Database.Value.ClearData();

        public static void AddFleaSale(FleaSoldMessageLogContent e, Profile profile) =>
            Database.Value.AddFleaSale(e, profile);

        public static int GetTotalSales(string currency) => Database.Value.GetTotalSales(currency);

        public static void AddRaid(RaidInfoEventArgs e) => Database.Value.AddRaid(e);

        public static int GetTotalRaids(string mapNameId) => Database.Value.GetTotalRaids(mapNameId);

        public static Dictionary<string, int> GetTotalRaidsPerMap(RaidType raidType) =>
            Database.Value.GetTotalRaidsPerMap(raidType, TarkovDev.Maps);
    }

    internal sealed class StatsDatabase : IDisposable
    {
        private readonly SqliteConnection connection;

        internal StatsDatabase(string databasePath)
        {
            connection = new SqliteConnection($"Data Source={databasePath};");
            connection.Open();
            CreateTables();
            UpdateDatabase();
        }

        public void Dispose()
        {
            connection.Dispose();
        }

        internal void ClearData()
        {
            var tableNames = new List<string>();
            using (var command = CreateCommand("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';"))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    tableNames.Add(reader.GetString(0));
                }
            }

            using var transaction = connection.BeginTransaction();
            foreach (var tableName in tableNames)
            {
                using var command = CreateCommand($"DELETE FROM [{tableName}];", transaction);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        internal void AddFleaSale(FleaSoldMessageLogContent e, Profile profile)
        {
            const string sql = "INSERT INTO flea_sales(profile_id, item_id, buyer, count, currency, price) VALUES(@profile_id, @item_id, @buyer, @count, @currency, @price);";
            var receivedItem = e.ReceivedItems.First();
            ExecuteNonQuery(sql, new Dictionary<string, object?>
            {
                ["profile_id"] = profile.Id,
                ["item_id"] = e.SoldItemId,
                ["buyer"] = e.Buyer,
                ["count"] = e.SoldItemCount,
                ["currency"] = receivedItem.Key,
                ["price"] = receivedItem.Value,
            });
        }

        internal int GetTotalSales(string currency)
        {
            return ExecuteScalarInt(
                "SELECT COALESCE(SUM(price), 0) FROM flea_sales WHERE currency = @currency;",
                new Dictionary<string, object?> { ["currency"] = currency });
        }

        internal void AddRaid(RaidInfoEventArgs e)
        {
            const string sql = "INSERT INTO raids(profile_id, map, raid_type, queue_time, raid_id) VALUES (@profile_id, @map, @raid_type, @queue_time, @raid_id);";
            ExecuteNonQuery(sql, new Dictionary<string, object?>
            {
                ["profile_id"] = e.Profile.Id,
                ["map"] = e.RaidInfo.Map?.nameId,
                ["raid_type"] = (int)e.RaidInfo.RaidType,
                ["queue_time"] = e.RaidInfo.QueueTime,
                ["raid_id"] = e.RaidInfo.RaidId,
            });
        }

        internal int GetTotalRaids(string mapNameId)
        {
            return ExecuteScalarInt(
                "SELECT COUNT(id) FROM raids WHERE map = @map;",
                new Dictionary<string, object?> { ["map"] = mapNameId });
        }

        internal Dictionary<string, int> GetTotalRaidsPerMap(RaidType raidType, IEnumerable<TarkovDev.Map> maps)
        {
            var mapTotals = new Dictionary<string, int>();
            using (var command = CreateCommand(
                "SELECT map, COUNT(id) FROM raids WHERE raid_type = @raid_type GROUP BY map;",
                parameters: new Dictionary<string, object?> { ["raid_type"] = (int)raidType }))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (!reader.IsDBNull(0))
                    {
                        mapTotals[reader.GetString(0)] = reader.GetInt32(1);
                    }
                }
            }

            return maps.ToDictionary(
                map => map.name,
                map => mapTotals.TryGetValue(map.nameId, out var total) ? total : 0);
        }

        private void CreateTables()
        {
            var commands = new[]
            {
                "CREATE TABLE IF NOT EXISTS flea_sales (id INTEGER PRIMARY KEY, profile_id VARCHAR(24), item_id CHAR(24), buyer VARCHAR(14), count INT, currency CHAR(24), price INT, time TIMESTAMP DEFAULT CURRENT_TIMESTAMP);",
                "CREATE TABLE IF NOT EXISTS raids (id INTEGER PRIMARY KEY, profile_id VARCHAR(24), map VARCHAR(24), raid_type INT, queue_time DECIMAL(6,2), raid_id VARCHAR(24), time TIMESTAMP DEFAULT CURRENT_TIMESTAMP);",
            };
            foreach (var sql in commands)
            {
                ExecuteNonQuery(sql);
            }
        }

        private void UpdateDatabase()
        {
            foreach (var tableName in new[] { "raids", "flea_sales" })
            {
                var profileIdFieldExists = false;
                using (var command = CreateCommand($"PRAGMA table_info([{tableName}]);"))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (reader.GetString(reader.GetOrdinal("name")) == "profile_id")
                        {
                            profileIdFieldExists = true;
                            break;
                        }
                    }
                }
                if (!profileIdFieldExists)
                {
                    ExecuteNonQuery($"ALTER TABLE [{tableName}] ADD COLUMN profile_id VARCHAR(24);");
                }
            }
        }

        private int ExecuteScalarInt(string sql, Dictionary<string, object?>? parameters = null)
        {
            using var command = CreateCommand(sql, parameters: parameters);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private void ExecuteNonQuery(string sql, Dictionary<string, object?>? parameters = null)
        {
            using var command = CreateCommand(sql, parameters: parameters);
            command.ExecuteNonQuery();
        }

        private SqliteCommand CreateCommand(
            string sql,
            SqliteTransaction? transaction = null,
            Dictionary<string, object?>? parameters = null)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = transaction;
            if (parameters != null)
            {
                foreach (var parameter in parameters)
                {
                    command.Parameters.AddWithValue($"@{parameter.Key}", parameter.Value ?? DBNull.Value);
                }
            }
            return command;
        }
    }
}
