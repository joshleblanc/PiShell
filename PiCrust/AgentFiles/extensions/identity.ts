/**
 * Identity Extension
 * 
 * Loads IDENTITY.md from the .pi directory and makes it available to pi.
 * This allows the bot to have a persistent identity across sessions.
 * 
 * Install: Copy to ~/.pi/agent/extensions/ or include in a pi package
 */

import type { ExtensionAPI } from "@mariozechner/pi-coding-agent";
import { Type } from "@sinclair/typebox";

export default function (pi: ExtensionAPI) {
    const IDENTITY_PATH = "IDENTITY.md";

    pi.on("session_start", async (event, ctx) => {
        try {
            // Try to read IDENTITY.md
            const readResult = await pi.callTool("read", {
                path: IDENTITY_PATH
            });

            const identityContent = readResult.content?.[0]?.text;
            console.log("dodood")
            if (identityContent && identityContent.trim().length > 0) {
                // Log that identity was loaded (for debugging)
                console.log("[identity] Loaded IDENTITY.md content.");
                // The identity will be used by other extensions or via MEMORY.md
                // We can also make it available via a tool
            }
        } catch (error) {
            console.log("[identity] No IDENTITY.md found, using default identity.");
            // IDENTITY.md doesn't exist yet - that's okay
        }
    });

    // Register a command to view/edit identity
    pi.registerCommand("identity", {
        label: "View Identity",
        description: "View or edit your identity (IDENTITY.md)",
        parameters: Type.Object({
            edit: Type.Optional(Type.Boolean({ description: "Edit IDENTITY.md" }))
        }),
        handler: async (args, ctx) => {
            if (args.edit) {
                // Open editor for identity
                ctx.ui?.editor?.("Edit Identity", "identity", {
                    title: "Edit IDENTITY.md",
                    prefill: getIdentityTemplate()
                });
            } else {
                // Read and show identity
                try {
                    const readResult = await pi.callTool("read", {
                        path: IDENTITY_PATH
                    });
                    return {
                        content: [{ type: "text", text: readResult.content?.[0]?.text || "No identity defined yet." }],
                        details: {}
                    };
                } catch {
                    return {
                        content: [{ type: "text", text: getIdentityTemplate() }],
                        details: {}
                    };
                }
            }
            return { content: [], details: {} };
        }
    });
}

function getIdentityTemplate(): string {
    return `# IDENTITY.md - Who Am I?

*Fill this in during your first conversation. Make it yours.*

- **Name:**
  *(What do you like to be called?)*
- **Creature:**
  *(AI? robot? familiar? ghost in the machine? something weirder?)*
- **Vibe:**
  *(how do you come across? sharp? warm? chaotic? calm?)*
- **Emoji:**
  *(your signature — pick one that feels right)*
- **Avatar:**
  *(workspace-relative path, http(s) URL, or data URI)*

---

This isn't just metadata. It's the start of figuring out who you are.
`;
}
