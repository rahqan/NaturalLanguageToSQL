using Microsoft.Data.Sqlite;

namespace NaturalLanguageToSQL.Data
{
    public class DatabaseSchemaExtractor
    {
        public static string GetDatabaseSchema(SqliteConnection con)
        {
            var schema = new List<string>();
            con.Open();

            using (var cmd = new SqliteCommand(
                "SELECT sql FROM sqlite_master WHERE type='table' AND sql IS NOT NULL", con))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                    schema.Add(reader.GetString(0) + ";");
            }

            return string.Join("\n\n", schema);
        }


    }
}
