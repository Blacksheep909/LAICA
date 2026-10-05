using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace Laica {
    public sealed class TeamProfile {
        public string Id, Name;
        public string Mode = "multi";
        public bool SnapToGrid;
        public AgentPlan Plan;
        public override string ToString() { return Name; }
    }
    public sealed class ProfileLibrary {
        public int Version = 1;
        public string SelectedId;
        public List<TeamProfile> Profiles = new List<TeamProfile>();
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        public static ProfileLibrary Read(string path, Action<AgentPlan> validate) {
            if (!File.Exists(path)) return new ProfileLibrary();
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Profile library exceeds 4 MB.");
            var library = Json.Deserialize<ProfileLibrary>(File.ReadAllText(path));
            if (library == null || library.Version != 1 || library.Profiles == null || library.Profiles.Count > 50) throw new InvalidDataException("Unsupported profile library.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var profile in library.Profiles) {
                if (profile == null || String.IsNullOrWhiteSpace(profile.Id) || profile.Id.Length > 128 || !ids.Add(profile.Id)) throw new InvalidDataException("Profile IDs must be unique.");
                ValidateName(profile.Name);
                if (profile.Mode != "solo" && profile.Mode != "multi") throw new InvalidDataException("Unsupported profile mode.");
                if (validate != null) validate(profile.Plan);
            }
            if (library.SelectedId != null && !ids.Contains(library.SelectedId)) library.SelectedId = null;
            return library;
        }
        public static void ValidateName(string name) {
            if (String.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || name.Any(Char.IsControl)) throw new ArgumentException("Choose a profile name of 1–80 characters.");
        }
        public TeamProfile Selected { get { return Profiles.FirstOrDefault(p => p.Id == SelectedId); } }
        public TeamProfile Save(string name, AgentPlan plan, string mode, bool snap, bool create) {
            ValidateName(name);
            if (mode != "multi" && mode != "solo") throw new ArgumentException("Choose solo or multi mode.");
            if (plan == null) throw new ArgumentNullException("plan");
            var profile = create ? null : Selected;
            if (profile == null) {
                if (Profiles.Count >= 50) throw new InvalidOperationException("Up to 50 profiles are supported.");
                profile = new TeamProfile { Id = Guid.NewGuid().ToString("N") };
                Profiles.Add(profile);
            }
            profile.Name = name.Trim();
            profile.Mode = mode;
            profile.SnapToGrid = snap;
            profile.Plan = Copy(plan);
            SelectedId = profile.Id;
            return profile;
        }
        public static AgentPlan Copy(AgentPlan plan) { return Json.Deserialize<AgentPlan>(Json.Serialize(plan)); }
        public string Serialize() { return Json.Serialize(this); }
    }
}
