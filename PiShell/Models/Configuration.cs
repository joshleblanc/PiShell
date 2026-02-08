namespace PiShell.Models
{
    public class Configuration
    {
        public string MiniMaxApiKey { get; set; } = string.Empty;
        public string DiscordToken { get; set; } = string.Empty;
        public string PiCodingAgentDir { get; set; } = string.Empty;
        public ulong OwnerId { get; set; }
        public int HeartbeatIntervalMinutes { get; set; }
    }
}
