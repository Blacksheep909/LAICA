using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Laica
{
    /// <summary>
    /// The plugin library: a curated catalogue of MCP servers, starter skills and agent CLIs. It looks at the programs installed on this
    /// computer so the ones that fit come first, then installs through the same paths the Settings page uses. Nothing is downloaded
    /// from an address LAICA was not built with, and every install shows the exact command first.
    /// </summary>
    public sealed partial class HarnessManager
    {
        sealed class PlugInput { public string Key, Label, Hint, Kind; public bool Secret, Required = true; }
        sealed class Plug
        {
            public string Id, Name, Summary, Category, Kind = "mcp", Command, Url, Home, ServerName; public string[] Needs = new string[0], Tags = new string[0], Programs = new string[0]; public PlugInput[] Inputs = new PlugInput[0];
            public string Body; public string Package, Manager;   // skills: SKILL.md body; agents: npm/pip package
        }
        static PlugInput In(string key, string label, string hint, string kind, bool secret = false, bool required = true) { return new PlugInput { Key = key, Label = label, Hint = hint, Kind = kind, Secret = secret, Required = required }; }

        static readonly Plug[] Catalog = new[] {
            // ---- runtimes the other tools need ----
            new Plug { Id = "runtime-node", Kind = "runtime", Name = "Node.js", Category = "Runtimes", Summary = "Most MCP servers and agent CLIs are installed and run with Node.js. Install it once and they all work.", Package = "OpenJS.NodeJS.LTS", Manager = "winget", Home = "https://nodejs.org" },
            new Plug { Id = "runtime-uv", Kind = "runtime", Name = "uv (Python tools)", Category = "Runtimes", Summary = "Runs the Python-based MCP servers (Blender, Git, Fetch, SQLite) without any setup.", Package = "astral-sh.uv", Manager = "winget", Home = "https://docs.astral.sh/uv/" },
            // ---- agents ----
            new Plug { Id = "agent-gemini", Kind = "agent", Name = "Gemini CLI", Category = "Agents", Summary = "Google's coding agent. Once installed it appears as a vendor you can chat with and use in teams.", Package = "@google/gemini-cli", Manager = "npm", Needs = new[] { "node" }, Home = "https://github.com/google-gemini/gemini-cli", Tags = new[] { "Google" } },
            new Plug { Id = "agent-qwen", Kind = "agent", Name = "Qwen Code", Category = "Agents", Summary = "Alibaba's open coding agent, works with local and hosted Qwen models.", Package = "@qwen-code/qwen-code", Manager = "npm", Needs = new[] { "node" }, Home = "https://github.com/QwenLM/qwen-code" },
            new Plug { Id = "agent-opencode", Kind = "agent", Name = "OpenCode", Category = "Agents", Summary = "Open-source terminal agent that talks to many providers.", Package = "opencode-ai", Manager = "npm", Needs = new[] { "node" }, Home = "https://opencode.ai" },
            new Plug { Id = "agent-claude", Kind = "agent", Name = "Claude Code", Category = "Agents", Summary = "Anthropic's coding agent. LAICA drives it for chats and teams.", Package = "@anthropic-ai/claude-code", Manager = "npm", Needs = new[] { "node" }, Home = "https://docs.anthropic.com/claude-code" },
            new Plug { Id = "agent-codex", Kind = "agent", Name = "Codex CLI", Category = "Agents", Summary = "OpenAI's coding agent, the other half of a Codex and Claude team.", Package = "@openai/codex", Manager = "npm", Needs = new[] { "node" }, Home = "https://github.com/openai/codex" },
            new Plug { Id = "agent-aider", Kind = "agent", Name = "Aider", Category = "Agents", Summary = "Pair-programming agent that edits your git repository.", Package = "aider-chat", Manager = "pip", Needs = new[] { "python" }, Home = "https://aider.chat", Programs = new[] { "git" } },
            // ---- MCP servers ----
            new Plug { Id = "filesystem", Kind = "mcp", Name = "Filesystem", Category = "Essentials", Summary = "Lets agents read and write files in one folder you choose, nowhere else.", Command = "npx -y @modelcontextprotocol/server-filesystem {folder}", Needs = new[] { "node" }, Inputs = new[] { In("folder", "Folder to share", @"C:\Users\you\Documents", "folder") }, Home = "https://github.com/modelcontextprotocol/servers" },
            new Plug { Id = "memory", Kind = "mcp", Name = "Memory", Category = "Essentials", Summary = "A small knowledge graph so agents remember people, projects and decisions between chats.", Command = "npx -y @modelcontextprotocol/server-memory", Needs = new[] { "node" }, Home = "https://github.com/modelcontextprotocol/servers" },
            new Plug { Id = "sequential-thinking", Kind = "mcp", Name = "Sequential Thinking", Category = "Essentials", Summary = "Helps agents break hard problems into steps and revise them.", Command = "npx -y @modelcontextprotocol/server-sequential-thinking", Needs = new[] { "node" }, Home = "https://github.com/modelcontextprotocol/servers" },
            new Plug { Id = "context7", Kind = "mcp", Name = "Context7 docs", Category = "Developer", Summary = "Up-to-date library documentation pulled into the chat, so code uses current APIs.", Command = "npx -y @upstash/context7-mcp", Needs = new[] { "node" }, Home = "https://github.com/upstash/context7", Programs = new[] { "vscode", "git", "node" } },
            new Plug { Id = "fetch", Kind = "mcp", Name = "Web fetch", Category = "Essentials", Summary = "Lets an agent read a web page and turn it into clean text.", Command = "uvx mcp-server-fetch", Needs = new[] { "uv" }, Home = "https://github.com/modelcontextprotocol/servers" },
            new Plug { Id = "git", Kind = "mcp", Name = "Git", Category = "Developer", Summary = "Read history, diffs and branches of a repository through a dedicated tool.", Command = "uvx mcp-server-git --repository {repo}", Needs = new[] { "uv", "git" }, Inputs = new[] { In("repo", "Repository folder", @"C:\Users\you\code\my-project", "folder") }, Programs = new[] { "git" }, Home = "https://github.com/modelcontextprotocol/servers" },
            new Plug { Id = "github", Kind = "mcp", Name = "GitHub", Category = "Developer", Summary = "Issues, pull requests and code search on GitHub. Runs GitHub's own server in Docker.", Command = "docker run -i --rm -e GITHUB_PERSONAL_ACCESS_TOKEN ghcr.io/github/github-mcp-server", Needs = new[] { "docker" }, Inputs = new[] { In("GITHUB_PERSONAL_ACCESS_TOKEN", "Personal access token", "github_pat_...", "env", true) }, Programs = new[] { "github", "git", "docker" }, Home = "https://github.com/github/github-mcp-server" },
            new Plug { Id = "playwright", Kind = "mcp", Name = "Playwright browser", Category = "Browser", Summary = "Lets an agent open pages, click and fill forms to test your web app.", Command = "npx -y @playwright/mcp@latest", Needs = new[] { "node" }, Programs = new[] { "chrome", "firefox" }, Home = "https://github.com/microsoft/playwright-mcp" },
            new Plug { Id = "chrome-devtools", Kind = "mcp", Name = "Chrome DevTools", Category = "Browser", Summary = "Inspect a live Chrome page: console, network, performance, screenshots.", Command = "npx -y chrome-devtools-mcp@latest", Needs = new[] { "node" }, Programs = new[] { "chrome" }, Home = "https://github.com/ChromeDevTools/chrome-devtools-mcp" },
            new Plug { Id = "blender", Kind = "mcp", Name = "Blender", Category = "Creative", Summary = "Control Blender from the chat: build scenes, change materials, run scripts. Also needs the Blender MCP add-on enabled in Blender.", Command = "uvx blender-mcp", Needs = new[] { "uv" }, Programs = new[] { "blender" }, Home = "https://github.com/ahujasid/blender-mcp" },
            new Plug { Id = "figma", Kind = "mcp", Name = "Figma Dev Mode", Category = "Creative", Summary = "Gives agents the design you have selected in the Figma desktop app. Turn on the Dev Mode MCP server in Figma first.", Url = "http://127.0.0.1:3845/mcp", Programs = new[] { "figma" }, Home = "https://help.figma.com/hc/en-us/articles/32132100833559" },
            new Plug { Id = "obsidian", Kind = "mcp", Name = "Obsidian vault", Category = "Productivity", Summary = "Read and search the notes in an Obsidian vault.", Command = "npx -y @mauricio.wolff/mcp-obsidian@latest {vault}", Needs = new[] { "node" }, Inputs = new[] { In("vault", "Vault folder", @"C:\Users\you\Documents\Vault", "folder") }, Programs = new[] { "obsidian" }, Home = "https://github.com/bitbonsai/mcp-obsidian" },
            new Plug { Id = "notion", Kind = "mcp", Name = "Notion", Category = "Productivity", Summary = "Search and edit pages and databases in your Notion workspace.", Command = "npx -y @notionhq/notion-mcp-server", Needs = new[] { "node" }, Inputs = new[] { In("NOTION_TOKEN", "Integration token", "ntn_...", "env", true) }, Programs = new[] { "notion" }, Home = "https://github.com/makenotion/notion-mcp-server" },
            new Plug { Id = "postgres", Kind = "mcp", Name = "PostgreSQL", Category = "Data", Summary = "Ask questions of a Postgres database (read-only queries).", Command = "npx -y @modelcontextprotocol/server-postgres {connection}", Needs = new[] { "node" }, Inputs = new[] { In("connection", "Connection string", "postgresql://user:password@localhost/db", "text", true) }, Programs = new[] { "postgres" }, Home = "https://github.com/modelcontextprotocol/servers" },
            new Plug { Id = "sqlite", Kind = "mcp", Name = "SQLite", Category = "Data", Summary = "Explore and query a SQLite database file.", Command = "uvx mcp-server-sqlite --db-path {db}", Needs = new[] { "uv" }, Inputs = new[] { In("db", "Database file", @"C:\Users\you\data\app.db", "file") }, Programs = new[] { "sqlite" }, Home = "https://github.com/modelcontextprotocol/servers" },
            new Plug { Id = "brave-search", Kind = "mcp", Name = "Brave Search", Category = "Essentials", Summary = "Web search for agents. Needs a free Brave Search API key.", Command = "npx -y @modelcontextprotocol/server-brave-search", Needs = new[] { "node" }, Inputs = new[] { In("BRAVE_API_KEY", "Brave API key", "BSA...", "env", true) }, Home = "https://brave.com/search/api/" },
            new Plug { Id = "desktop-commander", Kind = "mcp", Name = "Desktop Commander", Category = "Developer", Summary = "Run terminal commands and edit files with long-running process support.", Command = "npx -y @wonderwhy-er/desktop-commander@latest", Needs = new[] { "node" }, Home = "https://github.com/wonderwhy-er/DesktopCommanderMCP" },
            new Plug { Id = "sentry", Kind = "mcp", Name = "Sentry", Category = "Developer", Summary = "Look into errors and issues from your Sentry projects. Sign in with your Sentry account when asked.", Url = "https://mcp.sentry.dev/mcp", Home = "https://docs.sentry.io/product/sentry-mcp/" },
            // ---- starter skills ----
            new Plug { Id = "skill-code-review", Kind = "skill", Name = "Code review", Category = "Skills", ServerName = "code-review", Summary = "A checklist-driven review: correctness first, then risks, tests and clarity.", Body = "Review the changes the user points to. Work in this order:\n\n1. Correctness: logic errors, off-by-one, null handling, race conditions.\n2. Risk: security, data loss, breaking changes to public behaviour.\n3. Tests: what is untested, and which test would catch the bug you found.\n4. Clarity: naming, dead code, comments that lie.\n\nReport findings most severe first. For each: file and line, what is wrong, a concrete failing example, and a suggested fix. Say clearly when you found nothing serious." },
            new Plug { Id = "skill-commit", Kind = "skill", Name = "Commit messages", Category = "Skills", ServerName = "commit-messages", Summary = "Writes clear, conventional commit messages from the staged changes.", Body = "When asked to commit or to write a commit message:\n\n- Read the staged diff first.\n- Subject line: imperative mood, under 60 characters, no full stop.\n- Body: why the change was needed, not a list of files.\n- Mention breaking changes explicitly.\n- Never include secrets or personal paths in a message." },
            new Plug { Id = "skill-explain", Kind = "skill", Name = "Explain simply", Category = "Skills", ServerName = "explain-simply", Summary = "Explains code or a concept for someone new, with one example.", Body = "When asked to explain something:\n\n1. One sentence: what it is for.\n2. A small analogy from everyday life.\n3. The idea in 3 short steps.\n4. One runnable or concrete example.\n5. The common mistake to avoid.\n\nAvoid jargon unless you define it the first time." },
            new Plug { Id = "skill-tests", Kind = "skill", Name = "Test first", Category = "Skills", ServerName = "test-first", Summary = "Writes a failing test before changing code, then makes it pass.", Body = "For every bug fix or feature:\n\n1. Write the smallest test that fails for the right reason, and run it to see it fail.\n2. Change the code until it passes.\n3. Run the whole suite.\n4. Report the test name, the failure message you saw, and the final result.\n\nDo not skip step 1." },
            new Plug { Id = "skill-release-notes", Kind = "skill", Name = "Release notes", Category = "Skills", ServerName = "release-notes", Summary = "Turns recent commits into friendly release notes.", Body = "Read the commits since the last tag. Group them as Added, Changed, Fixed. Write for users, not developers: say what they can now do, not which function changed. Skip refactors with no visible effect. Keep each line under 120 characters." }
        };

        // ---------- what is on this computer ----------
        static string[] startMenuNames; static DateTime startMenuAt;
        static string[] StartMenuNames()
        {
            if (startMenuNames != null && (DateTime.UtcNow - startMenuAt).TotalMinutes < 5) return startMenuNames;
            var names = new List<string>();
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) })
            {
                try { string dir = Path.Combine(root, "Programs"); if (Directory.Exists(dir)) names.AddRange(Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories).Select(f => Path.GetFileNameWithoutExtension(f).ToLowerInvariant())); } catch (Exception) { }
            }
            startMenuNames = names.Distinct().ToArray(); startMenuAt = DateTime.UtcNow; return startMenuNames;
        }
        static bool AppNamed(params string[] words) { return StartMenuNames().Any(n => words.Any(w => n.Contains(w))); }
        static bool DirHas(string root, string pattern) { try { return Directory.Exists(root) && Directory.GetDirectories(root, pattern).Length > 0; } catch (Exception) { return false; } }

        /// <summary>The kinds of software found on this computer, by key. Cheap enough to call on every library open.</summary>
        /// <summary>A program installed after LAICA started is not on LAICA's PATH yet; read the current machine and user PATH.</summary>
        static void RefreshPath()
        {
            try
            {
                string m = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "", u = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "", cur = Environment.GetEnvironmentVariable("PATH") ?? "";
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var parts = new List<string>();
                foreach (string d in (cur + ";" + m + ";" + u).Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries)) { string e = Environment.ExpandEnvironmentVariables(d.Trim()); if (e != "" && seen.Add(e)) parts.Add(e); }
                Environment.SetEnvironmentVariable("PATH", String.Join(Path.PathSeparator.ToString(), parts));
            }
            catch (Exception) { }
        }

        Dictionary<string, string> DetectPrograms()
        {
            RefreshPath();
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), roam = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var found = new Dictionary<string, string>();
            Action<string, string, bool> add = (key, label, ok) => { if (ok && !found.ContainsKey(key)) found[key] = label; };
            add("node", "Node.js", FindAny("npx.cmd", "npx.exe") != null || File.Exists(Path.Combine(pf, "nodejs", "npx.cmd")));
            add("uv", "uv (Python tools)", FindAny("uvx.exe", "uv.exe") != null || File.Exists(Path.Combine(local, "Programs", "uv", "uvx.exe")) || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "uvx.exe")));
            add("python", "Python", FindAny("python.exe", "py.exe") != null || DirHas(Path.Combine(local, "Programs", "Python"), "Python*"));
            add("docker", "Docker", FindAny("docker.exe") != null || AppNamed("docker desktop"));
            add("git", "Git", FindAny("git.exe") != null || Directory.Exists(Path.Combine(pf, "Git")));
            add("github", "GitHub Desktop", AppNamed("github desktop"));
            add("vscode", "Visual Studio Code", FindAny("code.cmd", "code.exe") != null || AppNamed("visual studio code") || Directory.Exists(Path.Combine(local, "Programs", "Microsoft VS Code")));
            add("chrome", "Google Chrome", File.Exists(Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe")) || File.Exists(Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe")) || File.Exists(Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe")));
            add("edge", "Microsoft Edge", File.Exists(Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe")) || File.Exists(Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe")));
            add("firefox", "Firefox", File.Exists(Path.Combine(pf, "Mozilla Firefox", "firefox.exe")) || File.Exists(Path.Combine(pf86, "Mozilla Firefox", "firefox.exe")));
            add("blender", "Blender", DirHas(Path.Combine(pf, "Blender Foundation"), "Blender*") || AppNamed("blender"));
            add("figma", "Figma", Directory.Exists(Path.Combine(local, "Figma")) || AppNamed("figma"));
            add("obsidian", "Obsidian", File.Exists(Path.Combine(local, "Programs", "Obsidian", "Obsidian.exe")) || AppNamed("obsidian") || Directory.Exists(Path.Combine(roam, "obsidian")));
            add("notion", "Notion", File.Exists(Path.Combine(local, "Programs", "Notion", "Notion.exe")) || AppNamed("notion"));
            add("postgres", "PostgreSQL", FindAny("psql.exe") != null || DirHas(Path.Combine(pf, "PostgreSQL"), "*"));
            add("sqlite", "SQLite", FindAny("sqlite3.exe") != null || AppNamed("db browser for sqlite"));
            return found;
        }

        // ---------- the library ----------
        public object PluginLibrary()
        {
            var programs = DetectPrograms(); var tools = (Dictionary<string, object>)Tools();
            var mcpHave = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var skillHave = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object o in (object[])tools["Mcp"]) { var d = (Dictionary<string, object>)o; mcpHave.Add(Str(d, "Target") + "|" + Str(d, "Name")); }
            foreach (object o in (object[])tools["Skills"]) { var d = (Dictionary<string, object>)o; skillHave.Add(Str(d, "Target") + "|" + Str(d, "Name")); }
            var all = Harnesses(); bool claude = all.Any(h => h.Id == "claude" && h.Available), codex = all.Any(h => h.Id == "codex" && h.Available);
            var items = new List<object>();
            foreach (var p in Catalog)
            {
                var needsMissing = p.Needs.Where(n => !programs.ContainsKey(n)).Select(n => n == "node" ? "Node.js" : n == "uv" ? "uv (install from astral.sh/uv)" : n == "python" ? "Python" : n == "docker" ? "Docker" : n == "git" ? "Git" : n).ToArray();
                var because = p.Programs.Where(programs.ContainsKey).Select(k => programs[k]).Where(n => n != "Node.js" && n != "uv (Python tools)" && n != "Python").ToArray();
                bool installedAgent = p.Kind == "agent" ? all.Any(h => h.Id == p.Id.Replace("agent-", "") && h.Available) : p.Kind == "runtime" && programs.ContainsKey(p.Id == "runtime-node" ? "node" : "uv");
                string name = String.IsNullOrEmpty(p.ServerName) ? p.Id : p.ServerName;
                var installedOn = new List<string>();
                foreach (string t in new[] { "claude", "codex", "shared" })
                {
                    if (p.Kind == "mcp" && t != "shared" && mcpHave.Contains(t + "|" + name)) installedOn.Add(t);
                    if (p.Kind == "skill" && skillHave.Contains(t + "|" + name)) installedOn.Add(t);
                }
                items.Add(new Dictionary<string, object> {
                    { "Id", p.Id }, { "Kind", p.Kind }, { "Name", p.Name }, { "Summary", p.Summary }, { "Category", p.Category }, { "Home", p.Home ?? "" },
                    { "Needs", needsMissing }, { "Suggested", p.Kind == "runtime" ? !installedAgent && Catalog.Any(c => c.Needs.Contains(p.Id == "runtime-node" ? "node" : "uv") && c.Programs.Any(programs.ContainsKey)) : because.Length > 0 && !installedAgent && installedOn.Count == 0 },
                    { "Because", p.Kind == "runtime" ? "Needed by tools that fit your programs" : because.Length > 0 ? String.Join(", ", because.Distinct()) + " is installed" : "" },
                    { "Inputs", p.Inputs.Select(i => (object)new Dictionary<string, object> { { "Key", i.Key }, { "Label", i.Label }, { "Hint", i.Hint }, { "Kind", i.Kind }, { "Secret", i.Secret }, { "Required", i.Required } }).ToArray() },
                    { "Preview", p.Manager == "winget" ? "winget install --id " + p.Package + " -e --silent" : p.Kind == "agent" ? (p.Manager == "npm" ? "npm install -g " + p.Package : "python -m pip install " + p.Package) : p.Kind == "skill" ? "Creates ~/.claude/skills/" + name + "/SKILL.md (or the Codex or shared skills folder)" : (p.Url ?? p.Command) },
                    { "InstalledOn", installedOn.ToArray() }, { "AgentInstalled", installedAgent }
                });
            }
            return new Dictionary<string, object> {
                { "Items", items.ToArray() }, { "Programs", programs.Values.OrderBy(x => x).ToArray() },
                { "Targets", new Dictionary<string, object> { { "claude", claude }, { "codex", codex } } }
            };
        }

        static string FillTemplate(string template, Dictionary<string, string> values)
        {
            return Regex.Replace(template, @"\{([A-Za-z_]+)\}", m =>
            {
                string v; if (!values.TryGetValue(m.Groups[1].Value, out v) || String.IsNullOrWhiteSpace(v)) throw new ArgumentException("Fill in every field first.");
                if (v.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new ArgumentException("A value can't contain line breaks.");
                return v.IndexOf(' ') >= 0 || v.IndexOf('"') >= 0 ? "\"" + v.Replace("\"", "") + "\"" : v;
            });
        }

        public object PluginInstall(string id, string[] targets, Dictionary<string, string> values)
        {
            var p = Catalog.FirstOrDefault(x => x.Id == id); if (p == null) throw new ArgumentException("That plugin isn't in the library.");
            values = values ?? new Dictionary<string, string>(); targets = (targets ?? new string[0]).Where(t => t == "claude" || t == "codex" || t == "shared").Distinct().ToArray();
            if (p.Kind == "agent" || p.Kind == "runtime") return InstallAgent(p);
            if (targets.Length == 0) throw new ArgumentException("Choose where to install it.");
            var missing = p.Needs.Where(n => !DetectPrograms().ContainsKey(n)).ToArray();
            if (missing.Length > 0 && p.Kind == "mcp") throw new InvalidOperationException("This needs " + String.Join(" and ", missing.Select(n => n == "node" ? "Node.js" : n == "uv" ? "uv" : n)) + " installed first.");
            foreach (var i in p.Inputs) if (i.Required && String.IsNullOrWhiteSpace(values.ContainsKey(i.Key) ? values[i.Key] : "")) throw new ArgumentException(i.Label + " is required.");
            var done = new List<string>();
            if (p.Kind == "skill")
            {
                foreach (string t in targets) { SkillSave(t, p.ServerName, p.Summary, p.Body); done.Add(t); }
            }
            else
            {
                string cmd = p.Command == null ? "" : FillTemplate(p.Command, values.Where(kv => p.Inputs.Any(i => i.Key == kv.Key && i.Kind != "env")).ToDictionary(kv => kv.Key, kv => kv.Value));
                string env = String.Join(" ", p.Inputs.Where(i => i.Kind == "env").Select(i => i.Key + "=" + (values[i.Key].IndexOf(' ') >= 0 ? "\"" + values[i.Key] + "\"" : values[i.Key])));
                foreach (string t in targets.Where(t => t == "claude" || t == "codex")) { McpAdd(t, p.Id, LatestCommand(cmd), p.Url ?? "", env); done.Add(t); }
                if (done.Count == 0) throw new ArgumentException("MCP servers install into Claude Code or Codex.");
            }
            return new Dictionary<string, object> { { "Installed", done.ToArray() } };
        }

        public object PluginRemove(string id, string target)
        {
            var p = Catalog.FirstOrDefault(x => x.Id == id); if (p == null) throw new ArgumentException("That plugin isn't in the library.");
            if (p.Kind == "mcp") McpRemove(target, p.Id); else if (p.Kind == "skill") SkillDelete(target, p.ServerName); else throw new ArgumentException("Uninstall agents from Windows settings.");
            return true;
        }

        object InstallAgent(Plug p)
        {
            string tool, args;
            if (p.Manager == "winget")
            {
                tool = FindAny("winget.exe") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
                if (!File.Exists(tool)) throw new InvalidOperationException("The Windows Package Manager (winget) isn't available on this computer. Install " + p.Name + " from its website instead.");
                if (!Regex.IsMatch(p.Package, @"^[A-Za-z0-9._\-]+$")) throw new ArgumentException("That package name isn't valid.");
                RunCli(tool, "install --id " + p.Package + " -e --silent --accept-package-agreements --accept-source-agreements", 900000); RefreshPath(); Raise();
                return new Dictionary<string, object> { { "Installed", new[] { p.Id } }, { "Output", "" } };
            }
            if (p.Manager == "npm") { tool = FindAny("npm.cmd", "npm.exe") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd"); args = "install -g " + p.Package; }
            else { tool = FindAny("python.exe", "py.exe"); if (tool == null) throw new InvalidOperationException("Python isn't installed."); args = "-m pip install --user " + p.Package; }
            if (!File.Exists(tool)) throw new InvalidOperationException((p.Manager == "npm" ? "Node.js" : "Python") + " isn't installed. Install it first, then try again.");
            if (!Regex.IsMatch(p.Package, @"^[@A-Za-z0-9._/\-]+$")) throw new ArgumentException("That package name isn't valid.");
            string exe = tool, line = args;
            if (tool.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) { exe = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"; line = "/c \"\"" + tool + "\" " + args + "\""; }
            string output = RunCli(exe, line, 600000);
            Raise();
            return new Dictionary<string, object> { { "Installed", new[] { p.Id } }, { "Output", output.Length > 600 ? output.Substring(output.Length - 600) : output } };
        }
    }
}
