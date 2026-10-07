using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LaicaSetup
{
    /// <summary>
    /// Self-contained installer for LAICA. The program files are embedded as payload.zip. It installs per user (no administrator rights needed),
    /// adds Start menu and desktop shortcuts and an uninstall entry. Chats, settings and usage data live next to the program and are never deleted by an upgrade or uninstall.
    /// Usage: LAICA-Setup.exe [/S] [/D=folder]   or   Uninstall.exe /uninstall [/S]
    /// </summary>
    static class Program
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LAICA";

        static bool plain;   // /NOINTEGRATION: no shortcuts and no uninstall entry (used by the setup test)
        static Stream Payload() { return Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"); }
        static string Version() { return Assembly.GetExecutingAssembly().GetName().Version.ToString(3); }

        [STAThread]
        static int Main(string[] args)
        {
            plain = args.Any(a => a.Equals("/NOINTEGRATION", StringComparison.OrdinalIgnoreCase)); bool silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase)), uninstall = args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LAICA");
            foreach (string a in args) if (a.StartsWith("/D=", StringComparison.OrdinalIgnoreCase)) dir = a.Substring(3).Trim('"');
            dir = Path.GetFullPath(dir);
            Application.EnableVisualStyles();
            try { return uninstall ? Remove(dir, silent) : Install(dir, silent); }
            catch (Exception ex) { if (!silent) MessageBox.Show(ex.Message, "LAICA setup", MessageBoxButtons.OK, MessageBoxIcon.Error); else Console.Error.WriteLine(ex.Message); return 1; }
        }

        static void CloseRunning(string dir, bool silent)
        {
            var running = Process.GetProcessesByName("LAICA").Where(p => { try { return p.MainModule.FileName.StartsWith(dir, StringComparison.OrdinalIgnoreCase); } catch (Exception) { return false; } }).ToList();
            if (running.Count == 0) return;
            if (!silent && MessageBox.Show("LAICA is running. Close it to continue?", "LAICA setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) throw new InvalidOperationException("Setup cancelled.");
            foreach (var p in running) { try { p.CloseMainWindow(); if (!p.WaitForExit(4000)) p.Kill(); p.WaitForExit(4000); } catch (Exception) { } }
        }

        static int Install(string dir, bool silent)
        {
            if (!silent && MessageBox.Show("Install LAICA " + Version() + " to:\n" + dir + "\n\nThis build is not code-signed, so Windows may warn you about it.", "LAICA setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return 2;
            CloseRunning(dir, silent);
            Directory.CreateDirectory(dir); string root = dir.TrimEnd('\\') + "\\";
            using (var zip = new ZipArchive(Payload(), ZipArchiveMode.Read))
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.EndsWith("/")) continue;
                    string target = Path.GetFullPath(Path.Combine(dir, e.FullName.Replace('/', '\\')));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The installer payload is corrupt.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var src = e.Open()) using (var dst = new FileStream(target, FileMode.Create, FileAccess.Write)) src.CopyTo(dst);
                }
            string uninstaller = Path.Combine(dir, "Uninstall.exe"); File.Copy(Assembly.GetExecutingAssembly().Location, uninstaller, true);
            string exe = Path.Combine(dir, "LAICA.exe");
            if (!plain) { Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "LAICA.lnk"), exe, dir);
            Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "LAICA.lnk"), exe, dir); }
            if (!plain) using (var k = Registry.CurrentUser.CreateSubKey(Key))
            {
                k.SetValue("DisplayName", "LAICA"); k.SetValue("DisplayVersion", Version()); k.SetValue("Publisher", "LAICA"); k.SetValue("InstallLocation", dir); k.SetValue("DisplayIcon", exe);
                k.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall"); k.SetValue("NoModify", 1); k.SetValue("NoRepair", 1);
            }
            if (!silent && MessageBox.Show("LAICA " + Version() + " is installed.\n\nOpen it now?", "LAICA setup", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir });
            return 0;
        }

        static int Remove(string dir, bool silent)
        {
            if (!silent && MessageBox.Show("Remove LAICA from this computer?\n\nYour chats, settings and usage history in " + dir + " are kept.", "LAICA setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return 2;
            CloseRunning(dir, silent); string root = dir.TrimEnd('\\') + "\\";
            using (var zip = new ZipArchive(Payload(), ZipArchiveMode.Read))
                foreach (var e in zip.Entries.Where(x => !x.FullName.EndsWith("/")))
                {
                    string target = Path.GetFullPath(Path.Combine(dir, e.FullName.Replace('/', '\\'))); if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    if (target.EndsWith("\\Uninstall.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    try { if (File.Exists(target)) File.Delete(target); } catch (Exception) { }
                }
            foreach (string sub in Directory.Exists(dir) ? Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(s => s.Length).ToArray() : new string[0]) { try { if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub); } catch (Exception) { } }
            if (!plain) foreach (string lnk in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "LAICA.lnk"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "LAICA.lnk") }) { try { File.Delete(lnk); } catch (Exception) { } }
            if (!plain) try { Registry.CurrentUser.DeleteSubKeyTree(Key, false); } catch (Exception) { }
            // the running uninstaller cannot delete itself: a short-lived command does it after this process exits
            try { Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & erase \"" + Path.Combine(dir, "Uninstall.exe") + "\" & rmdir \"" + dir + "\"") { CreateNoWindow = true, UseShellExecute = false }); } catch (Exception) { }
            if (!silent) MessageBox.Show("LAICA was removed.", "LAICA setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        static void Shortcut(string path, string target, string workDir)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell"); object shell = Activator.CreateInstance(t);
                object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path }); Type lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target }); lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "LAICA" }); lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            }
            catch (Exception) { }
        }
    }
}
