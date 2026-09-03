namespace GenAiProject.DTOs
{
    public class ChatRequestDto
    {
        public List<ChatMessageDto> History { get; set; } = new List<ChatMessageDto>();
        public string CurrentPrompt { get; set; } = string.Empty;
    }
}
