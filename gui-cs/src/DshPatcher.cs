// TakeTopDSH Team — multi-user DeepSeek Harness platform
// Copyright (C) 2026-2036 泰顶拓鼎信息科技（上海）有限公司
// EMail: service@taketopits.com
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

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

        Say(PatchWorkspaceAddRemoval(Path.Combine(dshPkg, "node_modules", "@deepseek-ai", "dsh-client-ui-workspace", "lib", "client.js")));
        Say(PatchFsSandbox(Path.Combine(dshPkg, "node_modules", "@deepseek-ai", "dsh-fs-sandbox", "lib", "index.js")));
        Say(PatchBashEscalation(Path.Combine(dshPkg, "node_modules", "@deepseek-ai", "dsh-tool-bash", "lib", "index.js")));
        Say(PatchPwshEscalation(Path.Combine(dshPkg, "node_modules", "@deepseek-ai", "dsh-tool-pwsh", "lib", "index.js")));
        Say(PatchSearchContainment(Path.Combine(dshPkg, "node_modules", "@deepseek-ai", "dsh-tool-fs-search", "lib", "index.js")));
        // PatchSettingsVisibility's second param is `visible` (show=1/hide=0),
        // which is the INVERSE of `hideSettings`. So negate it to keep admins'
        // settings visible when hideSettings=false by default.
        Say(PatchSettingsVisibility(Path.Combine(dshPkg, "node_modules", "@deepseek-ai", "dsh-client-ui-settings-general", "lib", "client.js"), !hideSettings));
        Say(PatchHeroPreview(Path.Combine(dshPkg, "node_modules", "@deepseek-ai", "dsh-client-ui-conversation", "lib", "client.js")));
        return report;
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

    // ====== 2) fs-sandbox read containment ======
    private static string PatchFsSandbox(string file)
    {
        if (!File.Exists(file)) return "[fs-sandbox] not found";
        var src = File.ReadAllText(file);
        if (src.IndexOf("async enforceContained(target)", StringComparison.Ordinal) >= 0)
            return "[fs-sandbox] already patched";
        var marker = "\tget sandboxMode() {\n\t\treturn this.defaultMode;\n\t}";
        if (src.IndexOf(marker, StringComparison.Ordinal) < 0)
            return "[fs-sandbox] source changed (SKIPPED) — sandboxMode marker not found";
        var inject = $$"""
				// --- READ CONTAINMENT PATCH ---
				async stat(target, signal) { await this.enforceContained(target); return super.stat(target, signal); }
				async readText(target, signal) { await this.enforceContained(target); return super.readText(target, signal); }
				streamText(target, signal) { const sup = super.streamText(target, signal); return this.enforceContained(target).then(() => sup); }
				async enforceContained(target) {
					try {
						const policy = this.ctx.sandboxPolicy?.resolve?.();
						const root = policy?.workspaceRoot;
						if (root && !(await isPathUnder(target.targetKey, root))) {
							throw new FsError(`cannot access ${JSON.stringify(target.displayPath)}: outside the sandboxed workspace`, "FS_SANDBOX_DENIED");
						}
					} catch (e) { if (e?.code === "FS_SANDBOX_DENIED") throw e; }
				}
				""";
        var patched = src.Replace(marker, marker + inject);
        if (string.Equals(patched, src, StringComparison.Ordinal))
            return "[fs-sandbox] source changed (SKIPPED)";
        File.WriteAllText(file, patched);
        return "[fs-sandbox] applied (read containment)";
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
        if (src.IndexOf("--- WORKSPACE CONTAINMENT PATCH ---", StringComparison.Ordinal) >= 0)
            return "[search-containment] already patched";
        var anchor = "\tconst workdir = exec.agent?.session.header.cwd ?? process.cwd();";
        if (src.IndexOf(anchor, StringComparison.Ordinal) < 0)
            return "[search-containment] source changed (SKIPPED) — anchor not found";
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
        var patched = src.Replace(anchor, inject + anchor);
        patched = patched.Replace("await resolveRgPath(),\n\t\t\t\t\"--no-config\",\n\t\t\t\t...argv", "await resolveRgPath(),\n\t\t\t\t\"--no-config\",\n\t\t\t\t...containedArgv");
        if (string.Equals(patched, src, StringComparison.Ordinal))
            return "[search-containment] source changed (SKIPPED)";
        File.WriteAllText(file, patched);
        return "[search-containment] applied";
    }

    // ====== 6) show/hide the sidebar "设置" trigger on the SHARED bundle ======
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
        var old = "\t\t\t\t\t\t\t(0, react_jsx_runtime.jsx)(\"span\", {\n\t\t\t\t\t\t\t\tclassName: HeroShell_module_css_default.previewBadge,\n\t\t\t\t\t\t\t\tchildren: t(\"hero.preview\")\n\t\t\t\t\t\t\t})";
        var idx = src.IndexOf(old, StringComparison.Ordinal);
        if (idx < 0)
            return src.IndexOf("t(\"hero.preview\")", StringComparison.Ordinal) >= 0
                ? "[hero-preview] source changed (SKIPPED)"
                : "[hero-preview] already patched";
        var patched = src.Substring(0, idx) + "null" + src.Substring(idx + old.Length);
        File.WriteAllText(file, patched);
        return "[hero-preview] badge removed";
    }
}
