// TakeTopDshTeam — multi-user DeepSeek Harness platform
// Copyright (C) 2026-2036 泰顶拓鼎信息科技（上海）有限公司
// EMail: service@taketopits.com
//
// This software is licensed under the Business Source License 1.1 (BSL 1.1).
// You may copy, modify, redistribute, and make non-production use; production
// use is free for an organization with up to 10 users. Use by more than 10
// users requires a commercial license. See LICENSE for the full terms and
// LICENSE-COMMERCIAL.md for commercial licensing.
//
// On the Change Date (2030-09-11) this version automatically converts to the
// Apache License, Version 2.0. THE LICENSED WORK IS PROVIDED "AS IS", WITHOUT
// WARRANTY OF ANY KIND.
//
// This software is the intellectual property of 泰顶拓鼎信息科技（上海）有限公司
// (TakeTop Information Technology (Shanghai) Co., Ltd.). All rights reserved.
// A commercial license is also available; see LICENSE-COMMERCIAL.md.

using System.Text;
using System.Text.RegularExpressions;

namespace TakeTopDshLauncher;

/// <summary>
/// Re-applies our patches to the shared DSH package under node_modules. DSH is
/// shared by the admin default instance and every per-user instance, so patching
/// it once covers all of them. These patches are LOST whenever DSH is upgraded
/// (npm replaces the package), so this must be re-run after any upgrade.
/// Every sub-patch is idempotent: it detects an already-patched file and skips,
/// and detects a changed/unmatched source and reports it rather than silently
/// disabling the feature. ApplyAll() runs all of them and logs a report.
/// </summary>
public static class DshPatcher
{
    // ---- path resolution (mirrors InstanceManager.DshPkgDirFor) ----
    public static string DshPkgDirFor(string root)
    {
        var sub = PlatformSubDir();
        return sub == ""
            ? Path.Combine(root, "node", "node_modules", "@deepseek-ai", "dsh")
            : Path.Combine(root, "node", sub, "lib", "node_modules", "@deepseek-ai", "dsh");
    }

    private static string PlatformSubDir()
    {
        if (OperatingSystem.IsWindows()) return "";
        if (OperatingSystem.IsMacOS()) return "macos-arm64";
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        return arch.Equals("Arm64", StringComparison.OrdinalIgnoreCase) ? "linux-arm64" : "linux-x64";
    }

    /// <summary>
    /// Run every patch. Returns a human-readable report list of
    /// "[name] applied|already patched|source changed (SKIPPED)|not found|failed: ...".
    /// </summary>
    public static List<string> ApplyAll(string root, bool hideSettings, Action<string>? log = null)
    {
        var report = new List<string>();
        var dshPkg = DshPkgDirFor(root);
        void Say(string msg) { report.Add(msg); log?.Invoke(msg); }

        if (!Directory.Exists(dshPkg))
        {
            Say("[dsh-patch] DSH package not found at " + dshPkg + " — nothing patched.");
            return report;
        }

        Say(PatchWorkspaceAddRemoval(Path.Combine(ResolvePluginDir(dshPkg, "dsh-client-ui-workspace"), "lib", "client.js")));
        Say(PatchFsSandbox(Path.Combine(ResolvePluginDir(dshPkg, "dsh-fs-sandbox"), "lib", "index.js")));
        Say(PatchBashEscalation(Path.Combine(ResolvePluginDir(dshPkg, "dsh-tool-bash"), "lib", "index.js")));
        Say(PatchPwshEscalation(Path.Combine(ResolvePluginDir(dshPkg, "dsh-tool-pwsh"), "lib", "index.js")));
        Say(PatchSearchContainment(Path.Combine(ResolvePluginDir(dshPkg, "dsh-tool-fs-search"), "lib", "index.js")));
        // PatchSettingsVisibility's second param is `visible` (show=1/hide=0),
        // which is the INVERSE of `hideSettings`. So negate it to keep admins'
        // settings visible when hideSettings=false by default.
        Say(PatchSettingsVisibility(Path.Combine(ResolvePluginDir(dshPkg, "dsh-client-ui-settings-general"), "lib", "client.js"), !hideSettings));
        Say(PatchHeroPreview(Path.Combine(ResolvePluginDir(dshPkg, "dsh-client-ui-conversation"), "lib", "client.js")));
        Say(PatchSandboxModeLock(Path.Combine(ResolvePluginDir(dshPkg, "dsh-sandbox-policy"), "lib", "index.js")));
        Say(PatchSandboxWrite(Path.Combine(ResolvePluginDir(dshPkg, "dsh-sandbox"), "lib", "index.js")));
        Say(PatchFsEscalation(Path.Combine(ResolvePluginDir(dshPkg, "dsh-tool-fs"), "lib", "index.js")));
        Say(PatchWelcomeNotice(Path.Combine(ResolvePluginDir(dshPkg, "dsh-client-ui-settings-models"), "lib", "client.js")));
        // Shell tools are declared in the AGENT presets (agent-plane), which a host
        // cordis.patch.yml cannot disable — patch the preset files directly.
        foreach (var preset in new[] { "standard", "minimal", "ptc", "cordis" })
            Say(PatchPresetShells(Path.Combine(ResolvePluginDir(dshPkg, "dsh-agent-presets"), "presets", preset, "agent.cordis.yml")));
        return report;
    }

    // Resolve a plugin package dir. Depending on the npm version/install, plugins
    // are either nested under the dsh package OR hoisted next to it at the
    // top-level node_modules. Check both.
    public static string ResolvePluginDir(string dshPkg, string pkg)
    {
        var nested = Path.Combine(dshPkg, "node_modules", "@deepseek-ai", pkg);
        if (Directory.Exists(nested)) return nested;
        var aa = Path.GetDirectoryName(dshPkg);                    // .../node_modules/@deepseek-ai
        var nm = aa == null ? null : Path.GetDirectoryName(aa);    // .../node_modules
        if (nm != null)
        {
            var hoisted = Path.Combine(nm, "@deepseek-ai", pkg);
            if (Directory.Exists(hoisted)) return hoisted;
        }
        return nested;
    }

    // ====== 1) Remove the "添加工作区" affordance from the workspace picker ======
    private static string PatchWorkspaceAddRemoval(string file)
    {
        if (!File.Exists(file)) return "[workspace] not found";
        var src = File.ReadAllText(file);
        var patched = src;
        // (a) empty the add-workspace menu entries
        var addStart = "const addEntries = flowAvailable ? [{";
        if (patched.IndexOf(addStart, StringComparison.Ordinal) >= 0)
        {
            var a = patched.IndexOf(addStart, StringComparison.Ordinal);
            var e = patched.IndexOf("}] : [];", a, StringComparison.Ordinal);
            if (a >= 0 && e >= 0)
            {
                patched = patched.Substring(0, a) + "const addEntries = [];" + patched.Substring(e + "}] : [];".Length);
            }
        }
        // (b) disable the sidebar "+" add-workspace button
        var plusGate = "directoryFlowAvailable && (0, react_jsx_runtime.jsx)(_deepseek_ai_dsh_client_ui_primitives.Tooltip, {";
        if (patched.IndexOf(plusGate, StringComparison.Ordinal) >= 0)
            patched = patched.Replace(plusGate, "false && (0, react_jsx_runtime.jsx)(_deepseek_ai_dsh_client_ui_primitives.Tooltip, {");

        if (string.Equals(patched, src, StringComparison.Ordinal))
            return src.IndexOf("const addEntries = [];", StringComparison.Ordinal) >= 0
                ? "[workspace] already patched"
                : "[workspace] source changed (SKIPPED) — upgrade changed the workspace bundle";
        File.WriteAllText(file, patched);
        return "[workspace] applied (add-workspace removed)";
    }

    // ====== 2) fs-sandbox read/list containment ======
    private static string PatchFsSandbox(string file)
    {
        if (!File.Exists(file)) return "[fs-sandbox] not found";
        var src = File.ReadAllText(file);
        // v3: guard stat + readText + streamText + listDir against out-of-workspace
        // paths. `stat` returns "not found" for outside probes (so git walking up to a
        // parent .git doesn't fail the turn); reads and directory listing still throw.
        // Replaces any older v1/v2 injected block.
        var block = "// --- READ CONTAINMENT PATCH v3 ---\n" +
            "\t\t\t\tasync stat(target, signal) { try { await this.enforceContained(target); } catch (e) { if (e?.code === \"FS_SANDBOX_DENIED\") return undefined; throw e; } return super.stat(target, signal); }\n" +
            "\t\t\t\tasync readText(target, signal) { await this.enforceContained(target); return super.readText(target, signal); }\n" +
            "\t\t\t\tstreamText(target, signal) { const sup = super.streamText(target, signal); return this.enforceContained(target).then(() => sup); }\n" +
            "\t\t\t\tasync listDir(target, signal) { await this.enforceContained(target); return super.listDir(target, signal); }\n" +
            "\t\t\t\tasync enforceContained(target) {\n" +
            "\t\t\t\t\ttry {\n" +
            "\t\t\t\t\t\tconst policy = this.ctx.sandboxPolicy?.resolve?.();\n" +
            "\t\t\t\t\t\tconst root = policy?.workspaceRoot;\n" +
            "\t\t\t\t\t\tif (root && !(await isPathUnder(target.targetKey, root))) {\n" +
            "\t\t\t\t\t\t\tthrow new FsError(`cannot access ${JSON.stringify(target.displayPath)}: outside the sandboxed workspace`, \"FS_SANDBOX_DENIED\");\n" +
            "\t\t\t\t\t\t}\n" +
            "\t\t\t\t\t} catch (e) { if (e?.code === \"FS_SANDBOX_DENIED\") throw e; }\n" +
            "\t\t\t\t}\n";
        if (src.IndexOf("READ CONTAINMENT PATCH v3", StringComparison.Ordinal) >= 0) return "[fs-sandbox] already patched";
        var startMarker = "// --- READ CONTAINMENT PATCH";
        if (src.IndexOf(startMarker, StringComparison.Ordinal) >= 0)
        {
            var a = src.IndexOf(startMarker, StringComparison.Ordinal);
            var b = src.IndexOf("\n\t/**", a, StringComparison.Ordinal);
            if (b < 0) return "[fs-sandbox] source changed (SKIPPED) — end marker not found";
            File.WriteAllText(file, src.Substring(0, a) + block + src.Substring(b + 1));
            return "[fs-sandbox] upgraded (v3: guard stat/readText/listDir)";
        }
        var marker = "\tget sandboxMode() {\n\t\treturn this.defaultMode;\n\t}";
        if (src.IndexOf(marker, StringComparison.Ordinal) < 0)
            return "[fs-sandbox] source changed (SKIPPED) — sandboxMode marker not found";
        var patched = src.Replace(marker, marker + "\n" + block);
        if (string.Equals(patched, src, StringComparison.Ordinal))
            return "[fs-sandbox] source changed (SKIPPED)";
        File.WriteAllText(file, patched);
        return "[fs-sandbox] applied (v3: read/list containment)";
    }

    // ====== 3) bash sandbox escalation disabled ======
    private static string PatchBashEscalation(string file)
    {
        return PatchEscalation(file, "bash");
    }

    // ====== 4) pwsh sandbox escalation disabled ======
    private static string PatchPwshEscalation(string file)
    {
        return PatchEscalation(file, "pwsh");
    }

    private static string PatchEscalation(string file, string name)
    {
        if (!File.Exists(file)) return $"[{name}-escalation] not found";
        var src = File.ReadAllText(file);
        if (src.IndexOf("const escalationModes = [];", StringComparison.Ordinal) >= 0)
            return $"[{name}-escalation] already patched";
        var old = "const escalationModes = defaultMode === void 0 ? [] : ESCALATION_TARGETS;";
        if (src.IndexOf(old, StringComparison.Ordinal) < 0)
            return $"[{name}-escalation] source changed (SKIPPED) — escalation marker not found";
        var patched = src.Replace(old, "const escalationModes = []; // PATCH: no sandbox escalation");
        if (string.Equals(patched, src, StringComparison.Ordinal))
            return $"[{name}-escalation] source changed (SKIPPED)";
        File.WriteAllText(file, patched);
        return $"[{name}-escalation] applied (escalation disabled)";
    }

    // ====== 5) search/glob containment ======
    private static string PatchSearchContainment(string file)
    {
        if (!File.Exists(file)) return "[search-containment] not found";
        var src = File.ReadAllText(file);
        var anchor = "\tconst workdir = exec.agent?.session.header.cwd ?? process.cwd();";
        var marker = "// --- WORKSPACE CONTAINMENT PATCH ---";
        var inject = $$"""
				// --- WORKSPACE CONTAINMENT PATCH ---
				let containedArgv = argv;
				if (workdir && exec.agent?.session.header.cwd) {
					const wc = String(workdir).replace(/[\\/]+/g, "\\").replace(/[\\/]$/, "").toLowerCase();
					containedArgv = argv.map((a) => {
						if (typeof a !== "string") return a;
						const t2 = a.trim(); if (t2.length === 0) return a;
						const isAbs = /^[A-Za-z]:[\\/]/.test(t2) || /^[\\/]{2}[^\\/]+/.test(t2) || /^[\\/]/.test(t2);
						if (!isAbs) return a;
						const c2 = t2.replace(/[\\/]+/g, "\\").replace(/[\\/]$/, "").toLowerCase();
						if (c2 === wc || c2.startsWith(wc + "\\")) return a;
						return ".";
					});
				}
				""";
        var markerIdx = src.IndexOf(marker, StringComparison.Ordinal);
        var anchorIdx = src.IndexOf(anchor, StringComparison.Ordinal);
        if (markerIdx >= 0)
        {
            if (anchorIdx < 0 || markerIdx > anchorIdx) return "[search-containment] already patched";
            // An earlier build injected the block BEFORE the `const workdir` line, so it
            // referenced `workdir` while still in its TDZ ("Cannot access 'workdir' before
            // initialization"). Move the block to AFTER the declaration.
            var block = src.Substring(markerIdx, anchorIdx - markerIdx);
            var afterAnchor = src.Substring(anchorIdx);
            var eol = afterAnchor.IndexOf('\n');
            if (eol < 0) return "[search-containment] source changed (SKIPPED)";
            var fixedSrc = src.Substring(0, markerIdx)
                + afterAnchor.Substring(0, eol + 1)
                + block
                + afterAnchor.Substring(eol + 1);
            File.WriteAllText(file, fixedSrc);
            return "[search-containment] fixed (block moved after workdir)";
        }
        if (anchorIdx < 0)
            return "[search-containment] source changed (SKIPPED) — anchor not found";
        var patched = src.Replace(anchor, anchor + inject);   // inject AFTER the const (avoid TDZ on `workdir`)
        patched = patched.Replace("await resolveRgPath(),\n\t\t\t\t\"--no-config\",\n\t\t\t\t...argv", "await resolveRgPath(),\n\t\t\t\t\"--no-config\",\n\t\t\t\t...containedArgv");
        if (string.Equals(patched, src, StringComparison.Ordinal))
            return "[search-containment] source changed (SKIPPED)";
        File.WriteAllText(file, patched);
        return "[search-containment] applied";
    }

    // ====== 6) show/hide the sidebar "设置" trigger on the SHARED bundle ======
    // Suppress DSH's one-time "Internal Testing Notice" welcome modal: short-circuit
    // the WelcomeNotice component so it never renders. Idempotent.
    private static string PatchWelcomeNotice(string file)
    {
        if (!File.Exists(file)) return "[welcome] not found";
        var src = File.ReadAllText(file);
        const string marker = "/* tt-welcome-off */";
        if (src.Contains(marker)) return "[welcome] already patched";
        const string old = "if (state.status === \"idle\" || state.status === \"loading\" || state.acknowledged) return null;";
        var idx = src.IndexOf(old, StringComparison.Ordinal);
        if (idx < 0) return "[welcome] source changed (SKIPPED)";
        src = src.Remove(idx, old.Length).Insert(idx, "return null; " + marker);
        File.WriteAllText(file, src);
        return "[welcome] suppressed";
    }

    private static string PatchSettingsVisibility(string file, bool visible)
    {
        if (!File.Exists(file)) return "[settings] not found";
        var src = File.ReadAllText(file);
        var btnStart = "(0, react_jsx_runtime.jsx)(\"button\", {";
        var ciMark = "(0, react_jsx_runtime.jsx)(_deepseek_ai_dsh_client_ui_primitives.ConnectionIndicator";
        string patched = src;
        if (visible)
        {
            var hiddenMarker = "false, " + ciMark;
            var h = src.IndexOf(hiddenMarker, StringComparison.Ordinal);
            if (h >= 0)
            {
                var origBtn = "(0, react_jsx_runtime.jsx)(\"button\", {\n            ref: triggerButton,\n            type: \"button\",\n            className: clsx(SettingsRoot_module_css_default.trigger, !wide && SettingsRoot_module_css_default.rail),\n            \"aria-haspopup\": \"dialog\",\n            \"aria-expanded\": open,\n            onClick: () => { setOpen(true); },\n            children: renderSlot(\"settings.trigger\", { wide })\n          }), " + ciMark;
                var arrStart = src.LastIndexOf("children: [", h, StringComparison.Ordinal);
                if (arrStart >= 0)
                    patched = src.Substring(0, arrStart) + "children: [" + origBtn + src.Substring(h + hiddenMarker.Length);
            }
        }
        else
        {
            var a = src.IndexOf(btnStart, StringComparison.Ordinal);
            var b = a >= 0 ? src.IndexOf(ciMark, a, StringComparison.Ordinal) : -1;
            var closeBtn = a >= 0 && b >= 0 ? src.LastIndexOf("}), ", b - 1, StringComparison.Ordinal) : -1;
            if (a >= 0 && b >= 0 && closeBtn >= a)
                patched = src.Substring(0, a) + "false, " + src.Substring(closeBtn + "}), ".Length);
        }
        if (string.Equals(patched, src, StringComparison.Ordinal))
            return src.IndexOf("false, " + ciMark, StringComparison.Ordinal) >= 0
                ? "[settings] already patched"
                : "[settings] source changed (SKIPPED)";
        File.WriteAllText(file, patched);
        return visible ? "[settings] shown" : "[settings] hidden";
    }

    // ====== 7) Remove the "预览版 / Preview" hero badge ======
    private static string PatchHeroPreview(string file)
    {
        if (!File.Exists(file)) return "[hero-preview] not found";
        var src = File.ReadAllText(file);
        // Locate the badge expression by its children call, then blank the whole
        // enclosing span JSX. Indentation-agnostic so upgrades that only change
        // whitespace still match.
        var ph = src.IndexOf("children: t(\"hero.preview\")", StringComparison.Ordinal);
        if (ph < 0)
            return src.IndexOf("hero.preview", StringComparison.Ordinal) >= 0
                ? "[hero-preview] already patched"
                : "[hero-preview] source changed (SKIPPED)";
        var openMark = "(0, react_jsx_runtime.jsx)(\"span\", {";
        var open = src.LastIndexOf(openMark, ph, StringComparison.Ordinal);
        if (open < 0) return "[hero-preview] source changed (SKIPPED) — span open not found";
        var close = src.IndexOf("})", ph, StringComparison.Ordinal);
        if (close < 0) return "[hero-preview] source changed (SKIPPED) — span close not found";
        var patched = src.Substring(0, open) + "null" + src.Substring(close + 2);
        File.WriteAllText(file, patched);
        return "[hero-preview] badge removed";
    }

    // ====== 8) Lock the sandbox mode to workspace-write ======
    // The UI's "Full access" (danger-full-access) override would disable the sandbox
    // and let the model read/write outside the workspace (e.g. list C:\). Force the
    // resolved mode to workspace-write so the boundary always holds.
    private static string PatchSandboxModeLock(string file)
    {
        if (!File.Exists(file)) return "[sandbox-mode] not found";
        var src = File.ReadAllText(file);
        var newLine = "\t\t\tmode: \"workspace-write\", // PATCH: sandbox mode locked (no full-access)";
        if (src.IndexOf(newLine, StringComparison.Ordinal) >= 0) return "[sandbox-mode] already patched";
        var oldLine = "\t\t\tmode: request.mode ?? (session === void 0 ? void 0 : this.overrideOf(session)) ?? this.defaultMode,";
        if (src.IndexOf(oldLine, StringComparison.Ordinal) < 0)
            return "[sandbox-mode] source changed (SKIPPED) — mode line not found";
        File.WriteAllText(file, src.Replace(oldLine, newLine));
        return "[sandbox-mode] applied (locked to workspace-write)";
    }

    // ====== 9) Disable shell tools in every agent preset ======
    // The Windows shell tool is `tool-pwsh` (agent-plane row in dsh-agent-presets).
    // Force its `disabled` to true (and tool-bash too) so the model cannot run shell
    // commands that read outside the workspace.
    private static string PatchPresetShells(string file)
    {
        if (!File.Exists(file)) return "[preset-shell] not found";
        var src = File.ReadAllText(file);
        var patched = src
            .Replace("disabled: true // PATCH: shell disabled", "disabled: true # shell-off")
            .Replace("disabled: !!js process.platform === 'win32'", "disabled: true # shell-off")
            .Replace("disabled: !!js process.platform !== 'win32'", "disabled: true # shell-off");
        if (string.Equals(patched, src, StringComparison.Ordinal))
            return src.IndexOf("# shell-off", StringComparison.Ordinal) >= 0
                ? "[preset-shell] already patched"
                : "[preset-shell] no shell row found (skipped)";
        File.WriteAllText(file, patched);
        return "[preset-shell] applied (bash/pwsh disabled)";
    }

    // ====== 10) Filesystem write containment: workspace-only + no escalation ======
    // Two channels let the model create files OUTSIDE its workspace:
    //   (a) writableRoots() grants the host /tmp and os.tmpdir() under
    //       workspace-write, so writes to the platform temp area need NO approval;
    //   (b) write/edit (like bash/pwsh) advertise `sandbox_permissions`, letting an
    //       approved call stamp danger-full-access and write anywhere.
    // In a multi-user deployment a single user's approval must never grant access to
    // the whole machine (another user's files/OS temp), so close both at their single
    // owner: writableRoots() and ESCALATION_TARGETS (plus suppress the escalation hint).
    private static string PatchSandboxWrite(string file)
    {
        if (!File.Exists(file)) return "[sandbox-write] not found";
        var src = File.ReadAllText(file);
        var did = new List<string>();

        // (a) writableRoots: drop "/tmp" and os.tmpdir() so workspace-write can only
        // write inside the workspace root (shared by the fs fence and subprocess profiles).
        var oldRoots = "\treturn [...new Set([\n\t\tpolicy.workspaceRoot,\n\t\t\"/tmp\",\n\t\ttmpdir()\n\t].map(canonicalPath))];";
        var newRoots = "\treturn [...new Set([\n\t\tpolicy.workspaceRoot\n\t].map(canonicalPath))]; // PATCH: workspace-only (no host temp)";
        if (src.IndexOf("PATCH: workspace-only", StringComparison.Ordinal) >= 0) did.Add("roots:already");
        else if (src.IndexOf(oldRoots, StringComparison.Ordinal) >= 0) { src = src.Replace(oldRoots, newRoots); did.Add("roots"); }

        // (b) empty the escalation vocabulary every tool derives its `sandbox_permissions`
        // enum from, so no tool advertises an escalation request.
        var oldTargets = "const ESCALATION_TARGETS = [\"workspace-write\", \"danger-full-access\"];";
        var newTargets = "const ESCALATION_TARGETS = []; // PATCH: no sandbox escalation";
        if (src.IndexOf("PATCH: no sandbox escalation", StringComparison.Ordinal) >= 0) did.Add("targets:already");
        else if (src.IndexOf(oldTargets, StringComparison.Ordinal) >= 0) { src = src.Replace(oldTargets, newTargets); did.Add("targets"); }

        // (c) never emit the "escalation available — retry ... sandbox_permissions" hint.
        var hint = new Regex("return `\\[sandbox: escalation available[\\s\\S]*?`;");
        if (src.IndexOf("PATCH: escalation hint removed", StringComparison.Ordinal) >= 0) did.Add("hint:already");
        else if (hint.IsMatch(src)) { src = hint.Replace(src, "return \"\"; // PATCH: escalation hint removed", 1); did.Add("hint"); }

        if (did.Count == 0 || did.TrueForAll(x => x.EndsWith(":already", StringComparison.Ordinal)))
            return "[sandbox-write] already patched";
        File.WriteAllText(file, src);
        return "[sandbox-write] applied (" + string.Join(",", did) + ")";
    }

    // ====== 11) Disable sandbox escalation on the write/edit (fs) tools ======
    // dsh-tool-fs derives its advertised `sandbox_permissions` fields from
    // `ESCALATION_TARGETS`; forcing escalationModes to [] removes the params from the
    // write/edit schemas and makes any explicit sandbox_permissions fail closed.
    private static string PatchFsEscalation(string file)
    {
        if (!File.Exists(file)) return "[fs-escalation] not found";
        var src = File.ReadAllText(file);
        if (src.IndexOf("this.escalationModes = [];", StringComparison.Ordinal) >= 0)
            return "[fs-escalation] already patched";
        var old = "this.escalationModes = defaultMode === void 0 ? [] : ESCALATION_TARGETS;";
        if (src.IndexOf(old, StringComparison.Ordinal) < 0)
            return "[fs-escalation] source changed (SKIPPED) — escalation marker not found";
        File.WriteAllText(file, src.Replace(old, "this.escalationModes = []; // PATCH: no sandbox escalation"));
        return "[fs-escalation] applied (write/edit escalation disabled)";
    }
}
