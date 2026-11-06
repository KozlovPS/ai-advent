namespace ConsoleApp
{
    public class ChatResult
    {
        public string Content { get; set; } = "";
        public string FinishReason { get; set; } = "";
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }
    }
}
