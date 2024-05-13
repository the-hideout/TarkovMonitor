using System.Data.SQLite;
// do not upgrade to 1.0.118
// newer version throws an error after being compiled as single file assembly

namespace TarkovMonitor
{
    internal class Stats
    {
        public static string DatabasePath => Path.Join(Application.UserAppDataPath, "TarkovMonitor.db");
        public static string ConnectionString => $"Data Source={DatabasePath};Version=3;";
        private static SQLiteConnection Connection;
        static Stats()
        {
			Connection = new SQLiteConnection(ConnectionString);
            Connection.Open();

            List<string> createTableCommands = new()
            {
                "CREATE TABLE IF NOT EXISTS flea_sales (id INTEGER PRIMARY KEY, profile_id VARCHAR(24), item_id CHAR(24), buyer VARCHAR(14), count INT, currency CHAR(24), price INT, time TIMESTAMP DEFAULT CURRENT_TIMESTAMP);",
                "CREATE TABLE IF NOT EXISTS raids (id INTEGER PRIMARY KEY, profile_id VARCHAR(24), map VARCHAR(24), raid_type INT, queue_time DECIMAL(6,2), raid_id VARCHAR(24), time TIMESTAMP DEFAULT CURRENT_TIMESTAMP);",
            };
            foreach (var commandText in createTableCommands)
            {
                using var command = new SQLiteCommand(Connection);
                command.CommandText = commandText;
                command.ExecuteNonQuery();
            }

            UpdateDatabase();
        }

        public static void ClearData()
        {
            Query("PRAGMA foreign_keys=off;");
            var reader = Query("SELECT name FROM sqlite_master WHERE type='table';");
            while (reader.Read())
            {
                var tableName = reader.GetString(0);
                Query($"DELETE FROM {tableName};");
            }
            Query("PRAGMA foreign_keys=on;");
        }

        private static SQLiteDataReader Query(string query, Dictionary<string,object> parameters)
        {
            using var command = new SQLiteCommand(Connection);
            command.CommandText = query;
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue($"@{parameter.Key}", parameter.Value);
            }
            return command.ExecuteReader();
        }
        private static SQLiteDataReader Query (string query)
        {
            return Query(query, new());
        }

        public static void AddFleaSale(FleaSoldMessageLogContent e, Profile profile)
        {
            var sql = "INSERT INTO flea_sales(profile_id, item_id, buyer, count, currency, price) VALUES(@item_id, @buyer, @count, @currency, @price);";
            var parameters = new Dictionary<string, object>
            {
                {
                    "profile_id", profile.Id
                },
                {
                    "item_id", e.SoldItemId
                },
                {
                    "buyer", e.Buyer
                },
                {
                    "count", e.SoldItemCount
                },
                {
                    "currency", e.ReceivedItems.ElementAt(0).Key
                },
                {
                    "price", e.ReceivedItems.ElementAt(0).Value
                },
            };
            Query(sql, parameters);
        }
        public static int GetTotalSales(string currency)
        {
            var reader = Query("SELECT SUM(price) as total FROM flea_sales WHERE currency = @currency", new() { { "currency", currency } });
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }
                return reader.GetInt32(0);
            }
            return 0;
		}
        public static void AddRaid(RaidInfoEventArgs e)
        {
            var sql = "INSERT INTO raids(map, raid_type, queue_time, raid_id) VALUES (@map, @raid_type, @queue_time, @raid_id);";
            var parameters = new Dictionary<string, object> {
                {
                    "profile_id", e.Profile.Id
                },
                {
                    "map", e.RaidInfo.Map
                },
                {
                    "raid_type", e.RaidInfo.RaidType
                },
                {
                    "queue_time", e.RaidInfo.QueueTime
                },
                {
                    "raid_id", e.RaidInfo.RaidId
                },
            };
            Query(sql, parameters);
        }
        public static int GetTotalRaids(string mapNameId)
        {
            var reader = Query("SELECT COUNT(id) as total FROM raids WHERE map = @map", new() { { "map", mapNameId } });
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }
                return reader.GetInt32(0);
            }
            return 0;
        }
        public static Dictionary<string, int> GetTotalRaidsPerMap(RaidType raidType)
        {
            Dictionary<string, int> mapTotals = new();
            var reader = Query("SELECT map, COUNT(id) as total FROM raids WHERE raid_type = @raid_type GROUP BY map", new() { { "raid_type", raidType } });
            while (reader.Read())
            {
                if (reader.IsDBNull(1))
                {
                    mapTotals[reader.GetString(0)] = 0;
                    continue;
                }
                mapTotals[reader.GetString(0)] = reader.GetInt32(1);
            }
            Dictionary<string, int> raidsPerMap = new();
            foreach (var map in TarkovDev.Maps)
            {
                raidsPerMap[map.name] = 0;
                if (mapTotals.ContainsKey(map.nameId))
                {
                    raidsPerMap[map.name] = mapTotals[map.nameId];
                }
            }
            return raidsPerMap;
        }

        private static void UpdateDatabase()
        {
            List<string> db_tables = new() { "raids", "flea_sales" };
            foreach (var tableName in db_tables)
            {
                var profileIdFieldExists = false;
                var result = Query($"PRAGMA table_info({tableName});");
                while (result.Read())
                {
                    for (int i = 0; i < result.FieldCount; i++)
                    {
                        if (result.GetName(i) != "name")
                        {
                            continue;
                        }
                        if (result.GetString(i) == "profile_id")
                        {
                            profileIdFieldExists = true;
                            break;
                        }
                    }
                }
                if (!profileIdFieldExists)
                {
                    using var command = new SQLiteCommand(Connection);
                    command.CommandText = $"ALTER TABLE {tableName} ADD COLUMN profile_id VARCHAR(24)";
                    command.ExecuteNonQuery();
                }

            }
        }
    }
}
