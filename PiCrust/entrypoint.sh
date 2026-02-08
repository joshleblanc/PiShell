#!/bin/sh

# Seed agent files from the staging directory into the bind-mounted volume.
# Force-overwrite extensions, prompts, and skills we ship so deploys pick up
# changes, but leave any user-installed files untouched.
for dir in extensions prompts skills; do
    if [ -d "/opt/agent-files/$dir" ]; then
        cp -rf "/opt/agent-files/$dir/." "/home/picrust/$dir/"
    fi
done

# Copy remaining files without overwriting (preserves runtime state
# like memories, sessions, etc.)
cp -rn /opt/agent-files/* /home/picrust/ 2>/dev/null || true
cp -rn /opt/agent-files/.* /home/picrust/ 2>/dev/null || true

chown -R picrust:picrust /home/picrust

# Drop to picrust user and run the application
exec su picrust -s /bin/sh -c "dotnet PiCrust.dll"
