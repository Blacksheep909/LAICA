using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Laica
{
    /// <summary>MCP servers and skills (managed through the agents' own CLIs / folders) and previews for Office documents and PDFs.</summary>
    public sealed partial class HarnessManager
    {
        // ---------- locations ----------
        static string Home() { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        static string ClaudeDir() { string d = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"); return String.IsNullOrWhiteSpace(d) ? Path.Combine(Home(), ".claude") : d; }
        static string ClaudeJson() { string d = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"); return Path.Combine(String.IsNullOrWhiteSpace(d) ? Home() : d, ".claude.json"); }
        static string CodexHomeDir() { string d = Environment.GetEnvironmentVariable("CODEX_HOME"); return String.IsNullOrWhiteSpace(d) ? Path.Combine(Home(), ".codex") : d; }
        static string SkillRoot(string target)
        {
            if (target == "claude") return Path.Combine(ClaudeDir(), "skills");
            if (target == "codex") return Path.Combine(CodexHomeDir(), "skills");
            if (target == "shared") return Path.Combine(Home(), ".agents", "skills");
            throw new ArgumentException("Choose where the skill belongs.");
        }
        static readonly Regex SafeName = new Regex("^[A-Za-z0-9][A-Za-z0-9_-]{0,48}$");

        // ---------- listing ----------
        public object Tools()
        {
            var mcp = new List<Dictionary<string, object>>(); var skills = new List<Dictionary<string, object>>();
            try
            {
                string cj = ClaudeJson();
                if (File.Exists(cj)) { var d = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(cj)); var m = Obj(d, "mcpServers"); if (m != null) foreach (var k in m) { var v = k.Value as Dictionary<string, object>; mcp.Add(new Dictionary<string, object> { { "Name", k.Key }, { "Target", "claude" }, { "Source", "Claude Code" }, { "Detail", v == null ? "" : (Str(v, "command") + " " + Str(v, "url")).Trim() } }); } }
            }
            catch (Exception) { }
            try
            {
                string ct = Path.Combine(CodexHomeDir(), "config.toml");
                if (File.Exists(ct)) foreach (Match mm in Regex.Matches(File.ReadAllText(ct), @"(?m)^\[mcp_servers\.([^\].]+)\]")) mcp.Add(new Dictionary<string, object> { { "Name", mm.Groups[1].Value.Trim('"') }, { "Target", "codex" }, { "Source", "Codex" }, { "Detail", "" } });
            }
            catch (Exception) { }
            foreach (var root in new[] { new[] { "claude", "Claude" }, new[] { "codex", "Codex" }, new[] { "shared", "Shared" } })
            {
                try
                {
                    string dir = SkillRoot(root[0]);
                    if (Directory.Exists(dir)) foreach (string d in Directory.GetDirectories(dir)) { string sm = Path.Combine(d, "SKILL.md"); skills.Add(new Dictionary<string, object> { { "Name", Path.GetFileName(d) }, { "Target", root[0] }, { "Source", root[1] }, { "Detail", File.Exists(sm) ? SkillDescription(File.ReadAllText(sm)) : "(no SKILL.md)" } }); }
                }
                catch (Exception) { }
            }
            return new Dictionary<string, object> { { "Mcp", mcp.ToArray() }, { "Skills", skills.ToArray() } };
        }
        static string SkillDescription(string md) { var m = Regex.Match(md, @"(?m)^description:\s*(.+)$"); return m.Success ? m.Groups[1].Value.Trim().Trim('"', '\'') : ""; }

        // ---------- MCP (through the CLIs, so their own validation and config formats are respected) ----------
        static string[] Tokenize(string line)
        {
            var list = new List<string>(); var sb = new StringBuilder(); char quote = '\0'; bool any = false;
            foreach (char c in line ?? "")
            {
                if (quote != '\0') { if (c == quote) quote = '\0'; else sb.Append(c); }
                else if (c == '"' || c == '\'') { quote = c; any = true; }
                else if (Char.IsWhiteSpace(c)) { if (sb.Length > 0 || any) { list.Add(sb.ToString()); sb.Clear(); any = false; } }
                else sb.Append(c);
            }
            if (sb.Length > 0 || any) list.Add(sb.ToString());
            return list.ToArray();
        }
        static string WinQuote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\""); int bs = 0;
            foreach (char c in a)
            {
                if (c == '\\') bs++;
                else if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); bs = 0; }
                else { sb.Append('\\', bs); bs = 0; sb.Append(c); }
            }
            sb.Append('\\', bs * 2); sb.Append('"'); return sb.ToString();
        }
        static string RunCli(string exe, string args, int timeoutMs)
        {
            var psi = new ProcessStartInfo { FileName = exe, Arguments = args, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            using (var p = Process.Start(psi))
            {
                p.StandardInput.Close(); var err = p.StandardError.ReadToEndAsync(); var outp = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } throw new TimeoutException("The command took too long."); }
                string text = (outp.Result + "\n" + err.Result).Trim();
                if (p.ExitCode != 0) throw new InvalidOperationException(text == "" ? "The command failed." : text);
                return text;
            }
        }
        string CliFor(string target)
        {
            string exe = target == "claude" ? FindClaude() : target == "codex" ? ModelCatalog.FindCodex() : null;
            if (exe == null) throw new InvalidOperationException((target == "claude" ? "Claude Code" : "Codex") + " isn't installed on this computer.");
            return exe;
        }
        public void McpAdd(string target, string name, string command, string url, string env)
        {
            if (!SafeName.IsMatch(name ?? "")) throw new ArgumentException("Use a short name with letters, numbers, - or _.");
            string exe = CliFor(target); var parts = Tokenize(command); var envs = Tokenize(env);
            if (String.IsNullOrWhiteSpace(url) && parts.Length == 0) throw new ArgumentException("Give a command to run, or a server URL.");
            if (!String.IsNullOrWhiteSpace(url) && !Regex.IsMatch(url.Trim(), @"^https?://", RegexOptions.IgnoreCase)) throw new ArgumentException("The URL must start with http:// or https://.");
            foreach (string e in envs) if (!Regex.IsMatch(e, @"^[A-Za-z_][A-Za-z0-9_]*=")) throw new ArgumentException("Environment values look like KEY=value.");
            var a = new List<string> { "mcp", "add" };
            if (target == "claude")
            {
                a.Add("--scope"); a.Add("user");
                if (!String.IsNullOrWhiteSpace(url)) { a.Add("--transport"); a.Add("http"); a.Add(name); a.Add(url.Trim()); foreach (string e in envs) { a.Add("-H"); a.Add(e.Replace("=", ": ")); } }
                else { foreach (string e in envs) { a.Add("-e"); a.Add(e); } a.Add(name); a.Add("--"); a.AddRange(parts); }
            }
            else
            {
                a.Add(name);
                if (!String.IsNullOrWhiteSpace(url)) { a.Add("--url"); a.Add(url.Trim()); }
                else { foreach (string e in envs) { a.Add("--env"); a.Add(e); } a.Add("--"); a.AddRange(parts); }
            }
            RunCli(exe, String.Join(" ", a.Select(WinQuote)), 60000); Raise();
        }
        public void McpRemove(string target, string name)
        {
            if (!SafeName.IsMatch(name ?? "")) throw new ArgumentException("That server name isn't valid.");
            string exe = CliFor(target);
            RunCli(exe, target == "claude" ? "mcp remove --scope user " + WinQuote(name) : "mcp remove " + WinQuote(name), 60000); Raise();
        }

        // ---------- skills (plain folders with a SKILL.md) ----------
        string SkillDir(string target, string name)
        {
            if (!SafeName.IsMatch(name ?? "")) throw new ArgumentException("Use a short name with letters, numbers, - or _.");
            string root = Path.GetFullPath(SkillRoot(target)); string dir = Path.GetFullPath(Path.Combine(root, name));
            if (!dir.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("That skill name isn't valid.");
            return dir;
        }
        public object SkillRead(string target, string name) { string f = Path.Combine(SkillDir(target, name), "SKILL.md"); if (!File.Exists(f)) throw new ArgumentException("That skill has no SKILL.md."); return File.ReadAllText(f); }
        public void SkillSave(string target, string name, string description, string body)
        {
            string dir = SkillDir(target, name);
            if (String.IsNullOrWhiteSpace(description)) throw new ArgumentException("Describe when the skill should be used.");
            if (String.IsNullOrWhiteSpace(body)) throw new ArgumentException("Write the skill's instructions.");
            if ((body ?? "").Length > 200000) throw new ArgumentException("The skill is too long.");
            Directory.CreateDirectory(dir);
            string text = "---\nname: " + name + "\ndescription: " + description.Replace("\r", " ").Replace("\n", " ").Trim() + "\n---\n\n" + body.Trim() + "\n";
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), text, new UTF8Encoding(false)); Raise();
        }
        public void SkillDelete(string target, string name)
        {
            string dir = SkillDir(target, name);
            if (!File.Exists(Path.Combine(dir, "SKILL.md"))) throw new ArgumentException("That folder isn't a skill, so it was left alone.");
            // Archive first: deleting a skill must never be unrecoverable.
            string trash = Path.Combine(this.dir, "deleted-skills", name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(Path.GetDirectoryName(trash));
            Directory.Move(dir, trash); Raise();
        }
    }

    /// <summary>Read-only previews of .docx, .xlsx, .pptx and .pdf files.</summary>
    public static class OfficePreview
    {
        static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main", A = "http://schemas.openxmlformats.org/drawingml/2006/main", S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main", R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships", PR = "http://schemas.openxmlformats.org/package/2006/relationships";
        const long MaxFile = 40L * 1024 * 1024, MaxPart = 24L * 1024 * 1024;

        public static object Read(string path, string ext)
        {
            var fi = new FileInfo(path);
            if (fi.Length > MaxFile) return new Dictionary<string, object> { { "Kind", "toolarge" }, { "Size", fi.Length } };
            try
            {
                if (ext == ".pdf") { if (fi.Length > 15L * 1024 * 1024) return new Dictionary<string, object> { { "Kind", "toolarge" }, { "Size", fi.Length } }; return new Dictionary<string, object> { { "Kind", "pdf" }, { "DataUri", "data:application/pdf;base64," + Convert.ToBase64String(File.ReadAllBytes(path)) } }; }
                using (var zip = ZipFile.OpenRead(path))
                {
                    if (ext == ".docx") return Markdown(Docx(zip));
                    if (ext == ".pptx") return Markdown(Pptx(zip));
                    return Xlsx(zip);
                }
            }
            catch (InvalidDataException) { return new Dictionary<string, object> { { "Kind", "binary" }, { "Size", fi.Length } }; }
        }
        static object Markdown(string text) { return new Dictionary<string, object> { { "Kind", "markdown" }, { "Text", text.Length > 400000 ? text.Substring(0, 400000) + "\n\n… (truncated)" : text } }; }

        static XDocument Part(ZipArchive zip, string name)
        {
            var e = zip.GetEntry(name); if (e == null) return null;
            if (e.Length > MaxPart) throw new InvalidDataException("Part too large.");
            using (var s = e.Open()) using (var ms = new MemoryStream())
            {
                var buf = new byte[81920]; int n; long total = 0;
                while ((n = s.Read(buf, 0, buf.Length)) > 0) { total += n; if (total > MaxPart) throw new InvalidDataException("Part too large."); ms.Write(buf, 0, n); }
                ms.Position = 0; var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null };
                using (var xr = System.Xml.XmlReader.Create(ms, settings)) return XDocument.Load(xr);
            }
        }

        static string Docx(ZipArchive zip)
        {
            var doc = Part(zip, "word/document.xml"); if (doc == null) throw new InvalidDataException("Not a Word document.");
            var sb = new StringBuilder(); var body = doc.Root.Element(W + "body"); if (body == null) return "";
            foreach (var el in body.Elements())
            {
                if (el.Name == W + "p") { string line = Para(el); if (line != null) sb.Append(line + "\n\n"); }
                else if (el.Name == W + "tbl")
                    foreach (var row in el.Elements(W + "tr")) sb.Append(String.Join(" | ", row.Elements(W + "tc").Select(c => String.Join(" ", c.Elements(W + "p").Select(p => RunText(p))).Trim())) + "\n\n");
            }
            return sb.ToString().Trim();
        }
        static string RunText(XElement p)
        {
            var sb = new StringBuilder();
            foreach (var e in p.Descendants()) { if (e.Name == W + "t") sb.Append(e.Value); else if (e.Name == W + "tab") sb.Append('\t'); else if (e.Name == W + "br") sb.Append('\n'); }
            return sb.ToString();
        }
        static string Para(XElement p)
        {
            string text = RunText(p).Trim(); if (text == "") return null;
            var style = p.Element(W + "pPr") == null ? null : p.Element(W + "pPr").Element(W + "pStyle"); string val = style == null || style.Attribute(W + "val") == null ? "" : style.Attribute(W + "val").Value;
            var m = Regex.Match(val, @"^(?:Heading|heading)\s*(\d)"); if (m.Success) return new string('#', Math.Min(4, Int32.Parse(m.Groups[1].Value))) + " " + text;
            if (val == "Title") return "# " + text;
            if (p.Element(W + "pPr") != null && p.Element(W + "pPr").Element(W + "numPr") != null) return "- " + text;
            return text;
        }

        static string Pptx(ZipArchive zip)
        {
            var slides = zip.Entries.Where(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$")).OrderBy(e => Int32.Parse(Regex.Match(e.FullName, @"\d+").Value)).Take(200).ToList();
            if (slides.Count == 0) throw new InvalidDataException("Not a presentation.");
            var sb = new StringBuilder(); int n = 0;
            foreach (var e in slides)
            {
                n++; var doc = Part(zip, e.FullName); sb.Append("## Slide " + n + "\n\n");
                bool first = true;
                foreach (var para in doc.Descendants(A + "p")) { string t = String.Concat(para.Descendants(A + "t").Select(x => x.Value)).Trim(); if (t == "") continue; sb.Append(first ? "**" + t + "**\n\n" : "- " + t + "\n"); first = false; }
                sb.Append("\n");
            }
            return sb.ToString().Trim();
        }

        static int ColIndex(string cellRef) { int n = 0; foreach (char c in cellRef) { if (!Char.IsLetter(c)) break; n = n * 26 + (Char.ToUpperInvariant(c) - 'A' + 1); } return n - 1; }
        static object Xlsx(ZipArchive zip)
        {
            var wb = Part(zip, "xl/workbook.xml"); if (wb == null) throw new InvalidDataException("Not a workbook.");
            var shared = new List<string>(); var ss = Part(zip, "xl/sharedStrings.xml");
            if (ss != null) foreach (var si in ss.Root.Elements(S + "si")) shared.Add(String.Concat(si.Descendants(S + "t").Select(t => t.Value)));
            var rels = new Dictionary<string, string>(); var rd = Part(zip, "xl/_rels/workbook.xml.rels");
            if (rd != null) foreach (var rel in rd.Root.Elements(PR + "Relationship")) rels[(string)rel.Attribute("Id")] = (string)rel.Attribute("Target");
            var sheets = new List<object>(); int idx = 0;
            foreach (var sh in wb.Root.Descendants(S + "sheet").Take(6))
            {
                idx++; string rid = (string)sh.Attribute(R + "id"); string target; string file = rid != null && rels.TryGetValue(rid, out target) ? (target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target) : "xl/worksheets/sheet" + idx + ".xml";
                var sd = Part(zip, file); var rows = new List<object>(); bool truncated = false;
                if (sd != null)
                {
                    var sheetData = sd.Root.Element(S + "sheetData");
                    if (sheetData != null) foreach (var row in sheetData.Elements(S + "row"))
                        {
                            if (rows.Count >= 300) { truncated = true; break; }
                            var cells = new List<string>();
                            foreach (var c in row.Elements(S + "c"))
                            {
                                int col = ColIndex((string)c.Attribute("r") ?? ""); if (col < 0 || col >= 40) continue; while (cells.Count <= col) cells.Add("");
                                string t = (string)c.Attribute("t"), v = c.Element(S + "v") == null ? "" : c.Element(S + "v").Value; string val = v;
                                if (t == "s") { int si; val = Int32.TryParse(v, out si) && si >= 0 && si < shared.Count ? shared[si] : ""; }
                                else if (t == "inlineStr") val = String.Concat(c.Descendants(S + "t").Select(x => x.Value));
                                else if (t == "b") val = v == "1" ? "TRUE" : "FALSE";
                                cells[col] = val;
                            }
                            rows.Add(cells.ToArray());
                        }
                }
                sheets.Add(new Dictionary<string, object> { { "Name", (string)sh.Attribute("name") ?? ("Sheet" + idx) }, { "Rows", rows.ToArray() }, { "Truncated", truncated } });
            }
            return new Dictionary<string, object> { { "Kind", "sheet" }, { "Sheets", sheets.ToArray() } };
        }
    }
}
