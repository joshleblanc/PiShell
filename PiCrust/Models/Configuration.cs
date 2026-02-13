namespace PiCrust.Models
{
    public class Configuration
    {
        public string MiniMaxApiKey { get; set; } = string.Empty;
        public string DiscordToken { get; set; } = string.Empty;
        public string PiCodingAgentDir { get; set; } = string.Empty;
        public ulong OwnerId { get; set; }
        public int HeartbeatIntervalMinutes { get; set; }
        
        // Provider/Model configuration for pi
        // Can be specified in two ways:
        // 1. PI_PROVIDER and PI_MODEL separate
        // 2. PI_MODEL in "provider/model" format (takes precedence if both set)
        public string PiProvider { get; set; } = string.Empty;
        public string PiModel { get; set; } = string.Empty;
    }
}
