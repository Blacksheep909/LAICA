using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Laica
{
    /// <summary>
    /// LAICA as a contributor. Agents started through LAICA get a git hook that adds "Co-authored-by: LAICA &lt;email&gt;" to every commit they make,
    /// next to whatever trailer the agent adds itself (Claude, Codex, ...). On by default, switchable per project. Repos that set their own
    /// core.hooksPath are left alone, and the repo's own hooks keep running (the LAICA hook forwards to them first).
    /// </summary>
    public sealed partial class HarnessManager
    {
        public const string DefaultCoAuthorEmail = "noreply@laica.invalid";   // placeholder until a real GitHub bot account or App noreply address is chosen
        Dictionary<string, object> coauthor; static Action<ProcessStartInfo, string> coEnv;

        const string HookScript = @"#!/bin/sh
# Installed by LAICA. Runs the repository's own hook of the same name first, then (for commit-msg) adds LAICA as a co-author.
name=$(basename ""$0"")
common=$(git rev-parse --git-common-dir 2>/dev/null)
repo=""$common/hooks/$name""
if [ -f ""$repo"" ] && [ ""$repo"" != ""$0"" ]; then
  ""$repo"" ""$@"" || exit $?
fi
if [ ""$name"" = ""commit-msg"" ] && [ -n ""$LAICA_COAUTHOR"" ]; then
  git interpret-trailers --in-place --if-exists addIfDifferent --trailer ""Co-authored-by: $LAICA_COAUTHOR"" ""$1"" || exit 0
fi
exit 0
";
        static readonly string[] HookNames = { "pre-commit", "prepare-commit-msg", "commit-msg", "post-commit", "pre-push", "pre-rebase", "post-checkout", "post-merge" };

        Dictionary<string, object> CoAuthorConfig()
        {
            lock (gate)
            {
                if (coauthor != null) return coauthor;
                var l = LoadList("coauthor.json"); var c = l.Count > 0 ? l[0] : new Dictionary<string, object>();
                if (!c.ContainsKey("Enabled")) c["Enabled"] = true; if (Str(c, "Name") == "") c["Name"] = "LAICA"; if (Str(c, "Email") == "") c["Email"] = DefaultCoAuthorEmail; if (!(c.ContainsKey("Off") && c["Off"] is System.Collections.IList)) c["Off"] = new object[0];
                return coauthor = c;
            }
        }
        static string ProjectKey(string cwd) { try { return Path.GetFullPath(cwd ?? "").TrimEnd('\\', '/').ToLowerInvariant(); } catch (Exception) { return (cwd ?? "").ToLowerInvariant(); } }
        bool CoAuthorOn(string cwd)
        {
            var c = CoAuthorConfig(); lock (gate)
            {
                if (!Convert.ToBoolean(c["Enabled"])) return false;
                string k = ProjectKey(cwd); foreach (object o in (System.Collections.IEnumerable)c["Off"]) if (Convert.ToString(o) == k) return false; return true;
            }
        }
        static bool TrailerName(string v) { return v != null && v.Length > 0 && v.Length < 80 && v.IndexOfAny(new[] { '<', '>', '\r', '\n', '"' }) < 0; }
        static bool CleanEmail(string v) { return v != null && v.Length < 120 && System.Text.RegularExpressions.Regex.IsMatch(v, @"^[^\s<>@""]+@[^\s<>@""]+\.[^\s<>@""]+$"); }

        public string CoAuthorTrailer(string cwd) { if (!CoAuthorOn(cwd)) return ""; var c = CoAuthorConfig(); lock (gate) return "Co-authored-by: " + Str(c, "Name") + " <" + Str(c, "Email") + ">"; }

        string HooksDir()
        {
            string h = Path.Combine(dir, "hooks");
            try
            {
                Directory.CreateDirectory(h); var bytes = new UTF8Encoding(false).GetBytes(HookScript);
                foreach (string n in HookNames) { string p = Path.Combine(h, n); if (!File.Exists(p) || !File.ReadAllBytes(p).SequenceEqual(bytes)) File.WriteAllBytes(p, bytes); }
            }
            catch (Exception) { return null; }
            return h;
        }

        /// <summary>Adds the hook and the co-author identity to an agent process's environment (only inside git repos with no hooksPath of their own).</summary>
        internal void CoAuthorEnv(ProcessStartInfo psi, string cwd)
        {
            try
            {
                if (!CoAuthorOn(cwd) || !GitTools.Available || GitTools.Root(cwd) == null) return;
                if (GitTools.Run(cwd, "config core.hooksPath", false).Trim() != "") return;
                string hooks = HooksDir(); if (hooks == null) return;
                int n; Int32.TryParse(psi.EnvironmentVariables.ContainsKey("GIT_CONFIG_COUNT") ? psi.EnvironmentVariables["GIT_CONFIG_COUNT"] : "0", out n);
                psi.EnvironmentVariables["GIT_CONFIG_KEY_" + n] = "core.hooksPath"; psi.EnvironmentVariables["GIT_CONFIG_VALUE_" + n] = hooks.Replace('\\', '/'); psi.EnvironmentVariables["GIT_CONFIG_COUNT"] = (n + 1).ToString();
                var c = CoAuthorConfig(); lock (gate) psi.EnvironmentVariables["LAICA_COAUTHOR"] = Str(c, "Name") + " <" + Str(c, "Email") + ">";
            }
            catch (Exception) { }
        }

        public object CoAuthorGet()
        {
            var c = CoAuthorConfig(); string email, name; bool on; object[] off; lock (gate) { on = Convert.ToBoolean(c["Enabled"]); email = Str(c, "Email"); name = Str(c, "Name"); off = ((System.Collections.IEnumerable)c["Off"]).Cast<object>().ToArray(); }
            var projects = new List<object>(); lock (gate) foreach (string p in sessions.Values.Select(s => s.Cwd).Where(x => !String.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) projects.Add(new Dictionary<string, object> { { "Cwd", p }, { "Enabled", on && !off.Contains((object)ProjectKey(p)) } });
            return new Dictionary<string, object> { { "Enabled", on }, { "Name", name }, { "Email", email }, { "Placeholder", email.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase) }, { "Projects", projects.ToArray() }, { "Trailer", "Co-authored-by: " + name + " <" + email + ">" } };
        }
        public object CoAuthorSet(Dictionary<string, object> d)
        {
            var c = CoAuthorConfig();
            lock (gate)
            {
                if (d.ContainsKey("Enabled")) c["Enabled"] = Convert.ToBoolean(d["Enabled"]);
                if (d.ContainsKey("Name")) { string n = Str(d, "Name").Trim(); if (n != "" && !TrailerName(n)) throw new ArgumentException("That name can't be used in a commit trailer."); if (n != "") c["Name"] = n; }
                if (d.ContainsKey("Email")) { string e = Str(d, "Email").Trim(); if (e == "") e = DefaultCoAuthorEmail; if (!CleanEmail(e)) throw new ArgumentException("That isn't a valid email address."); c["Email"] = e; }
                if (d.ContainsKey("Cwd")) { string k = ProjectKey(Str(d, "Cwd")); var off = ((System.Collections.IEnumerable)c["Off"]).Cast<object>().Select(Convert.ToString).Where(x => x != k).ToList(); if (d.ContainsKey("ProjectEnabled") && !Convert.ToBoolean(d["ProjectEnabled"])) off.Add(k); c["Off"] = off.Cast<object>().ToArray(); }
                SaveList("coauthor.json", new List<Dictionary<string, object>> { c });
            }
            return CoAuthorGet();
        }
    }
}
