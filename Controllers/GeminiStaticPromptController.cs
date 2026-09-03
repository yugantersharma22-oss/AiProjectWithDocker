using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace GenAiProject.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class GeminiStaticPromptController : ControllerBase
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;
        private readonly string _connectionString;

        public GeminiStaticPromptController(IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _httpClient = httpClientFactory.CreateClient();
            _apiKey = configuration["GeminiSettings:ApiKey"] ?? string.Empty;
            _connectionString = configuration.GetConnectionString("DefaultConnection") ?? string.Empty;
        }

        [HttpPost("execute-prompt")]
        public async Task<IActionResult> ExecutePrompt([FromBody] string userPrompt)
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                return BadRequest(new { Error = "API Key missing hai!" });
            }

            // 1. Apne Database ka Schema Yahan Define Karein
            string dbSchema = @"
            Tables and Columns available in Database:
            - Employees (Id INT, Name VARCHAR, Salary DECIMAL, DepartmentId INT, IsActive BIT)
            - Departments (Id INT, DepartmentName VARCHAR)
        ";

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={_apiKey}";

            // 2. System Prompt: Direct T-SQL query maang rahe hain
            var payload = new
            {
                contents = new[]
                {
                new
                {
                    parts = new[]
                    {
                        new { text = $"You are a Senior MS SQL DBA. Based on this DB Schema:\n{dbSchema}\n\nConvert this request into a valid T-SQL query: {userPrompt}. Return ONLY the raw SQL query code without markdown backticks, sql keywords, formatting, or explanation." }
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

            // 3. Clean Generated SQL String
            using var doc = JsonDocument.Parse(jsonResponse);
            string generatedSql = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString() ?? string.Empty;

            generatedSql = generatedSql.Replace("```sql", "").Replace("```", "").Trim();

            // 4. Security Guardrail: Check ki sirf SELECT query ho
            if (!generatedSql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { Error = "Security Error: Only SELECT queries are allowed!", Query = generatedSql });
            }

            // 5. Dapper se MS SQL Server par query execute karke Data fetch karna
            try
            {
                using var connection = new SqlConnection(_connectionString);

                // Dynamic query execution via Dapper
                var resultData = await connection.QueryAsync(generatedSql);

                return Ok(new
                {
                    ExecutedQuery = generatedSql,
                    Data = resultData
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    Error = "Database Execution Error",
                    Message = ex.Message,
                    FailedQuery = generatedSql
                });
            }
        }
    
}
}
