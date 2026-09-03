using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Text;
using System.Text.Json;
using GenAiProject.DTOs;
namespace GenAiProject.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MultiHumanChatController : ControllerBase
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _connectionString;
        public MultiHumanChatController(IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _httpClient = httpClientFactory.CreateClient();
            _apiKey = configuration["GeminiSettings:ApiKey"] ?? string.Empty;
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
        }

        private async Task<string> GetDatabaseSchemaAsync()
        {
            using var connection = new SqlConnection(_connectionString);

            string schemaQuery = @"
                SELECT 
                    TABLE_NAME, 
                    COLUMN_NAME, 
                    DATA_TYPE 
                FROM INFORMATION_SCHEMA.COLUMNS 
                WHERE TABLE_SCHEMA = 'dbo'
                ORDER BY TABLE_NAME, ORDINAL_POSITION;";

            var columns = await connection.QueryAsync(schemaQuery);

            var schemaBuilder = new StringBuilder();
            string currentTable = string.Empty;

            foreach (var row in columns)
            {
                string tableName = row.TABLE_NAME;
                string columnName = row.COLUMN_NAME;
                string dataType = row.DATA_TYPE;

                if (currentTable != tableName)
                {
                    currentTable = tableName;
                    schemaBuilder.AppendLine($"\nTable: {currentTable}");
                    schemaBuilder.AppendLine("Columns:");
                }

                schemaBuilder.AppendLine($"  - {columnName} ({dataType})");
            }

            return schemaBuilder.ToString();
        }

        // Helper Method: Natural Language Summary Generator
        private async Task<string> GenerateDataSummaryAsync(string userPrompt, string executedSql, string rawDataJson)
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={_apiKey}";

            string summaryPromptText = $@"User Request: ""{userPrompt}""
Executed SQL Query: ""{executedSql}""
Returned Data JSON: {rawDataJson}

Task: Summarize the returned database result in 1-2 concise, clear natural language sentences in Hinglish/English. Focus on key numbers, names, or insights. Do not show code or raw JSON.";

            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[] { new { text = summaryPromptText } }
                    }
                }
            };

            var response = await _httpClient.PostAsJsonAsync(url, payload);
            if (!response.IsSuccessStatusCode) return "Summary generation failed.";

            var jsonResponse = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonResponse);

            return doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString()?.Trim() ?? "No summary available.";
        }

        [HttpPost("execute-chat-prompt")]
        public async Task<IActionResult> ExecuteChatPrompt([FromBody] ChatRequestDto request)
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                return BadRequest(new { Error = "API Key missing hai!" });
            }

            try
            {
                // 1. Live Schema Retrieval
                string dynamicDbSchema = await GetDatabaseSchemaAsync();

                var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={_apiKey}";

                // 2. Build Multi-Turn History Payload
                var contentsList = new List<object>();

                string systemInstruction = $"You are a Senior MS SQL DBA. Based strictly on the following Database Schema:\n{dynamicDbSchema}\n\nConvert natural language requests into valid T-SQL queries. Return ONLY the raw SQL query without backticks, sql keywords formatting, or explanation. Note: Strict adherence to schema column names is required (e.g. use 'IsActive' for active status, not 'Status'). If the user asks a follow-up, refine the previous query context.";

                foreach (var msg in request.History)
                {
                    contentsList.Add(new
                    {
                        role = msg.Role,
                        parts = new[] { new { text = msg.Text } }
                    });
                }

                string finalPromptText = request.History.Count == 0
                    ? $"{systemInstruction}\n\nUser Request: {request.CurrentPrompt}"
                    : request.CurrentPrompt;

                contentsList.Add(new
                {
                    role = "user",
                    parts = new[] { new { text = finalPromptText } }
                });

                var payload = new { contents = contentsList };

                // 3. Call Gemini to Generate SQL
                var response = await _httpClient.PostAsJsonAsync(url, payload);
                var jsonResponse = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return BadRequest(new { Error = "Gemini API Call Failed", Details = jsonResponse });
                }

                // 4. Extract SQL
                using var doc = JsonDocument.Parse(jsonResponse);
                string generatedSql = doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString() ?? string.Empty;

                generatedSql = generatedSql.Replace("```sql", "").Replace("```", "").Trim();

                // 5. Security Inspection
                if (!generatedSql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { Error = "Security Violation: Only SELECT queries are allowed!", Query = generatedSql });
                }

                // 6. Execute via Dapper
                using var dbConnection = new SqlConnection(_connectionString);
                var resultData = await dbConnection.QueryAsync(generatedSql);

                // 7. Dynamic Data Summarization (Step 2 Implementation)
                string rawDataJson = JsonSerializer.Serialize(resultData);
                string dataSummary = await GenerateDataSummaryAsync(request.CurrentPrompt, generatedSql, rawDataJson);

                // 8. History Update
                var updatedHistory = request.History;
                updatedHistory.Add(new ChatMessageDto { Role = "user", Text = request.CurrentPrompt });
                updatedHistory.Add(new ChatMessageDto { Role = "model", Text = generatedSql });

                return Ok(new
                {
                    ExecutedQuery = generatedSql,
                    Summary = dataSummary,
                    Data = resultData,
                    UpdatedHistory = updatedHistory
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = "Execution Error", Message = ex.Message });
            }
        }
    }
}
