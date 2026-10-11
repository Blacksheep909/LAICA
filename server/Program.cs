using System;
using System.IO;
using System.Threading;
namespace Laica
{
    /// <summary>The LAICA engine without a window. It runs the backend and serves the interface and its commands over loopback HTTP,
    /// which is what the macOS and Linux apps (an Electron window) open. It stops when its input closes, so it never outlives the app.
    /// Usage: laica-server --ui &lt;folder&gt; --data &lt;folder&gt; [--port N] [--work &lt;folder&gt;]   (password in LAICA_APP_PASSWORD)</summary>
    public static class ServerProgram
    {
        static string Arg(string[] a, string name, string fallback) { for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1]; return fallback; }
        public static int Main(string[] args)
        {
            string ui = Arg(args, "--ui", null), data = Arg(args, "--data", null), work = Arg(args, "--work", null);
            int port; if (!Int32.TryParse(Arg(args, "--port", "8977"), out port)) port = 8977;
            string password = Environment.GetEnvironmentVariable("LAICA_APP_PASSWORD");
            if (ui == null || !File.Exists(Path.Combine(ui, "index.html"))) { Console.Error.WriteLine("ERROR --ui must be the interface folder (index.html not found)."); return 2; }
            if (String.IsNullOrEmpty(password) || password.Length < 8) { Console.Error.WriteLine("ERROR LAICA_APP_PASSWORD must be set (at least 8 characters)."); return 2; }
            if (String.IsNullOrEmpty(data)) data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LAICA");
            Directory.CreateDirectory(data);
            string home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (String.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            var backend = new WorkspaceBackend(data, home);
            if (!String.IsNullOrEmpty(work) && Directory.Exists(work)) backend.WorkingDirectory = work;
            backend.Harness.StartUpdateChecks();
            var remote = new RemoteServer(backend, data, ui);
            try { remote.Configure(true, port, false, password); }
            catch (Exception ex) { Console.Error.WriteLine("ERROR " + ex.Message); return 3; }
            Console.WriteLine("READY http://127.0.0.1:" + port + "/"); Console.Out.Flush();
            var done = new ManualResetEventSlim();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; done.Set(); };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => done.Set();
            new Thread(() => { try { while (Console.In.Read() >= 0) { } } catch (Exception) { } done.Set(); }) { IsBackground = true }.Start();   // the app closing its end ends the engine
            done.Wait();
            try { remote.Dispose(); backend.Dispose(); } catch (Exception) { }
            return 0;
        }
    }
}
