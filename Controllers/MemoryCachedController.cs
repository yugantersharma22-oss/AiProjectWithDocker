using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using System.Text;
using System.Text.Json;
using GenAiProject.DTOs;

namespace GenAiProject.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MemoryCachedController : ControllerBase
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _connectionString;
        private readonly IMemoryCache _cache;
        public MemoryCachedController(
            IHttpClientFactory httpClientFactory,
            IConfiguration configuration,
            IMemoryCache cache)
        {
            _httpClient = httpClientFactory.CreateClient();
            _apiKey = configuration["GeminiSettings:ApiKey"] ?? string.Empty;
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
            _cache = cache;
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

        private async Task<string> GenerateDataSummaryAsync(string userPrompt, string executedSql, string rawDataJson)
        {
            // var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={_apiKey}";
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.6-flash:generateContent?key={_apiKey}";

            string summaryPromptText = $@"User Request: ""{userPrompt}""
Executed SQL Query: ""{executedSql}""
Returned Data JSON: {rawDataJson}

Task: Summarize the returned database result in 1-2 concise sentences in natural language (Hinglish/English). Focus on key figures or insights. Do not show code or raw JSON.";

            var payload = new
            {
                contents = new[]
                {
                    new { parts = new[] { new { text = summaryPromptText } } }
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
        public async Task<IActionResult> ExecuteChatPrompt([FromBody] ChatRequestCachedDto request)
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                return BadRequest(new { Error = "API Key missing hai!" });
            }

            try
            {
                // 1. Fetch Existing Chat History from Server Cache
              // Step 1 ki jagah ye line likhein:
string cacheKey = "ChatHistory_GlobalSession";
var sessionHistory = _cache.Get<List<ChatMessageDto>>(cacheKey) ?? new List<ChatMessageDto>();
                // 2. Fetch Live Schema
                string dynamicDbSchema = await GetDatabaseSchemaAsync();
                // var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={_apiKey}";
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.6-flash:generateContent?key={_apiKey}";

                // 3. Build Gemini Payload using Server History
                var contentsList = new List<object>();

//                string systemInstruction = $@"You are an expert MS SQL DBA. 
//Database Schema:
//{dynamicDbSchema}

//RULES FOR SQL GENERATION:
//1. ONLY return a valid raw T-SQL SELECT query. No explanations, no markdown backticks.
//2. Strict schema alignment: Use exact column names from the schema (e.g., 'IsActive' for status).
//3. CONTEXT HANDLING:
//   - IF the user request is a follow-up or refinement (e.g., 'inme se', 'filter', 'sort', 'from these'), MODIFY and EXTEND the previous SQL query.
//   - IF the user request is a NEW independent question or switches topic, IGNORE previous history context and generate a FRESH query from scratch.";

               string systemInstruction = $@"You are an expert MS SQL DBA. 
Database Schema:{dynamicDbSchema}

RULES FOR SQL GENERATION:
1. STRICTLY ONLY return a valid raw T-SQL SELECT query. DO NOT include markdown backticks (like ```sql), DO NOT include any explanations, greetings, comments, or conversational text. The response must start with SELECT and end with a semicolon.
2. Strict schema alignment: Use exact column names from the schema (e.g., 'IsActive' for status).
3. CONTEXT HANDLING:
   - IF the user request is a follow-up or refinement (e.g., 'inme se', 'filter', 'sort', 'from these'), MODIFY and EXTEND the previous SQL query.
   - IF the user request is a NEW independent question or switches topic, IGNORE previous history context and generate a FRESH query from scratch.
4. ABSOLUTE BAN ON CHATBOT MODE: Never generate markdown tables, mock data, or explanations, even if the user switches topics or asks for something not directly named in the schema. You are purely a database query generator.";

                foreach (var msg in sessionHistory)
                {
                    contentsList.Add(new
                    {
                        role = msg.Role,
                        parts = new[] { new { text = msg.Text } }
                    });
                }

                string finalPromptText = sessionHistory.Count == 0
                    ? $"{systemInstruction}\n\nUser Request: {request.CurrentPrompt}"
                    : request.CurrentPrompt;

                contentsList.Add(new
                {
                    role = "user",
                    parts = new[] { new { text = finalPromptText } }
                });

                var payload = new { contents = contentsList };

                // 4. Call Gemini API
                var response = await _httpClient.PostAsJsonAsync(url, payload);
                var jsonResponse = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return BadRequest(new { Error = "Gemini API Call Failed", Details = jsonResponse });
                }

                // 5. Extract SQL
                using var doc = JsonDocument.Parse(jsonResponse);
                string generatedSql = doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString() ?? string.Empty;

                generatedSql = generatedSql.Replace("```sql", "").Replace("```", "").Trim();

                if (!generatedSql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { Error = "Security Violation: Only SELECT queries are allowed!", Query = generatedSql });
                }

                // 6. Execute SQL
                using var dbConnection = new SqlConnection(_connectionString);
                var resultData = await dbConnection.QueryAsync(generatedSql);

                // 7. Generate Data Summary
                string rawDataJson = JsonSerializer.Serialize(resultData);
                string dataSummary = await GenerateDataSummaryAsync(request.CurrentPrompt, generatedSql, rawDataJson);

                // 8. Update Server Cache History
                sessionHistory.Add(new ChatMessageDto { Role = "user", Text = request.CurrentPrompt });
                sessionHistory.Add(new ChatMessageDto { Role = "model", Text = generatedSql });

                // Cache for 30 minutes
                _cache.Set(cacheKey, sessionHistory, TimeSpan.FromMinutes(30));

                return Ok(new
                {
                    ExecutedQuery = generatedSql,
                    Summary = dataSummary,
                    Data = resultData,
                    FullHistory = sessionHistory
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { Error = "Execution Error", Message = ex.Message });
            }
        }

        // Endpoint to clear session history if needed
        [HttpDelete("clear-history/{sessionId}")]
        public IActionResult ClearHistory(string sessionId = "default-session")
        {
            string cacheKey = $"ChatHistory_{sessionId}";
            _cache.Remove(cacheKey);
            return Ok(new { Message = $"Session '{sessionId}' history cleared successfully." });
        }
    
}
}
