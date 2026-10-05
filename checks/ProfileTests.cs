using System;
using System.IO;
using System.Linq;
using Laica;
class ProfileTests {
    static int checks;
    static void Must(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    static void Reject(Action action) { bool rejected = false; try { action(); } catch { rejected = true; } Must(rejected, "Invalid profile accepted"); }
    public static void Main() {
        string dir = Path.Combine(Path.GetTempPath(), "laica-profile-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try {
            string path = Path.Combine(dir, "profiles.json"); var library = ProfileLibrary.Read(path, null); Must(library.Profiles.Count == 0, "Missing store should create empty library");
            var plan = GraphPlanHelpers.CreateStarter(); plan.Nodes[0].ConnectionId = "local-service"; plan.Nodes[0].Model = "model:tag"; plan.Nodes[0].Effort = "default"; plan.HiddenStageLinks.Add(new StageLink { FromId = "$input", ToId = "root" });
            var first = library.Save("Local team", plan, "multi", true, true); plan.Nodes[0].Model = "changed"; Must(first.Plan.Nodes[0].Model == "model:tag", "Profile aliases current plan");
            File.WriteAllText(path, library.Serialize()); var read = ProfileLibrary.Read(path, p => { if (p == null || p.Nodes.Count != 4) throw new Exception(); });
            Must(read.Selected.Name == "Local team" && read.Selected.SnapToGrid, "Selected profile not restored"); Must(read.Selected.Plan.HiddenStageLinks.Count == 1 && read.Selected.Plan.Nodes[0].ConnectionId == "local-service", "Layout/service data lost");
            var serializer=new System.Web.Script.Serialization.JavaScriptSerializer(); Must(serializer.Serialize(read.Selected.Plan)==serializer.Serialize(first.Plan),"Full graph snapshot changed after reopening profile store");
            read.Save("Renamed", plan, "solo", false, false); Must(read.Profiles.Count == 1 && read.Selected.Mode == "solo" && read.Selected.Name == "Renamed", "Save existing duplicates a profile");
            read.Save("Second", plan, "multi", false, true); Must(read.Profiles.Count == 2 && read.Profiles.Select(p => p.Id).Distinct().Count() == 2, "Save as fails to give fresh identity");
            Must(!read.Serialize().Contains("ProtectedKey") && !read.Serialize().Contains("BaseUrl"), "Profile leaks connection secrets/settings");
            Reject(() => read.Save("\n", plan, "solo", false, true)); Reject(() => read.Save(new String('a', 81), plan, "solo", false, true)); Reject(() => read.Save("x", plan, "invalid", false, true));
            File.WriteAllText(path, "{\"Version\":2,\"Profiles\":[]}"); Reject(() => ProfileLibrary.Read(path, null));
            File.WriteAllText(path, "{bad"); Reject(() => ProfileLibrary.Read(path, null));
            Console.WriteLine("ProfileTests PASS " + checks);
        } catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
        finally { foreach (string file in Directory.GetFiles(dir)) File.Delete(file); Directory.Delete(dir); }
    }
}
