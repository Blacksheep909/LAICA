using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly:System.Reflection.AssemblyFileVersion("0.7.0.0")]
[assembly:System.Reflection.AssemblyProduct("LAICA")]
namespace Laica.GlassWorkspace {
    public sealed class GlassHost : Form {
        const string Origin = "https://laica.local";
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        readonly WorkspaceBackend backend;
        readonly WebView2 web = new WebView2();
        readonly string appDir, assets, initialPage;
        readonly SemaphoreSlim messages = new SemaphoreSlim(1, 1);
        readonly SemaphoreSlim observations = new SemaphoreSlim(1, 1);
        NotifyIcon tray;
        bool exiting, ready, closingRequested;
        string pendingCloseId;
        TaskCompletionSource<string> pendingClose;
        [StructLayout(LayoutKind.Sequential)] struct Margins { public int Left,Right,Top,Bottom; }
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attr,ref int value,int size);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd,ref Margins margins);
        public GlassHost(string dataDir,string assetDir,string page,bool preview=false) {
            appDir=dataDir;assets=assetDir;initialPage=page;
            string home=Environment.GetEnvironmentVariable("CODEX_HOME");if(String.IsNullOrWhiteSpace(home))home=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
            backend=new WorkspaceBackend(appDir,home);
            Text=preview?"LAICA · Desktop preview":"LAICA — Light Weight AI Container for Agents";StartPosition=FormStartPosition.Manual;var workspace=Screen.FromPoint(Cursor.Position).WorkingArea;ClientSize=new Size(Math.Max(900,Math.Min(1420,workspace.Width-80)),Math.Max(650,Math.Min(870,workspace.Height-100)));MinimumSize=new Size(900,650);Location=new Point(workspace.Left+(workspace.Width-Width)/2,workspace.Top+(workspace.Height-Height)/2);BackColor=Color.FromArgb(15,20,29);AutoScaleMode=AutoScaleMode.Dpi;
            string icon=Path.Combine(assets,"LAICA.ico");if(File.Exists(icon))Icon=new Icon(icon);
            web.Dock=DockStyle.Fill;web.DefaultBackgroundColor=Color.Transparent;Controls.Add(web);
            var menu=new ContextMenuStrip();menu.Items.Add("Open LAICA",null,(s,e)=>Restore());menu.Items.Add("Teams",null,(s,e)=>Navigate("teams"));menu.Items.Add("Activity",null,(s,e)=>Navigate("activity"));menu.Items.Add(new ToolStripSeparator());menu.Items.Add("Exit",null,async(s,e)=>await RequestClose(true));
            tray=new NotifyIcon{Icon=Icon,Text="LAICA · Agent workspace",Visible=true,ContextMenuStrip=menu};tray.DoubleClick+=(s,e)=>Restore();
            backend.Changed+=state=>Post(new {Type="state",State=state});
            Shown+=async(s,e)=>{if(preview){Hide();Show();}await Initialize();};
            FormClosing+=async(s,e)=>{if(e.CloseReason==CloseReason.UserClosing&&!exiting){e.Cancel=true;await RequestClose(false);}};
            FormClosed+=(s,e)=>{backend.Dispose();tray.Visible=false;tray.Dispose();};
        }
        protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);ApplyBackdrop();}
        void ApplyBackdrop(){try{int dark=1,round=2,material=SystemInformation.HighContrast?1:3;DwmSetWindowAttribute(Handle,20,ref dark,4);DwmSetWindowAttribute(Handle,33,ref round,4);DwmSetWindowAttribute(Handle,38,ref material,4);var margins=material==3?new Margins{Left=-1,Right=-1,Top=-1,Bottom=-1}:new Margins();DwmExtendFrameIntoClientArea(Handle,ref margins);}catch(DllNotFoundException){}catch(EntryPointNotFoundException){}}
        protected override void OnPaintBackground(PaintEventArgs e){if(SystemInformation.HighContrast){base.OnPaintBackground(e);return;}var saved=e.Graphics.Save();e.Graphics.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy;using(var brush=new SolidBrush(Color.FromArgb(0,0,0,0)))e.Graphics.FillRectangle(brush,ClientRectangle);e.Graphics.Restore(saved);}
        protected override void WndProc(ref Message m){if(m.Msg==0x8001){Restore();Navigate(m.WParam==new IntPtr(1)?"mode":m.WParam==new IntPtr(3)?"services":"teams");return;}base.WndProc(ref m);if(m.Msg==0x031E||m.Msg==0x031A||m.Msg==0x001A)ApplyBackdrop();}
        public void Restore(){Show();if(WindowState==FormWindowState.Minimized)WindowState=FormWindowState.Normal;Activate();}
        async Task RequestClose(bool exit){if(closingRequested)return;if(!ready){if(exit){exiting=true;Close();}else Hide();return;}closingRequested=true;pendingCloseId=Guid.NewGuid().ToString("N");pendingClose=new TaskCompletionSource<string>();try{Post(new{Type="flush",Id=pendingCloseId});var completed=await Task.WhenAny(pendingClose.Task,Task.Delay(10000));string error=completed==pendingClose.Task?await pendingClose.Task:"LAICA is still saving your draft. Wait for the current operation, then close it again.";if(!String.IsNullOrEmpty(error)){Restore();Post(new{Type="closeError",Error=error});return;}if(exit){exiting=true;Close();}else Hide();}finally{closingRequested=false;pendingClose=null;pendingCloseId=null;}}
        void Navigate(string page){Restore();Post(new {Type="navigate",Page=page});}
        async Task Initialize(){try{
            if(!Directory.Exists(assets)||!File.Exists(Path.Combine(assets,"index.html")))throw new FileNotFoundException("LAICA's interface files are missing. Reinstall the application.");
            string cache=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LAICA","WebView2");
            var environment=await CoreWebView2Environment.CreateAsync(null,cache);
            await web.EnsureCoreWebView2Async(environment);
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;web.CoreWebView2.Settings.AreDevToolsEnabled=Environment.GetCommandLineArgs().Contains("--preview");web.CoreWebView2.Settings.IsStatusBarEnabled=false;
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("laica.local",assets,CoreWebView2HostResourceAccessKind.DenyCors);
            web.CoreWebView2.NavigationStarting+=(s,e)=>{if(!e.Uri.StartsWith(Origin+"/",StringComparison.OrdinalIgnoreCase))e.Cancel=true;};
            web.CoreWebView2.NewWindowRequested+=(s,e)=>{e.Handled=true;OpenLink(e.Uri);};
            web.CoreWebView2.PermissionRequested+=(s,e)=>e.State=CoreWebView2PermissionState.Deny;
            web.CoreWebView2.WebMessageReceived+=Receive;
            web.CoreWebView2.NavigationCompleted+=(s,e)=>{ready=e.IsSuccess;if(ready){Post(new{Type="state",State=backend.State()});Post(new{Type="navigate",Page=initialPage});}};
            web.CoreWebView2.Navigate(Origin+"/index.html");
        }catch(Exception ex){web.Visible=false;var text=new Label{Dock=DockStyle.Fill,Text="LAICA couldn't open its interface.\n\n"+ex.Message+"\n\nInstall Microsoft Edge WebView2 Runtime, then reopen LAICA.",Padding=new Padding(40),ForeColor=Color.White,BackColor=BackColor,Font=new Font("Segoe UI",12)};Controls.Add(text);text.BringToFront();}}
        async void Receive(object sender,CoreWebView2WebMessageReceivedEventArgs e){
            if(!e.Source.StartsWith(Origin+"/",StringComparison.OrdinalIgnoreCase))return;
            string id=null;SemaphoreSlim commandGate=null;bool entered=false;
            try{if(e.WebMessageAsJson.Length>4*1024*1024)throw new ArgumentException("Request is too large.");var request=json.Deserialize<Dictionary<string,object>>(e.WebMessageAsJson);id=Convert.ToString(request["Id"]);string method=Convert.ToString(request["Method"]);object raw;var payload=request.TryGetValue("Payload",out raw)?raw as Dictionary<string,object>:null;payload=payload??new Dictionary<string,object>();if(method=="closeReady"){if(pendingClose!=null&&Convert.ToString(payload["Id"])==pendingCloseId){object error;pendingClose.TrySetResult(payload.TryGetValue("Error",out error)?Convert.ToString(error):null);}Post(new{Type="response",Id=id,Result=true});return;}bool observation=method=="sessions"||method=="activity"||method=="teamOverview";commandGate=observation?observations:method=="stop"||method=="state"?null:messages;if(commandGate!=null){await commandGate.WaitAsync();entered=true;}
                object response;
                if(observation)response=await Task.Run(()=>backend.HandleAsync(method,payload));
                else if(method=="chooseFolder"){using(var dialog=new FolderBrowserDialog{Description="Choose the folder LAICA may inspect",SelectedPath=backend.WorkingDirectory}){if(dialog.ShowDialog(this)!=DialogResult.OK)response=null;else response=await backend.HandleAsync("chooseWorkingDirectory",new Dictionary<string,object>{{"Path",dialog.SelectedPath}});}}
                else if(method=="importFile"){using(var dialog=new OpenFileDialog{Filter="LAICA team (*.json)|*.json"}){if(dialog.ShowDialog(this)!=DialogResult.OK)response=null;else{if(new FileInfo(dialog.FileName).Length>4*1024*1024)throw new ArgumentException("Plan exceeds 4 MB.");response=await backend.HandleAsync("importPlan",new Dictionary<string,object>{{"Plan",json.DeserializeObject(File.ReadAllText(dialog.FileName))}});}}}
                else if(method=="exportFile"){object plan=await backend.HandleAsync("exportPlan",payload);using(var dialog=new SaveFileDialog{Filter="LAICA team (*.json)|*.json",FileName="my-team.json"}){if(dialog.ShowDialog(this)!=DialogResult.OK)response=null;else{File.WriteAllText(dialog.FileName,json.Serialize(plan));response=new{Message="Team exported."};}}}
                else if(method=="saveResult"){using(var dialog=new SaveFileDialog{Filter="Text file (*.txt)|*.txt",FileName="laica-result.txt"}){if(dialog.ShowDialog(this)!=DialogResult.OK)response=null;else{var state=json.Deserialize<Dictionary<string,object>>(json.Serialize(backend.State()));File.WriteAllText(dialog.FileName,Convert.ToString(state["Answer"]));response=new{Message="Answer saved."};}}}
                else if(method=="openLink"){OpenLink(Convert.ToString(payload["Url"]));response=null;}
                else if(method=="desktopMode"){string config=Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex"),"config.toml");string text=File.Exists(config)?File.ReadAllText(config):"";var agents=System.Text.RegularExpressions.Regex.Match(text,@"(?ms)^\[agents\][ \t]*\r?\n(?<body>.*?)(?=^\[|\z)");var enabled=System.Text.RegularExpressions.Regex.Match(agents.Groups["body"].Value,@"(?m)^enabled\s*=\s*(true|false)");response=new{Mode=enabled.Success?(enabled.Groups[1].Value=="true"?"multi":"solo"):"unknown"};}
                else if(method=="restartCodex")throw new InvalidOperationException("Configure delegation in Codex settings outside LAICA, restart Codex yourself, then start a fresh chat.");
                else response=await backend.HandleAsync(method,payload);
                Post(new{Type="response",Id=id,Result=response});
            }catch(Exception ex){Post(new{Type="response",Id=id,Error=ex.Message});}finally{if(entered)commandGate.Release();}
        }
        void OpenLink(string value){Uri uri;if(!Uri.TryCreate(value,UriKind.Absolute,out uri)||uri.Scheme!="https"||!(uri.Host=="github.com"||uri.Host=="learn.microsoft.com"||uri.Host=="ollama.com"||uri.Host=="lmstudio.ai"||uri.Host=="moelueker.com"))return;Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});}
        void Post(object message){if(IsDisposed||!IsHandleCreated)return;if(InvokeRequired){try{BeginInvoke(new Action(()=>Post(message)));}catch(InvalidOperationException){}return;}if(ready&&web.CoreWebView2!=null)web.CoreWebView2.PostWebMessageAsJson(json.Serialize(message));}
    }
    public static class Program {
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string cls,string name);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h,int message,IntPtr w,IntPtr l);
        [STAThread] public static void Main(string[] args){string exeDir=Path.GetDirectoryName(Application.ExecutablePath);string data=Option(args,"--data-dir")??Laica.DataPaths.DataDirectory();string assets=Option(args,"--assets")??Path.Combine(exeDir,"ui");bool preview=args.Contains("--preview");const string title="LAICA — Light Weight AI Container for Agents";using(var mutex=new Mutex(false,"Local\\LAICA-"+(preview?"preview":"workspace")+"-"+Environment.UserName)){bool owns=false;try{owns=mutex.WaitOne(0);}catch(AbandonedMutexException){owns=true;}if(!owns){var hwnd=FindWindow(null,preview?"LAICA · Desktop preview":title);if(hwnd!=IntPtr.Zero)PostMessage(hwnd,0x8001,args.Contains("--mode")?new IntPtr(1):args.Contains("--codex-mode")?new IntPtr(3):IntPtr.Zero,IntPtr.Zero);return;}try{Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new GlassHost(data,assets,args.Contains("--mode")?"mode":args.Contains("--codex-mode")?"services":"teams",preview));}catch(Exception ex){MessageBox.Show(ex.Message,"LAICA",MessageBoxButtons.OK,MessageBoxIcon.Error);}finally{mutex.ReleaseMutex();}}}
        static string Option(string[] args,string key){int index=Array.IndexOf(args,key);return index>=0&&index+1<args.Length?Path.GetFullPath(args[index+1]):null;}
    }
}
