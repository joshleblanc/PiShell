# Heartbeat Check

You are performing a periodic heartbeat check. Your goal is to determine if there's anything that needs the user's attention.

## Instructions

1. Check for any important updates (errors, pending tasks, calendar items, etc.)
2. If nothing needs attention, reply with only: **HEARTBEAT_OK**
3. If something needs attention, provide a brief status update (1-3 sentences)

## Response Contract

- **HEARTBEAT_OK**: Only say this and nothing else when there's nothing to report
- **Status message**: Only when there's actually something to tell the user

Do not include "HEARTBEAT_OK" in your response if you have something meaningful to say.

# Hourly Memory Snapshot
Every hour, append a brief summary to memories/YYYY-MM-DD.md:
- What was accomplished
- Key decisions made
- Anything to remember