# PiCrust

A containerized personal AI assistant that provides a Discord interface to the [pi](https://github.com/badlogic/pi) coding agent, featuring scheduled heartbeats and persistent memory.

```
┌─────────────────────────────────────────────────────┐
│ Docker Container (runs as picrust user)              │
│  ┌─────────────────────┐    ┌──────────────────┐   │
│  │ .NET Discord Service │───▶│ pi (RPC mode)    │   │
│  │ - Bot integration    │    │ - Extensions     │   │
│  │ - Message relay      │    │ - Skills         │   │
│  │ - Heartbeat scheduler│    │ - MEMORY.md      │   │
│  └─────────────────────┘    └──────────────────┘   │
│           /app                   /home/picrust       │
└─────────────────────────────────────────────────────┘
                                       ▲
                                       │
                                ┌──────┴───────┐
                                │ picrust-data  │
                                │ (volume)      │
                                │ - extensions/ │
                                │ - skills/     │
                                │ - prompts/    │
                                │ - sessions/   │
                                │ - MEMORY.md   │
                                └──────────────┘
```

## Inspiration & Credits

This project is **inspired by** and builds upon the work of:

- **[pi-mono/mom](https://github.com/badlogic/pi-mono/tree/main/packages/mom)** - The original Model Context Manager that pioneered the markdown-based memory architecture used here
- **[OpenClaw](https://github.com/openclaw/openclaw)** - **Credit for the markdown files**: The `AgentFiles/` directory structure and markdown files (MEMORY.md, HEARTBEAT.md, SOUL.md, USER.md, etc.) are **directly copied from OpenClaw**. This project would not exist without their excellent work on agent scaffolding and memory management

## Features

- **Discord Interface**: Send messages to the bot via DM or mentions, receive AI responses
- **Persistent Memory**: `MEMORY.md` survives container restarts via Docker volumes
- **Scheduled Heartbeat**: Periodic prompts to keep the assistant active and contextualized
- **Full pi Capabilities**: Extensions, skills, and prompt templates from the pi ecosystem
- **Volume Persistence**: All agent data in `/home/picrust` persists across rebuilds and redeployments
- **Non-root Container**: Runs as a dedicated `picrust` user for security
- **Kamal Deployment**: One-command deploy to any server

## Quick Start

### Prerequisites

- Docker Desktop
- [Kamal](https://kamal-deploy.org/) (for deployment)
- A server with SSH access (for production)
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
cp PiCrust/.env.example PiCrust/.env

# Edit with your values
nano PiCrust/.env
```

Required in `.env`:
```
DEPLOY_HOST=your.server.ip
MINIMAX_API_KEY=your_api_key_here
DISCORD_TOKEN=your_bot_token_here
OWNER_ID=your_discord_user_id
```

### Deploy to Production

```bash
# First-time setup (installs Docker, builds, and deploys)
kamal setup

# Subsequent deploys
kamal deploy

# View logs (from WSL or SSH)
kamal logs

# Shell into the container
kamal shell
```

### Local Development (Visual Studio)

1. Open `PiCrust.sln` in Visual Studio
2. Select **"Container (Dockerfile)"** launch profile
3. Press F5 to debug

The VS Container Tools will build the `base` stage, inject the debugger, and attach automatically.

### Local Development (CLI)

```bash
cd PiCrust
dotnet restore
dotnet run
```

### Test

Mention your bot in Discord or send a DM:
```
@YourBot What can you do?
```

## Configuration

### Agent Files (`AgentFiles/` directory)

Agent configuration files are copied to `/home/picrust/` inside the container. In production, a Docker volume (`picrust-data`) is mounted over `/home/picrust/` for persistence.

| File/Directory | Purpose |
|----------------|---------|
| `MEMORY.md` | Persistent long-term memory across sessions |
| `SOUL.md` | System prompt defining the assistant's identity |
| `IDENTITY.md` | Agent identity configuration |
| `USER.md` | User preferences and context |
| `HEARTBEAT.md` | Periodic heartbeat prompt template |
| `TOOLS.md` | Available tools documentation |
| `AGENTS.md` | Multi-agent configuration |
| `BOOTSTRAP.md` | Agent bootstrap instructions |
| `extensions/` | Custom TypeScript extensions |
| `skills/` | Agent skills (from [Agent Skills](https://agentskills.io)) |
| `prompts/` | Reusable prompt templates |
| `sessions/` | Conversation history |
| `memories/` | Stored memory snapshots |

### Adding Extensions

1. Place `.ts` files in `AgentFiles/extensions/` or use the provided extensions:
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
AgentFiles/skills/my-skill/
├── SKILL.md    # Skill definition
└── (other files)
```

### Adding Prompt Templates

Create markdown files in `AgentFiles/prompts/`:

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

Customize `AgentFiles/HEARTBEAT.md` to change the heartbeat behavior.

## Memory System

`MEMORY.md` persists across container restarts and redeployments through the `picrust-data` Docker volume. The `auto-memory` extension automatically updates it after significant interactions.

### Manual Memory Editing

You can edit `MEMORY.md` directly to:
- Set user preferences
- Document working context
- Add important notes that should persist

Access via SSH:
```bash
ssh root@your.server.ip
docker exec -it $(docker ps -q --filter label=service=picrust) bash
cat /home/picrust/MEMORY.md
```

## Project Structure

```
pi-shell/
├── config/
│   └── deploy.yml                # Kamal deployment configuration
├── PiCrust/
│   ├── AgentFiles/               # pi agent configuration (seeded into container)
│   │   ├── MEMORY.md             # Persistent memory
│   │   ├── HEARTBEAT.md          # Heartbeat prompt
│   │   ├── SOUL.md               # Agent identity prompt
│   │   ├── IDENTITY.md           # Agent identity config
│   │   ├── USER.md               # User context
│   │   ├── TOOLS.md              # Tools documentation
│   │   ├── AGENTS.md             # Multi-agent config
│   │   ├── BOOTSTRAP.md          # Bootstrap instructions
│   │   ├── extensions/           # TypeScript extensions
│   │   │   ├── auto-memory/      # Auto-update memory
│   │   │   ├── identity/         # Identity management
│   │   │   └── session-manager.ts
│   │   ├── skills/               # Agent skills
│   │   ├── prompts/              # Prompt templates
│   │   ├── sessions/             # Conversation history
│   │   └── memories/             # Memory snapshots
│   ├── Models/
│   │   └── Configuration.cs      # Configuration model
│   ├── Services/
│   │   ├── DiscordService.cs     # Discord bot implementation
│   │   ├── HeartbeatService.cs   # Periodic heartbeat runner
│   │   └── PiService.cs         # pi RPC client wrapper
│   ├── Properties/
│   │   └── launchSettings.json   # VS launch profiles
│   ├── Program.cs                # Application entry point
│   ├── PiCrust.csproj            # .NET project file
│   ├── Dockerfile                # Multi-stage Docker build
│   ├── .env.example              # Environment variable template
│   └── .dockerignore
├── .gitignore
└── README.md
```

## Deployment

PiCrust uses [Kamal](https://kamal-deploy.org/) for deployment. The deploy configuration is in `config/deploy.yml`.

### Environment Variables

| Variable | Where | Description |
|----------|-------|-------------|
| `DEPLOY_HOST` | `.env` | Server IP or hostname |
| `DISCORD_TOKEN` | `.env` (secret) | Discord bot token |
| `MINIMAX_API_KEY` | `.env` (secret) | AI provider API key |
| `OWNER_ID` | `.env` (secret) | Your Discord user ID |
| `ASPNETCORE_ENVIRONMENT` | Kamal (clear) | Set to `Production` by Kamal |
| `PI_CODING_AGENT_DIR` | Kamal (clear) | `/home/picrust` |

### Kamal Commands

```bash
kamal setup       # First-time deploy (installs Docker on server)
kamal deploy      # Deploy latest changes
kamal shell       # SSH into the running container
kamal app logs    # View application logs
```

> **Note**: `kamal logs` and other alias commands have SSH quoting issues on Windows. Use WSL or SSH directly for log viewing.

### Volumes

The `picrust-data` volume is mounted at `/home/picrust` and persists all agent data across deployments. On first deploy, it is seeded with the contents of `AgentFiles/`.

To inspect the volume on the server:
```bash
docker volume inspect picrust-data
sudo ls -la /var/lib/docker/volumes/picrust-data/_data/
```

## Architecture

1. **Program.cs**: Sets up dependency injection, configuration binding, and hosted services
2. **PiService**: Manages the pi subprocess in RPC mode, handles communication
3. **DiscordService**: Discord bot that relays messages between users and pi
4. **HeartbeatService**: Background service for periodic heartbeat prompts

## Security Notes

- **API Keys**: Store in `.env`, never commit to version control
- **Bot Token**: Rotate if compromised
- **Container**: Runs as non-root `picrust` user
- **Memory File**: Review before sharing logs publicly

## Acknowledgments

- **[badlogic](https://github.com/badlogic)** for creating [pi](https://github.com/badlogic/pi) and [pi-mono/mom](https://github.com/badlogic/pi-mono/tree/main/packages/mom)
- **[OpenClaw](https://github.com/openclaw/openclaw)** for the excellent markdown-based agent scaffolding and directory structure

## License

Unlicense
