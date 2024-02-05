using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data.SQLite;
using System.Windows.Forms;

namespace TarkovMonitor
{
    internal class Stats
    {
        private static string DatabasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TarkovMonitor", "TarkovMonitor.db");
        private static string ConnectionString = $"Data Source={DatabasePath};Version=3;";
        private static SQLiteConnection Connection;
        static Stats()
        {
            Connection = new SQLiteConnection(ConnectionString);
            Connection.Open();

            List<string> createTableCommands = new()
            {
                "CREATE TABLE IF NOT EXISTS flea_sales (id INTEGER PRIMARY KEY, item_id CHAR(24), buyer VARCHAR(14), count INT, currency CHAR(24), price INT, time TIMESTAMP DEFAULT CURRENT_TIMESTAMP);",
                "CREATE TABLE IF NOT EXISTS raids (id INTEGER PRIMARY KEY, map VARCHAR(24), raid_type INT, queue_time DECIMAL(6,2), time TIMESTAMP DEFAULT CURRENT_TIMESTAMP);",
            };
            foreach (var commandText in createTableCommands)
            {
                using var command = new SQLiteCommand(Connection);
                command.CommandText = commandText;
                command.ExecuteNonQuery();
            }
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

        public static void AddFleaSale(FleaSoldMessageEventArgs e)
        {
            var sql = "INSERT INTO flea_sales(item_id, buyer, count, currency, price) VALUES(@item_id, @buyer, @count, @currency, @price);";
            var parameters = new Dictionary<string, object>
            {
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
                }
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
            var sql = "INSERT INTO raids(map, raid_type, queue_time) VALUES (@map, @raid_type, @queue_time);";
            var parameters = new Dictionary<string, object> {
                {
                    "map", e.RaidInfo.Map
                },
                {
                    "raid_type", e.RaidInfo.RaidType
                },
                {
                    "queue_time", e.RaidInfo.QueueTime
                }
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
    }
}
