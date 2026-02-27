# PiCrust Development Environment

# Copy and fill in your values
cp .env.example .env

# Edit .env with your credentials:
# - DISCORD_TOKEN: Your Discord bot token
# - DISCORD_CHANNEL_ID: Channel ID for the bot to listen in
# - DISCORD_OWNER_ID: Your Discord user ID (bot only responds to you)
# - ANTHROPIC_API_KEY: Your Anthropic API key

# Run development container with mounted AgentFiles for live editing
docker run -it --rm \
  --name picrust-dev \
  -p 4110:4110 \
  -v $(pwd)/PiCrust/AgentFiles:/home/picrust \
  --env-file .env \
  picrust:latest

# Or without mounting (uses files baked into image):
docker run -it --rm \
  --name picrust-dev \
  -p 4110:4110 \
  --env-file .env \
  picrust:latest

# View logs in another terminal:
docker logs -f picrust-dev

# Rebuild after making changes:
docker build -t picrust:latest ./PiCrust
