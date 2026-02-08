# PiShell

A containerized personal AI assistant that provides a Discord interface to the [pi](https://github.com/badlogic/pi) coding agent, featuring scheduled heartbeats and persistent memory.

```
┌─────────────────────────────────────────────────────┐
│ Docker Container                                     │
│  ┌─────────────────────┐    ┌──────────────────┐  │
│  │ .NET Discord Service │───▶│ pi (RPC mode)    │  │
│  │ - Bot integration    │    │ - Extensions     │  │
│  │ - Message relay      │    │ - Skills         │  │
│  │ - Scheduler (cron)   │    │ - MEMORY.md      │  │
│  └─────────────────────┘    │ - HEARTBEAT.md    │  │
│                             └──────────────────┘  │
└─────────────────────────────────────────────────────┘
         ▲                           ▲
         │                           │
    ┌────┴────┐              ┌───────┴───────┐
    │ .NET     │              │ .pi/          │
    │ Config   │              │ - extensions/ │
    │ (volume) │              │ - skills/     │
    └──────────┘              │ - prompts/    │
                              │ - sessions/   │
                              │ - MEMORY.md   │
                              └───────────────┘
```

## Inspiration & Credits

This project is **inspired by** and builds upon the work of:

- **[pi-mono/mom](https://github.com/badlogic/pi-mono/tree/main/packages/mom)** - The original Model Context Manager that pioneered the markdown-based memory architecture used here
- **[OpenClaw](https://github.com/openclaw/openclaw)** - **Credit for the markdown files**: The `.pi/` directory structure and markdown files (MEMORY.md, HEARTBEAT.md, SOUL.md, USER.md, etc.) are **directly copied from OpenClaw**. This project would not exist without their excellent work on agent scaffolding and memory management

## Features

- **Discord Interface**: Send messages to the bot via DM or mentions, receive AI responses
- **Persistent Memory**: `MEMORY.md` survives container restarts and is automatically updated
- **Scheduled Heartbeat**: Periodic health checks to keep the assistant active and contextualized
- **Full pi Capabilities**: Extensions, skills, and prompt templates from the pi ecosystem
- **Volume Persistence**: All configuration and data persist across rebuilds
- **Containerized**: Runs entirely in Docker for easy deployment

## Quick Start

### Prerequisites

- Docker & Docker Compose
- Discord Bot Token
- MiniMax API Key (or compatible LLM provider)

### Create Discord Bot

1. Go to [Discord Developer Portal](https://discord.com/developers/applications)
2. Create a New Application
3. Go to "Bot" section → "Add Bot"
4. Copy the **Bot Token**
5. Enable "Message Content Intent" in Bot settings
6. Invite the bot to your server with appropriate permissions

### Configure

```bash
# Copy environment template
cp .env.example .env

# Edit with your values
nano .env
```

Required in `.env`:
```
MINIMAX_API_KEY=your_api_key_here
DISCORD_TOKEN=your_bot_token_here
OWNER_ID=your_discord_user_id
PI_CODING_AGENT_DIR=/app/.pi
```

### Build & Run

```bash
# Build the image
docker compose build

# Start the service
docker compose up -d

# View logs
docker compose logs -f
```

### Test

Mention your bot in Discord or send a DM:
```
@YourBot What can you do?
```

## Configuration

### pi Configuration (`.pi/` directory)

The `.pi/` directory is mounted at the path specified by `PI_CODING_AGENT_DIR` (default: `/app/.pi` inside container):

| File/Directory | Purpose |
|----------------|---------|
| `MEMORY.md` | Persistent long-term memory across sessions |
| `SOUL.md` | System prompt defining the assistant's identity |
| `USER.md` | User preferences and context |
| `HEARTBEAT.md` | Periodic heartbeat prompt template |
| `TOOLS.md` | Available tools documentation |
| `extensions/` | Custom TypeScript extensions |
| `skills/` | Agent skills (from [Agent Skills](https://agentskills.io)) |
| `prompts/` | Reusable prompt templates |
| `sessions/` | Conversation history |

### Adding Extensions

1. Place `.ts` files in `extensions/` or use the provided extensions:
   - `auto-memory` - Automatically updates MEMORY.md after significant interactions
   - `identity` - Manages agent identity and context
   - `session-manager` - Handles conversation session persistence

2. pi auto-discovers and loads extensions on startup

Example extension:
```typescript
export default function (pi: ExtensionAPI) {
    pi.registerTool({ name: "my_tool", ... });
    pi.on("agent_end", async (event) => { /* ... */ });
    pi.log("My extension loaded");
}
```

### Adding Skills

Create skill directories following the [Agent Skills](https://agentskills.io) standard:

```
skills/my-skill/
├── SKILL.md    # Skill definition
└── (other files)
```

### Adding Prompt Templates

Create markdown files in `prompts/`:

```markdown
<!-- prompts/review.md -->
Review this code for bugs:
{{code}}
```

## Heartbeat System

The heartbeat scheduler runs every 30 minutes (configurable via `HEARTBEAT_INTERVAL_MINUTES`) to:

1. Query the current session state
2. Send a heartbeat prompt to summarize progress
3. Keep the assistant contextualized and active

Customize `HEARTBEAT.md` to change the heartbeat behavior.

## Memory System

`MEMORY.md` persists across container restarts through Docker volumes. The `auto-memory` extension significant interactions.

 automatically updates it after### Manual Memory Editing

You can edit `MEMORY.md` directly to:
- Set user preferences
- Document working context
- Add important notes that should persist

## Project Structure

```
PiShell/
├── .pi/                          # pi configuration (from OpenClaw)
│   ├── MEMORY.md                 # Persistent memory
│   ├── HEARTBEAT.md              # Heartbeat prompt
│   ├── SOUL.md                   # Agent identity
│   ├── USER.md                   # User context
│   ├── TOOLS.md                  # Tools documentation
│   ├── extensions/
│   │   ├── auto-memory/          # Auto-update memory
│   │   └── identity/             # Identity management
│   ├── skills/                   # Agent skills
│   ├── prompts/                  # Prompt templates
│   └── sessions/                 # Conversation history
├── Pi/
│   ├── PiClient.cs               # pi RPC client wrapper
│   └── SystemPromptBuilder.cs    # Builds system prompt from .pi files
├── Discord/
│   └── DiscordService.cs         # Discord bot implementation
├── Scheduler/
│   └── HeartbeatScheduler.cs     # Periodic heartbeat runner
├── Configuration.cs              # Configuration model
├── Program.cs                    # Application entry point
├── Dockerfile
├── docker-compose.yml
├── .env.example
└── README.md
```

## Development

### Running Locally (without Docker)

```bash
# Install dependencies
dotnet restore

# Run the bot
dotnet run --project PiShell
```

The application will start pi as a subprocess in RPC mode.

### Architecture

1. **Program.cs**: Sets up dependency injection and configuration binding
2. **PiClient**: Manages the pi subprocess, handles RPC communication
3. **DiscordService**: Discord bot that relays messages between users and pi
4. **HeartbeatScheduler**: Background service for periodic health checks

## Security Notes

- **API Keys**: Store in `.env`, never commit to version control
- **Bot Token**: Rotate if compromised
- **Container**: Consider running with non-root user for production
- **Memory File**: Review before sharing logs publicly

## Acknowledgments

- **[badlogic](https://github.com/badlogic)** for creating [pi](https://github.com/badlogic/pi) and [pi-mono/mom](https://github.com/badlogic/pi-mono/tree/main/packages/mom)
- **[OpenClaw](https://github.com/openclaw/openclaw)** for the excellent markdown-based agent scaffolding and directory structure

## License

MIT
