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
    public class SingleChatDynamicPromptController : ControllerBase
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _connectionString;

        public SingleChatDynamicPromptController(IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _httpClient = httpClientFactory.CreateClient();
            _apiKey = configuration["GeminiSettings:ApiKey"] ?? string.Empty;
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
        }

        // Dynamic Helper Method: SQL Server se Saare Tables aur Columns Auto-Fetch karega
        private async Task<string> GetDatabaseSchemaAsync()
        {
            using var connection = new SqlConnection(_connectionString);

            // SQL System View se Active Tables aur Unke Columns fetch karne ki query
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

        [HttpPost("execute-prompt-dynamic")]
        public async Task<IActionResult> ExecutePromptDynamic([FromBody] string userPrompt)
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                return BadRequest(new { Error = "API Key missing hai!" });
            }

            try
            {
                // 1. Dynamic Database Schema Auto-Fetch
                string dynamicDbSchema = await GetDatabaseSchemaAsync();

                var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={_apiKey}";

                // 2. Prompt me Dynamic Schema pass karein
                var payload = new
                {
                    contents = new[]
                    {
                    new
                    {
                        parts = new[]
                        {
                            new { text = $"You are a Senior MS SQL DBA. Based on the following live Database Schema:\n{dynamicDbSchema}\n\nConvert this natural language request into a valid T-SQL query: {userPrompt}. Return ONLY the raw SQL query code without markdown backticks, sql keywords, formatting, or explanation." }
                        }
                    }
                }
                };

                var response = await _httpClient.PostAsJsonAsync(url, payload);
                var jsonResponse = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return BadRequest(new { Error = "Gemini API Call Failed", Details = jsonResponse });
                }

                // 3. Extract SQL Query
                using var doc = JsonDocument.Parse(jsonResponse);
                string generatedSql = doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString() ?? string.Empty;

                generatedSql = generatedSql.Replace("```sql", "").Replace("```", "").Trim();

                // 4. Security Inspection: Only SELECT Query
                if (!generatedSql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new { Error = "Security Error: Only SELECT queries are allowed!", Query = generatedSql });
                }

                // 5. Execute via Dapper
                using var dbConnection = new SqlConnection(_connectionString);
                var resultData = await dbConnection.QueryAsync(generatedSql);

                return Ok(new
                {
                    AutoFetchedSchema = dynamicDbSchema.Trim(),
                    ExecutedQuery = generatedSql,
                    Data = resultData
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Error = "Execution Error",
                    Message = ex.Message
                });
            }
        }
    }
}
