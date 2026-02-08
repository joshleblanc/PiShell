#!/bin/sh

# Seed agent files from the staging directory into the bind-mounted volume.
# Uses cp -rn to avoid overwriting existing files (preserves runtime state
# like memories, sessions, etc.)
cp -rn /opt/agent-files/* /home/picrust/ 2>/dev/null || true
cp -rn /opt/agent-files/.* /home/picrust/ 2>/dev/null || true

chown -R picrust:picrust /home/picrust

# Drop to picrust user and run the application
exec su picrust -s /bin/sh -c "dotnet PiCrust.dll"
