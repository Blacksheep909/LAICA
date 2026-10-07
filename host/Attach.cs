using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Laica
{
    /// <summary>
    /// Files for a chat: picked from disk, dropped, or pasted. A file already inside the chat's folder is referenced where it is;
    /// anything else is copied into the folder's .laica/attachments so the agent can read it with its normal permissions.
    /// Also speech-to-text through an OpenAI-compatible service for dictation.
    /// </summary>
    public sealed partial class HarnessManager
    {
        const long MaxAttachBytes = 200L * 1024 * 1024;
        static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

        string AttachFolder(string sessionId, string cwd)
        {
            string folder = cwd;
            if (!String.IsNullOrEmpty(sessionId)) lock (gate) folder = Get(sessionId).Cwd;
            if (String.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) throw new ArgumentException("Choose a project folder first.");
            return Path.GetFullPath(folder);
        }

        static bool InsideFolder(string path, string folder)
        {
            string f = folder.TrimEnd('\\', '/') + "\\";
            return Path.GetFullPath(path).StartsWith(f, StringComparison.OrdinalIgnoreCase);
        }

        static string CleanName(string name)
        {
            name = Path.GetFileName((name ?? "").Replace('/', '\\'));
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            name = name.Trim().TrimEnd('.');
            if (name == "") name = "file";
            return name.Length > 120 ? name.Substring(name.Length - 120) : name;
        }

        /// <summary>Keeps the attachments folder out of git without touching tracked files.</summary>
        static void KeepOutOfGit(string folder)
        {
            try
            {
                string root = GitTools.Root(folder); if (root == null) return;
                string gitDir = Path.Combine(root, ".git"); if (!Directory.Exists(gitDir)) return;
                string info = Path.Combine(gitDir, "info"); Directory.CreateDirectory(info);
                string ex = Path.Combine(info, "exclude"); string text = File.Exists(ex) ? File.ReadAllText(ex) : "";
                if (text.Split('\n').Any(l => l.Trim() == ".laica/")) return;
                File.AppendAllText(ex, (text.Length > 0 && !text.EndsWith("\n") ? "\n" : "") + ".laica/\n");
            }
            catch (Exception) { }
        }

        static string UniquePath(string dir, string name)
        {
            string p = Path.Combine(dir, name); if (!File.Exists(p)) return p;
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int i = 2; i < 1000; i++) { p = Path.Combine(dir, stem + " (" + i + ")" + ext); if (!File.Exists(p)) return p; }
            return Path.Combine(dir, stem + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ext);
        }

        static Dictionary<string, object> AttachInfo(string path, bool copied, bool folder)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant(); long size = 0; if (!folder) { try { size = new FileInfo(path).Length; } catch (Exception) { } }
            return new Dictionary<string, object> { { "Name", Path.GetFileName(path.TrimEnd('\\', '/')) }, { "Path", path }, { "Size", size }, { "Kind", folder ? "folder" : ImageExt.Contains(ext) ? "image" : "file" }, { "Copied", copied } };
        }

        public object AttachStage(string sessionId, string cwd, IEnumerable<string> paths)
        {
            string folder = AttachFolder(sessionId, cwd); var result = new List<object>(); int count = 0;
            foreach (string raw in paths ?? new string[0])
            {
                if (String.IsNullOrWhiteSpace(raw) || ++count > 50) continue;
                string path; try { path = Path.GetFullPath(raw.Trim().Trim('"')); } catch (Exception) { throw new ArgumentException("That isn't a valid path."); }
                if (Directory.Exists(path)) { result.Add(AttachInfo(path, false, true)); continue; }
                if (!File.Exists(path)) throw new ArgumentException("That file can't be found: " + Path.GetFileName(path));
                if (new FileInfo(path).Length > MaxAttachBytes) throw new ArgumentException(Path.GetFileName(path) + " is larger than 200 MB.");
                if (InsideFolder(path, folder)) { result.Add(AttachInfo(path, false, false)); continue; }
                string dir = Path.Combine(folder, ".laica", "attachments"); Directory.CreateDirectory(dir); KeepOutOfGit(folder);
                string target = UniquePath(dir, CleanName(path)); File.Copy(path, target, false);
                result.Add(AttachInfo(target, true, false));
            }
            return result.ToArray();
        }

        /// <summary>Receives a file in pieces (dropped into a browser, or pasted from the clipboard) and writes it into the attachments folder.</summary>
        public object AttachSave(string sessionId, string cwd, string name, string base64, string target, bool append)
        {
            string folder = AttachFolder(sessionId, cwd); string dir = Path.Combine(folder, ".laica", "attachments"); string path;
            byte[] data; try { data = Convert.FromBase64String(base64 ?? ""); } catch (FormatException) { throw new ArgumentException("That file data isn't valid."); }
            if (append)
            {
                if (String.IsNullOrEmpty(target)) throw new ArgumentException("Missing the file to continue.");
                path = Path.GetFullPath(target); if (!InsideFolder(path, dir)) throw new ArgumentException("That isn't an attachment of this chat.");
            }
            else { Directory.CreateDirectory(dir); KeepOutOfGit(folder); path = UniquePath(dir, CleanName(name)); }
            if (File.Exists(path) && new FileInfo(path).Length + data.Length > MaxAttachBytes) { try { File.Delete(path); } catch (Exception) { } throw new ArgumentException("That file is larger than 200 MB."); }
            using (var fs = new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write)) fs.Write(data, 0, data.Length);
            return AttachInfo(path, true, false);
        }

        // ---------- dictation through a speech-to-text service ----------
        ServiceConnection SpeechService(string serviceId, out string model)
        {
            var all = services(); ServiceConnection svc = null; model = "whisper-1";
            if (!String.IsNullOrEmpty(serviceId)) svc = all.FirstOrDefault(x => x.Id == serviceId);
            Func<ServiceConnection, string, bool> host = (x, h) => (x.BaseUrl ?? "").IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0;
            if (svc == null) svc = all.FirstOrDefault(x => host(x, "openai.com"));
            if (svc == null) svc = all.FirstOrDefault(x => host(x, "groq.com"));
            if (svc == null) return null;
            if (host(svc, "openai.com")) model = "gpt-4o-mini-transcribe"; else if (host(svc, "groq.com")) model = "whisper-large-v3-turbo";
            return svc;
        }

        /// <summary>Whether cloud dictation is available, and through which service.</summary>
        public object SpeechStatus()
        {
            string model; var svc = SpeechService(null, out model);
            return new Dictionary<string, object> { { "Cloud", svc != null }, { "Service", svc != null ? svc.Name : "" }, { "Model", svc != null ? model : "" } };
        }

        public string Transcribe(string base64, string mime, string serviceId, string language)
        {
            string model; var svc = SpeechService(serviceId, out model);
            if (svc == null) throw new InvalidOperationException("Add an OpenAI or Groq service under Settings to dictate with higher accuracy.");
            byte[] data; try { data = Convert.FromBase64String(base64 ?? ""); } catch (FormatException) { throw new ArgumentException("That audio isn't valid."); }
            if (data.Length < 400) throw new ArgumentException("That recording was too short.");
            string ext = (mime ?? "").Contains("ogg") ? "ogg" : (mime ?? "").Contains("mp4") ? "m4a" : (mime ?? "").Contains("wav") ? "wav" : "webm";
            var fields = new Dictionary<string, string> { { "model", model }, { "response_format", "json" } };
            if (!String.IsNullOrWhiteSpace(language) && System.Text.RegularExpressions.Regex.IsMatch(language, "^[a-z]{2}$")) fields["language"] = language;
            string raw; try { raw = ApiServices.PostMultipartAsync(svc, "audio/transcriptions", fields, "file", "dictation." + ext, String.IsNullOrEmpty(mime) ? "audio/webm" : mime.Split(';')[0], data, CancellationToken.None).Result; }
            catch (AggregateException ex) { throw new InvalidOperationException(Unwrap(ex)); }
            var o = json.Deserialize<Dictionary<string, object>>(raw); return Str(o, "text").Trim();
        }

        /// <summary>The conversation as Markdown, for saving or sharing.</summary>
        public string ExportMarkdown(string id)
        {
            Session s; var sb = new StringBuilder();
            lock (gate)
            {
                s = Get(id); sb.AppendLine("# " + s.Title).AppendLine().AppendLine("*" + VendorName(VendorKey(s)) + (String.IsNullOrEmpty(s.Model) ? "" : " - " + s.Model) + " - " + s.Cwd + "*").AppendLine();
                foreach (var e in s.Events)
                {
                    string k = Str(e, "Kind"), tx = Str(e, "Text");
                    if (k == "user") sb.AppendLine("## You").AppendLine().AppendLine(tx).AppendLine();
                    else if (k == "assistant") sb.AppendLine(tx).AppendLine();
                    else if (k == "tool") sb.AppendLine("> Tool: " + tx + (Str(e, "Detail") != "" ? " " + Str(e, "Detail") : "")).AppendLine();
                    else if (k == "error") sb.AppendLine("> **Error:** " + tx).AppendLine();
                }
            }
            return sb.ToString();
        }    
        // ---------- images the agent saw or made (screenshots, generated pictures) ----------
        const long MaxImageBytes = 12L * 1024 * 1024;
        static readonly System.Text.RegularExpressions.Regex ImageName = new System.Text.RegularExpressions.Regex("^[0-9a-f]{16}\\.(png|jpg|gif|webp)$");
        static string PictureExt(string mime, byte[] b)
        {
            if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "png";
            if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8) return "jpg";
            if (b.Length > 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return "gif";
            if (b.Length > 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "webp";
            return null;   // not a picture we show (this also keeps SVG and anything executable out)
        }
        string StoreImage(Session s, byte[] bytes)
        {
            if (bytes == null || bytes.Length < 32 || bytes.Length > MaxImageBytes) return null; string ext = PictureExt(null, bytes); if (ext == null) return null;
            string name; using (var sha = System.Security.Cryptography.SHA1.Create()) name = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant().Substring(0, 16) + "." + ext;
            try
            {
                string folder = Path.Combine(dir, "images", Safe(s.Id)); Directory.CreateDirectory(folder); string path = Path.Combine(folder, name);
                if (!File.Exists(path)) { if (Directory.GetFiles(folder).Length >= 400) return null; File.WriteAllBytes(path, bytes); }
                return name;
            }
            catch (Exception) { return null; }
        }
        /// <summary>Finds picture blocks in a tool result (Claude's base64 image blocks, MCP image content, data URLs) and stores them next to the chat. Returns the stored file names.</summary>
        List<string> SaveImages(Session s, object content)
        {
            var found = new List<string>(); if (content == null || content is string) return found;
            var list = content as System.Collections.IEnumerable; if (list == null) return found;
            foreach (object x in list)
            {
                var d = x as Dictionary<string, object>; if (d == null) continue; string type = Str(d, "type"); string data = "";
                if (type == "image")
                {
                    var src = Obj(d, "source"); data = src != null ? Str(src, "data") : Str(d, "data");
                    if (data == "" && src != null && Str(src, "type") == "url") continue;
                }
                else if (type == "image_url" || type == "input_image" || type == "output_image") { object u = d.ContainsKey("image_url") ? d["image_url"] : d.ContainsKey("url") ? d["url"] : null; data = u is Dictionary<string, object> ? Str((Dictionary<string, object>)u, "url") : Convert.ToString(u); }
                else continue;
                int comma = data.IndexOf("base64,", StringComparison.Ordinal); if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0) data = data.Substring(comma + 7);
                if (data.Length < 40 || data.Length > MaxImageBytes * 4 / 3 + 16) continue;
                try { string n = StoreImage(s, Convert.FromBase64String(data)); if (n != null && !found.Contains(n)) found.Add(n); } catch (FormatException) { }
            }
            return found;
        }
        /// <summary>Codex items that are pictures by themselves (an image the agent viewed or generated): a base64 field or a file path inside the chat's folder.</summary>
        List<string> ImagesFromItem(Session s, Dictionary<string, object> item)
        {
            var found = new List<string>();
            foreach (string k in new[] { "result", "data", "image", "b64_json", "base64" })
            {
                string v = Str(item, k); if (v.Length > 200) { try { string n = StoreImage(s, Convert.FromBase64String(v.Substring(v.IndexOf("base64,", StringComparison.Ordinal) >= 0 ? v.IndexOf("base64,", StringComparison.Ordinal) + 7 : 0))); if (n != null) found.Add(n); } catch (FormatException) { } }
            }
            string path = Str(item, "path"); if (path == "") path = Str(item, "image_path"); if (path == "") path = Str(item, "saved_path");
            try
            {
                if (path != "" && File.Exists(path) && new FileInfo(path).Length <= MaxImageBytes)
                {
                    string full = Path.GetFullPath(path), cwd = Path.GetFullPath(s.Cwd ?? "."); string tmp = Path.GetFullPath(Path.GetTempPath());
                    string home = Path.GetFullPath(CodexHomeDir());
                    if (full.StartsWith(cwd, StringComparison.OrdinalIgnoreCase) || full.StartsWith(tmp, StringComparison.OrdinalIgnoreCase) || full.StartsWith(home, StringComparison.OrdinalIgnoreCase)) { string n = StoreImage(s, File.ReadAllBytes(full)); if (n != null) found.Add(n); }
                }
            }
            catch (Exception) { }
            return found;
        }
        internal string[] SaveImagesForTest(string id, object content) { Session s; lock (gate) s = Get(id); return SaveImages(s, content).ToArray(); }
        /// <summary>Returns one stored picture as base64 for the interface.</summary>
        public object ImageGet(string id, string file)
        {
            if (!ImageName.IsMatch(file ?? "")) throw new ArgumentException("That picture isn't available.");
            string path = Path.Combine(dir, "images", Safe(id), file); if (!File.Exists(path)) throw new ArgumentException("That picture isn't available any more.");
            string ext = Path.GetExtension(file).TrimStart('.'); string mime = ext == "jpg" ? "image/jpeg" : "image/" + ext;
            return new Dictionary<string, object> { { "Mime", mime }, { "Data", Convert.ToBase64String(File.ReadAllBytes(path)) } };
        }}
}
