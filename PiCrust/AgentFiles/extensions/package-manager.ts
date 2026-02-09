/**
 * Package Manager Extension
 *
 * Lets the agent install pi packages into itself at runtime using the
 * `pi install` CLI. Supports npm, git, and https sources.
 *
 * Tools:
 *   install_package     — install a pi package
 *   uninstall_package   — remove a pi package
 *   list_packages       — list installed pi packages
 *   update_packages     — update installed packages
 *
 * Source formats:
 *   npm:@scope/name      — from npm registry
 *   npm:@scope/name@1.2  — pinned version
 *   git:github.com/u/r   — from git
 *   git:github.com/u/r@v — tag or commit
 *   https://github.com/… — shorthand git URL
 */

import type { ExtensionAPI } from "@mariozechner/pi-coding-agent";
import { Type } from "@sinclair/typebox";

export default function (pi: ExtensionAPI) {
    // Tool: install_package
    pi.registerTool({
        name: "install_package",
        label: "Install Package",
        description: "Install a pi package from npm, git, or https source. The runtime will be reloaded automatically after installation.",
        parameters: Type.Object({
            source: Type.String({
                description: "Package source (npm:@scope/name, git:github.com/user/repo, or https://github.com/user/repo)",
            }),
        }),
        async execute(_toolCallId, params, signal) {
            const source = params.source?.trim();
            if (!source) {
                return {
                    content: [{ type: "text", text: "Usage: install_package(source: 'npm:pkg' or 'git:repo' or 'https://github.com/user/repo')" }],
                    details: { success: false },
                };
            }

            if (!isValidSource(source)) {
                return {
                    content: [{ type: "text", text: `Invalid source: ${source}. Use npm:pkg, git:github.com/user/repo, or an https URL.` }],
                    details: { success: false },
                };
            }

            const result = await pi.exec("pi", ["install", source], { signal, timeout: 120000 });
            const output = result.stdout + result.stderr;

            if (result.code !== 0 || output.toLowerCase().includes("error")) {
                return {
                    content: [{ type: "text", text: `Install failed: ${output || "Unknown error"}` }],
                    details: { success: false, output },
                };
            }

            return {
                content: [{ type: "text", text: `Installed ${source}. The runtime will be reloaded automatically.` }],
                details: { success: true, output },
            };
        },
    });

    // Tool: uninstall_package
    pi.registerTool({
        name: "uninstall_package",
        label: "Uninstall Package",
        description: "Remove an installed pi package",
        parameters: Type.Object({
            source: Type.String({
                description: "Package source to remove (same format as install_package)",
            }),
        }),
        async execute(_toolCallId, params, signal) {
            const source = params.source?.trim();
            if (!source) {
                return {
                    content: [{ type: "text", text: "Usage: uninstall_package(source: 'npm:pkg' or 'git:repo')" }],
                    details: { success: false },
                };
            }

            const result = await pi.exec("pi", ["remove", source], { signal, timeout: 60000 });
            const output = result.stdout + result.stderr;

            if (result.code !== 0 || output.toLowerCase().includes("error")) {
                return {
                    content: [{ type: "text", text: `Remove failed: ${output || "Unknown error"}` }],
                    details: { success: false, output },
                };
            }

            return {
                content: [{ type: "text", text: `Removed ${source}. The runtime will be reloaded automatically.` }],
                details: { success: true, output },
            };
        },
    });

    // Tool: list_packages
    pi.registerTool({
        name: "list_packages",
        label: "List Packages",
        description: "List all installed pi packages",
        parameters: Type.Object({}),
        async execute(_toolCallId, _params, signal) {
                    const result = await pi.exec("pi", ["list"], { signal, timeout: 30000 });
            const output = result.stdout + result.stderr;

            if (!output.trim()) {
                return {
                    content: [{ type: "text", text: "No packages installed yet." }],
                    details: { success: true, packages: [] },
                };
            }

            // Parse the output to extract package names
            const packages = parsePackageList(output);

            return {
                content: [{ type: "text", text: output.trim() }],
                details: { success: true, packages },
            };
        },
    });

    // Tool: update_packages
    pi.registerTool({
        name: "update_packages",
        label: "Update Packages",
        description: "Update all installed packages (skips pinned versions). The runtime will be reloaded automatically after updating.",
        parameters: Type.Object({
            source: Type.Optional(Type.String({
                description: "Optional: specific package to update",
            })),
        }),
        async execute(_toolCallId, params, signal) {
            const args = params.source ? ["update", params.source] : ["update"];
            const result = await pi.exec("pi", args, { signal, timeout: 120000 });
            const output = result.stdout + result.stderr;

            if (result.code !== 0 || output.toLowerCase().includes("error")) {
                return {
                    content: [{ type: "text", text: `Update failed: ${output || "Unknown error"}` }],
                    details: { success: false, output },
                };
            }

            return {
                content: [{ type: "text", text: output.trim() || "Packages updated. The runtime will be reloaded automatically." }],
                details: { success: true, output },
            };
        },
    });
}

/** Validate that the source looks like a valid pi package specifier. */
function isValidSource(input: string): boolean {
    // npm:@scope/name or npm:name, optionally with @version
    if (/^npm:(@[\w\-\.]+\/)?[\w\-\.]+(@[\w\-\.\^~>=<*]+)?$/.test(input)) return true;
    // git:github.com/user/repo, optionally with @ref
    if (/^git:[\w\-\.]+\/[\w\-\.]+\/[\w\-\.]+((@|#)[\w\-\.\/]+)?$/.test(input)) return true;
    // https://github.com/user/repo style URLs
    if (/^https?:\/\/[\w\-\.]+\/[\w\-\.]+\/[\w\-\.]+(\.git)?/.test(input)) return true;
    return false;
}

/** Parse the output of `pi list` into an array of package info. */
function parsePackageList(output: string): Array<{ source: string; version?: string; path?: string }> {
    const packages: Array<{ source: string; version?: string; path?: string }> = [];

    // Try to parse JSON output first (newer pi versions)
    try {
        const parsed = JSON.parse(output);
        if (Array.isArray(parsed)) {
            return parsed;
        }
    } catch {
        // Not JSON, parse text format
    }

    // Text format: "npm:@scope/name@1.0.0 -> ~/.pi/agent/extensions/pkg"
    // or just "npm:@scope/name -> ~/.pi/agent/extensions/pkg"
    const lines = output.split("\n");
    for (const line of lines) {
        const trimmed = line.trim();
        if (!trimmed || trimmed.toLowerCase().includes("no packages")) {
            continue;
        }

        // Match: "npm:@scope/name@version -> path" or "git:repo@ref -> path"
        const match = trimmed.match(/^(npm|git):([^\s@]+(?:@[^\s]+)?)\s*->\s*(.+)$/);
        if (match) {
            const source = match[1] + ":" + match[2];
            packages.push({ source, path: match[3] });
        } else if (isValidSource(trimmed) || trimmed.startsWith("npm:") || trimmed.startsWith("git:") || trimmed.startsWith("https://")) {
            // Fallback: just use the line as a package source
            packages.push({ source: trimmed });
        }
    }

    return packages;
}
