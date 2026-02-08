import type { ExtensionAPI } from "@mariozechner/pi-coding-agent";

/**
 * Session Management Extension
 * 
 * Features:
 * - Automatically creates a new session at midnight each day
 * - Provides /new command to manually create a new session
 */

export default function (pi: ExtensionAPI) {
  // Track the last date we created a session for
  let lastSessionDate: string | null = null;
  let checkInterval: ReturnType<typeof setInterval> | null = null;

  // Get today's date string (YYYY-MM-DD)
  function getTodayDate(): string {
    return new Date().toISOString().split("T")[0];
  }

  // Check if it's time to create a new session (midnight)
  function checkMidnight(): boolean {
    const now = new Date();
    const currentDate = getTodayDate();
    
    // Check if we've already created a session today
    if (lastSessionDate === currentDate) {
      return false;
    }

    // Check if it's past midnight (hours >= 0, minutes >= 0 to ensure we're past midnight)
    // We consider it "midnight passed" if we're at least 0 minutes into the new day
    if (now.getHours() === 0 && now.getMinutes() >= 0) {
      return true;
    }

    // Also trigger if we missed midnight while the process was running
    // (e.g., process started yesterday, now it's a new day)
    return false;
  }

  // Create a new session at midnight
  async function createMidnightSession() {
    const today = getTodayDate();
    lastSessionDate = today;

    pi.setSessionName(`Session - ${today}`);

    const result = await pi.sendUserMessage("--- Auto-generated: New day, new session ---\n\nStart fresh today. What would you like to work on?");
    
    if (result) {
      console.log(`[session-manager] Created new session for ${today}`);
    }
  }

  // Register the /new command
  pi.registerCommand("new", {
    description: "Create a new session",
    handler: async (args, ctx) => {
      const result = await ctx.newSession();
      
      if (result.cancelled) {
        ctx.ui.notify("New session cancelled by extension", "warning");
      } else {
        ctx.ui.notify("Created new session", "success");
      }
    },
  });

  // On session start, check if we need to create a midnight session
  pi.on("session_start", async (_event, ctx) => {
    // Initialize the last session date from current session
    // This helps us track across restarts
    lastSessionDate = getTodayDate();

    // Check if we need to create a midnight session
    // This handles the case where pi was restarted after midnight
    if (checkMidnight()) {
      ctx.ui.notify("Midnight passed while offline - creating new session", "info");
      await createMidnightSession();
    }
  });

  // Start the midnight checker - runs every minute
  async function startMidnightChecker() {
    checkInterval = setInterval(async () => {
      if (checkMidnight()) {
        await createMidnightSession();
      }
    }, 60 * 1000); // Check every minute

    // Initial check on startup
    if (checkMidnight()) {
      await createMidnightSession();
    }
  }

  // Start the checker when the extension loads
  startMidnightChecker();

  // Cleanup on shutdown
  pi.on("session_shutdown", () => {
    if (checkInterval) {
      clearInterval(checkInterval);
      checkInterval = null;
    }
  });
}
