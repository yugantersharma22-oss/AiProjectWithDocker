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
    public class MultiChatDynamicController : ControllerBase
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _connectionString;
        public MultiChatDynamicController(IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _httpClient = httpClientFactory.CreateClient();
            _apiKey = configuration["GeminiSettings:ApiKey"] ?? string.Empty;
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
        }

        // Helper Method: Live DB Schema Auto-Fetcher
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

        [HttpPost("execute-chat-prompt")]
        public async Task<IActionResult> ExecuteChatPrompt([FromBody] ChatRequestDto request)
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                return BadRequest(new { Error = "API Key missing hai!" });
            }

            try
            {
                // 1. Dynamic DB Schema Get Karein
                string dynamicDbSchema = await GetDatabaseSchemaAsync();

                var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={_apiKey}";

                // 2. Chat Payload Build Karein
                var contentsList = new List<object>();

                string systemInstruction = $"You are a Senior MS SQL DBA. Based strictly on the following Database Schema:\n{dynamicDbSchema}\n\nConvert natural language requests into valid T-SQL queries. Return ONLY the raw SQL query without backticks, sql keywords formatting, or explanation. Note: Strict adherence to schema column names is required (e.g. use 'IsActive' for employee active status, not 'Status'). If the user asks a follow-up, refine the previous query context.";

                // Purani Chat History Loop
                foreach (var msg in request.History)
                {
                    contentsList.Add(new
                    {
                        role = msg.Role,
                        parts = new[] { new { text = msg.Text } }
                    });
                }

                // First Turn me Schema attach karo, baad me direct user prompt
                string finalPromptText = request.History.Count == 0
                    ? $"{systemInstruction}\n\nUser Request: {request.CurrentPrompt}"
                    : request.CurrentPrompt;

                contentsList.Add(new
                {
                    role = "user",
                    parts = new[] { new { text = finalPromptText } }
                });

                var payload = new { contents = contentsList };

                // 3. Gemini API Call
                var response = await _httpClient.PostAsJsonAsync(url, payload);
                var jsonResponse = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return BadRequest(new { Error = "Gemini API Call Failed", Details = jsonResponse });
                }

                // 4. SQL Extract & Clean
                using var doc = JsonDocument.Parse(jsonResponse);
                string generatedSql = doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString() ?? string.Empty;

                generatedSql = generatedSql.Replace("```sql", "").Replace("```", "").Trim();

                // 5. Security Guardrail: Only SELECT Queries
                if (!generatedSql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { Error = "Security Violation: Only SELECT queries are allowed!", Query = generatedSql });
                }

                // 6. Dapper Execution
                using var dbConnection = new SqlConnection(_connectionString);
                var resultData = await dbConnection.QueryAsync(generatedSql);

                // History Update for Next Turn
                var updatedHistory = request.History;
                updatedHistory.Add(new ChatMessageDto { Role = "user", Text = request.CurrentPrompt });
                updatedHistory.Add(new ChatMessageDto { Role = "model", Text = generatedSql });

                return Ok(new
                {
                    ExecutedQuery = generatedSql,
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
