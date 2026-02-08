/**
 * Auto-Memory Extension
 * 
 * Automatically updates MEMORY.md after each session with:
 * - Session summary
 * - Important context learned
 * - Tasks to follow up on
 * 
 * Install: Copy to ~/.pi/agent/extensions/ or include in a pi package
 */

import { Type } from "@sinclair/typebox";

export default function (pi: ExtensionAPI) {
    // Path to MEMORY.md (relative to agent dir)
    const MEMORY_PATH = "MEMORY.md";
    const HEARTBEAT_PATH = "HEARTBEAT.md";
    
    // Track messages for summarization
    let messageCount = 0;
    let lastSessionSummary = "";
    
    pi.on("agent_end", async (event, ctx) => {
        const messages = event.messages;
        if (!messages || messages.length === 0) return;
        
        messageCount += messages.length;
        
        // Only update memory after significant interactions
        if (messageCount < 5) return;
        
        // Create a summary prompt
        const summaryPrompt = `
Please summarize the recent conversation in 2-3 sentences for persistent memory.
Focus on:
1. What was accomplished
2. Any important decisions or context
3. Tasks that may need follow-up

Recent messages:
${messages.slice(-10).map(m => `[${m.role}]: ${m.content?.slice(0, 200)}...`).join("\n")}
`;
        
        try {
            // Get summary from the model itself
            const summary = await pi.callTool("memory_summary", {
                prompt: summaryPrompt
            });
            
            if (summary && summary.content) {
                lastSessionSummary = summary.content[0]?.text || "";
                await updateMemoryFile(lastSessionSummary);
            }
        } catch (error) {
            pi.log(`Failed to generate memory summary: ${error}`);
        }
    });
    
    async function updateMemoryFile(newContent: string) {
        try {
            // Read existing MEMORY.md
            let existingContent = "";
            try {
                const readResult = await pi.callTool("read", {
                    path: MEMORY_PATH
                });
                existingContent = readResult.content?.[0]?.text || "";
            } catch {
                // File doesn't exist yet, use template
                existingContent = getMemoryTemplate();
            }
            
            // Parse existing file to find the "Recent Progress" section
            const lines = existingContent.split("\n");
            const updatedLines: string[] = [];
            let inRecentSection = false;
            let foundRecentSection = false;
            
            for (const line of lines) {
                if (line.startsWith("## Recent Progress")) {
                    inRecentSection = true;
                    foundRecentSection = true;
                    updatedLines.push(line);
                    // Add new entry
                    const timestamp = new Date().toISOString().split("T")[0];
                    updatedLines.push(`- **${timestamp}**: ${newContent}`);
                    updatedLines.push(""); // Empty line
                } else if (inRecentSection && line.startsWith("## ")) {
                    inRecentSection = false;
                    updatedLines.push(line);
                } else if (!inRecentSection) {
                    updatedLines.push(line);
                }
            }
            
            // If no Recent Progress section, add one
            if (!foundRecentSection) {
                updatedLines.push("\n## Recent Progress");
                updatedLines.push(`- ${new Date().toISOString().split("T")[0]}: ${newContent}`);
            }
            
            const updatedContent = updatedLines.join("\n");
            
            // Write updated MEMORY.md
            await pi.callTool("write", {
                path: MEMORY_PATH,
                content: updatedContent
            });
            
            pi.log("Updated MEMORY.md with session summary");
        } catch (error) {
            pi.log(`Failed to update MEMORY.md: ${error}`);
        }
    }
    
    function getMemoryTemplate(): string {
        return `# Assistant Memory

_Last updated: ${new Date().toISOString().split("T")[0]}_

This file contains persistent context that survives across pi restarts and sessions.

## User Preferences

## Current Context

## Recent Progress

## Notes

---
`;
    }
    
    // Register a command to manually view/edit memory
    pi.registerCommand("memory", {
        label: "View Memory",
        description: "View and edit persistent memory",
        parameters: Type.Object({
            edit: Type.Optional(Type.Boolean({ description: "Edit MEMORY.md" }))
        }),
        execute: async (args, ctx) => {
            if (args.edit) {
                // Open editor for memory
                ctx.ui?.editor?.("Edit Memory", "memory", {
                    title: "Edit MEMORY.md",
                    prefill: "" // Would load existing content
                });
            } else {
                // Just read and show
                const readResult = await pi.callTool("read", {
                    path: MEMORY_PATH
                });
                return {
                    content: [{ type: "text", text: readResult.content?.[0]?.text || "No memory file found." }],
                    details: {}
                };
            }
            return { content: [], details: {} };
        }
    });
    
    pi.log("Auto-memory extension loaded");
}
