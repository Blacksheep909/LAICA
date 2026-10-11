using System;
using System.Diagnostics;
using System.IO;

/// <summary>The few things the checks run as a stand-in agent, written once for Windows, macOS and Linux.</summary>
public static class Portable
{
    public static bool Unix
    {
        get { return Path.DirectorySeparatorChar == '/'; }
    }

    /// <summary>A program that prints what it is given (the stand-in for an agent that streams text back).</summary>
    public static string Echo
    {
        get { return Unix ? "cat" : "cmd.exe"; }
    }

    public static string EchoArgs
    {
        get { return Unix ? "" : "/c more"; }
    }

    /// <summary>A program that does nothing for a while (the stand-in for a long-running agent).</summary>
    public static string Sleeper
    {
        get { return Unix ? "sleep" : "cmd.exe"; }
    }

    public static string SleepArgs(int seconds)
    {
        return Unix ? seconds.ToString() : "/c ping -n " + seconds + " 127.0.0.1 >nul";
    }

    /// <summary>
    /// A program that starts this checks program again with the given arguments (the stand-in for an agent that speaks a real protocol).
    /// On Windows that is the .exe itself; elsewhere a small script that starts it with mono or dotnet.
    /// </summary>
    public static string SelfExe
    {
        get
        {
            string self = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!Unix)
                return self;
            string script = self + ".run.sh";
            bool mono = Type.GetType("Mono.Runtime") != null;
            string text = "#!/bin/sh\nexec " + (mono ? "mono" : "dotnet") + " \"" + self + "\" \"$@\"\n";
            if (!File.Exists(script) || File.ReadAllText(script) != text)
            {
                File.WriteAllText(script, text);
                using (var chmod = Process.Start("chmod", "755 \"" + script + "\""))
                    chmod.WaitForExit();
            }
            return script;
        }
    }

    /// <summary>Writes a tiny program called <paramref name="name"/> into <paramref name="dir"/>: a .cmd on Windows, an executable script elsewhere.</summary>
    public static void WriteShim(string dir, string name, string windowsBody, string unixBody)
    {
        if (!Unix)
        {
            File.WriteAllText(Path.Combine(dir, name + ".cmd"), windowsBody);
            return;
        }
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + unixBody + "\n");
        using (var chmod = Process.Start("chmod", "755 \"" + path + "\""))
            chmod.WaitForExit();
    }
}
