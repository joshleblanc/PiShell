/**
 * Identity Extension
 * 
 * Loads IDENTITY.md from the .pi directory and makes it available to pi.
 * This allows the bot to have a persistent identity across sessions.
 * 
 * Install: Copy to ~/.pi/agent/extensions/ or include in a pi package
 */

import type { ExtensionAPI } from "@mariozechner/pi-coding-agent";
import { access, readFile } from "fs/promises";
import { constants } from "fs";


const FILES = [
    "IDENTITY.md", 
    "MEMORY.md",
    "SOUL.md",
    "USER.md"
]

async function _readFile(path) {
    try {
        await access(path, constants.R_OK);
        const content = await readFile(path, "utf-8");
        return content;
    } catch (e) {
        console.error(`Failed to load ${path}`);
        return "";
    }
}

export default function (pi: ExtensionAPI) {
    pi.on("before_agent_start", async (event, ctx) => {
        const files = FILES.map(file => {
            return _readFile(file);
        })
        await Promise.all(files);

        return {
            systemPrompt: event.systemPrompt + "\n" + files.join("\n")
        }
    });
}
