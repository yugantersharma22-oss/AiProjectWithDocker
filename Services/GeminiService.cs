namespace GenAiProject.Services
{
    public class GeminiService
    {
        private readonly string _apiKey;

        public GeminiService()
        {
            // Yeh system ya environment se automatic key utha lega
            _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        }

        public string GetApiKey()
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                throw new Exception("Gemini API Key is missing from environment variables!");
            }
            return _apiKey;
        }
    }
}