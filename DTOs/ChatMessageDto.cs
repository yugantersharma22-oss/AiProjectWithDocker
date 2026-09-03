namespace GenAiProject.DTOs
{
    public class ChatMessageDto
    {
        public string Role { get; set; } = "user"; // "user" ya "model"
        public string Text { get; set; } = string.Empty;
    }
}
