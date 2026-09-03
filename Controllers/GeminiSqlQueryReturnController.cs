using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;

namespace GenAiProject.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class GeminiSqlQueryReturnController : ControllerBase
    {
        private readonly HttpClient _httpClient;
        private readonly string _apiKey;

        public GeminiSqlQueryReturnController(IHttpClientFactory httpClientFactory)
        {
            _httpClient = httpClientFactory.CreateClient();
            _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? string.Empty;
        
        }

        [HttpPost("generate-sql")]
        public async Task<IActionResult> GenerateSql([FromBody] string requirement)
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                return BadRequest(new { Error = "API Key appsettings.json me missing hai!" });
            }

            // Updated Model Name to gemini-2.5-flash
            // Updated model string to gemini-3-flash-preview
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={_apiKey}";

            var payload = new
            {
                contents = new[]
                {
                new
                {
                    parts = new[]
                    {
                        new { text = $"You are a Senior MS SQL DBA. Convert the following requirement into a valid T-SQL query. Return ONLY raw SQL code without markdown backticks or explanation: {requirement}" }
                    }
                }
            }
            };

            // PostAsJsonAsync use karke application/json issue se bachein
            var response = await _httpClient.PostAsJsonAsync(url, payload);
            var jsonResponse = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return BadRequest(new { Error = "Gemini API Call Failed", Details = jsonResponse });
            }

            using var doc = JsonDocument.Parse(jsonResponse);
            string generatedSql = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString() ?? string.Empty;

            return Ok(new { GeneratedSqlQuery = generatedSql.Trim() });
        }
    }
}
