using System;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace NaturalLanguageToSQL.Plugin
{
    public class NlqRetrievalPlugin
    {
        private readonly Kernel _kernel;
        private readonly ChatHistory _fullChat;
        private ChatHistory _recentChat;
        private ChatHistory _summarizedChat;

        private readonly IConfiguration _config;
        private readonly ChatHistorySummarizationReducer _historySummarizer;
        private readonly IChatCompletionService _chatService;
        private readonly ChatHistoryTruncationReducer _historyTruncater;

        public NlqRetrievalPlugin(
            Kernel kernel,
            ChatHistory chat,
            IConfiguration config,
            ChatHistory recentChat,
            ChatHistory summarizedChat
        )
        {
            _kernel = kernel;
            _fullChat = chat;
            _recentChat = recentChat;
            _config = config;
            _summarizedChat = summarizedChat;

            _chatService = kernel.GetRequiredService<IChatCompletionService>();
            _historySummarizer = new ChatHistorySummarizationReducer(_chatService, targetCount: 4);
            _historyTruncater = new ChatHistoryTruncationReducer(targetCount: 4);
        }

        private bool IsSafeSelectQuery(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
                return false;

            string normalized = sql.Trim().ToLowerInvariant();

            if (normalized.Contains(";") || normalized.Contains("--") ||
                normalized.Contains("/*") || normalized.Contains("*/"))
                return false;

            StringBuilder sb = new StringBuilder(normalized.Length);

            foreach (char c in normalized)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
                else
                    sb.Append(' ');  
            }

            string cleaned = sb.ToString();
            string[] words = cleaned.Split(' ');

            HashSet<string> forbidden = new HashSet<string>
    {
        "insert", "update", "delete", "drop", "alter",
        "create", "replace", "truncate", "attach", "detach",
        "pragma", "vacuum", "reindex", "begin", "commit",
        "rollback", "exec", "execute"
    };

            foreach (string word in words)
            {
                if (forbidden.Contains(word))
                    return false;
            }

            return true;
        }


        [KernelFunction, Description("Ask natural language question to query the database.")]
        public async Task<object> AskDb(
    [Description("The natural language question.")] string question
)
        {
            _fullChat.AddUserMessage(question);
            _recentChat.AddUserMessage(question);
            _summarizedChat.AddUserMessage(question);

            var summarizedChatString = string.Join("\n",
                _summarizedChat.ToImmutableList().Select(m => $"{m.Role}: {m.Content}"));
            var recentChatString = string.Join("\n",
                _recentChat.ToImmutableList().Select(m => $"{m.Role}: {m.Content}"));

            string fullschema = Data.DatabaseSchemaExtractor.GetDatabaseSchema(
                new SqliteConnection(_config.GetConnectionString("SQLite")));

            // 1. Get relevant schema
            string relevantSchemaPromptTemplate = File.ReadAllText("./Plugin/Prompts/RelevantSchema.txt");
            string relevantSchemaPrompt = relevantSchemaPromptTemplate
                .Replace("{{SCHEMA}}", fullschema)
                .Replace("{{CONVERSATION}}", summarizedChatString)
                .Replace("{{RECENT}}", recentChatString)
                .Replace("{{USER}}", question);


            var relevantSchemaResult = await _kernel.InvokePromptAsync(relevantSchemaPrompt);
            string relevantSchema = relevantSchemaResult.ToString().Trim();



            // 2. Generate SQL
            string promptTemplate = File.ReadAllText("./Plugin/Prompts/NLQtoSQL.txt");
            string sqlPrompt = promptTemplate
                .Replace("{{SCHEMA}}", relevantSchema)
                .Replace("{{CONVERSATION}}", summarizedChatString)
                .Replace("{{RECENT}}", recentChatString)
                .Replace("{{USER}}", question);

            var sql = await _kernel.InvokePromptAsync(sqlPrompt);
            string query = CleanSqlOutput(sql.ToString());

            //Console.WriteLine("Generated SQL Query: " + query);

            if (!IsSafeSelectQuery(query))
            {
                Console.WriteLine("Generated SQL query is not a safe SELECT statement.");
                return new { sql = string.Empty, results = new List<Dictionary<string, object>>() };
            }

            string cs = _config.GetConnectionString("SQLite");
            using var con = new SqliteConnection(cs);
            await con.OpenAsync();

            List<Dictionary<string, object>> results;

            try
            {
                results = await ExecuteQuery(con, query);
            }
            catch (Exception sqlError)
            {
                Console.WriteLine($"Error executing SQL: {sqlError.Message}. Retrying...");

                string retryPromptTemplate = File.ReadAllText("./Plugin/Prompts/RetryPrompt.txt");
                string retryPrompt = retryPromptTemplate
                    .Replace("{{SCHEMA}}", fullschema)  
                    .Replace("{{CONVERSATION}}", summarizedChatString)
                    .Replace("{{RECENT}}", recentChatString)
                    .Replace("{{USER}}", question)
                    .Replace("{{FAILED_SQL}}", query)
                    .Replace("{{ERROR}}", sqlError.Message.Substring(0, Math.Min(sqlError.Message.Length, 50)));
                
                var retrySql = await _kernel.InvokePromptAsync(retryPrompt);
                string retryQuery = CleanSqlOutput(retrySql.ToString());

                Console.WriteLine("Retry SQL Query: " + retryQuery);

                if (!IsSafeSelectQuery(retryQuery))
                {
                    Console.WriteLine("Retry SQL query is not a safe SELECT statement.");
                    return new { sql = string.Empty, results = new List<Dictionary<string, object>>() };
                }

                try
                {
                    results = await ExecuteQuery(con, retryQuery);
                    query = retryQuery; // Use successful retry query
                }
                catch (Exception retryError)
                {
                    Console.WriteLine($"Retry also failed: {retryError.Message}");
                    return new
                    {
                        sql = retryQuery,
                        results = new List<Dictionary<string, object>>(),
                        error = $"Both attempts failed. Last error: {retryError.Message}"
                    };
                }
            }

            StringBuilder sb = new StringBuilder();
            foreach (var row in results)
            {
                sb.AppendLine(string.Join(", ", row.Select(kv => $"{kv.Key}: {kv.Value}")));
            }

            string answerText = results.Count == 0 ? "No results found" : sb.ToString().Trim();

            _fullChat.AddAssistantMessage(answerText);
            _recentChat.AddAssistantMessage(answerText);
            _summarizedChat.AddAssistantMessage(answerText);

            await _recentChat.ReduceInPlaceAsync(_historyTruncater, CancellationToken.None);
            await _summarizedChat.ReduceInPlaceAsync(_historySummarizer, CancellationToken.None);

            
            Console.WriteLine("-----");
            foreach (var item in _recentChat)
                Console.WriteLine($"Recent Chat - {item.Role}: {item.Content}");
            Console.WriteLine("-----");
            foreach (var item in _summarizedChat)
                Console.WriteLine($"Summarized Chat - {item.Role}: {item.Content}");
            Console.WriteLine("-----");

            return new { sql = query, results };
        }

        // Helper methods
        private string CleanSqlOutput(string sql)
        {
            return sql
                .Trim()
                .Replace("```sql", "", StringComparison.OrdinalIgnoreCase)
                .Replace("```", "")
                .Replace("\n", " ")
                .Replace("\r", "")
                .Trim();
        }

        private async Task<List<Dictionary<string, object>>> ExecuteQuery(
            SqliteConnection con,
            string query)
        {
            using var cmd = new SqliteCommand(query, con);
            cmd.CommandTimeout = 30;
            Stopwatch stopwatch = Stopwatch.StartNew();
            using var reader = await cmd.ExecuteReaderAsync();

            var results = new List<Dictionary<string, object>>();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.GetValue(i);
                results.Add(row);
            }
            stopwatch.Stop();
            Console.WriteLine($"Query executed in {stopwatch.ElapsedMilliseconds} ms, returned {results.Count} rows.");
            return results;
        }
    }
}
