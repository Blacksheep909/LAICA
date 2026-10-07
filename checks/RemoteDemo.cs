using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Laica;

/// <summary>Developer tool: runs the real backend with throwaway data and serves build\ui over the remote-access server so the interface can be viewed in any browser.
/// Usage: RemoteDemo.exe &lt;ui-folder&gt; &lt;port&gt; &lt;password&gt; [work-folder]</summary>
public static class RemoteDemo
{
    public static int Main(string[] args)
    {
        string ui = args[0]; int port = Int32.Parse(args[1]); string password = args[2]; string work = args.Length > 3 ? args[3] : Environment.CurrentDirectory;
        string data = Path.Combine(Path.GetTempPath(), "laica-demo-" + port); Directory.CreateDirectory(data);
        var backend = new WorkspaceBackend(data, Path.Combine(data, "codex")); backend.WorkingDirectory = work;
        backend.Harness.SaveAgent(new Dictionary<string, object> { { "Name", "Echo (demo)" }, { "Command", "cmd.exe" }, { "Args", "/c more" } });
        backend.Harness.SaveAgent(new Dictionary<string, object> { { "Name", "Slow (demo)" }, { "Command", "cmd.exe" }, { "Args", "/c ping -n 60 127.0.0.1 >nul" } });
        var remote = new RemoteServer(backend, data, ui); remote.Configure(true, port, false, password);
        Console.WriteLine("READY http://127.0.0.1:" + port + "/"); Thread.Sleep(Timeout.Infinite); return 0;
    }
}
