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

[assembly:System.Reflection.AssemblyProduct("LAICA")]
namespace Laica.GlassWorkspace {
    public sealed class GlassHost : Form {
        const string Origin = "https://laica.local";
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        readonly WorkspaceBackend backend; RemoteServer remote;
        readonly WebView2 web = new WebView2();
        HostDictation dictation;
        readonly string appDir, assets, initialPage;
        readonly SemaphoreSlim messages = new SemaphoreSlim(1, 1);
        readonly SemaphoreSlim observations = new SemaphoreSlim(1, 1);
        NotifyIcon tray;
        bool exiting, ready, closingRequested;
        string pendingCloseId;
        TaskCompletionSource<string> pendingClose;
        [StructLayout(LayoutKind.Sequential)] struct Margins { public int Left,Right,Top,Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct NcRect { public int Left,Top,Right,Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct NcCalcSizeParams { public NcRect Rect0,Rect1,Rect2; public IntPtr Pos; }
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd,int msg,IntPtr w,IntPtr l);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd,out NcRect rect);
        bool fullscreen, wasMaximized; Rectangle restoreBounds; const int ResizeBand=6;
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attr,ref int value,int size);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd,ref Margins margins);
        public GlassHost(string dataDir,string assetDir,string page,bool preview=false) {
            appDir=dataDir;assets=assetDir;initialPage=page;
            string home=Environment.GetEnvironmentVariable("CODEX_HOME");if(String.IsNullOrWhiteSpace(home))home=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
            backend=new WorkspaceBackend(appDir,home);if(!preview)backend.Harness.StartUpdateChecks();remote=new RemoteServer(backend,appDir,assets);
            Text=preview?"LAICA · Desktop preview":"LAICA · Lightweight AI Harness";StartPosition=FormStartPosition.Manual;var workspace=Screen.FromPoint(Cursor.Position).WorkingArea;ClientSize=new Size(Math.Max(900,Math.Min(1420,workspace.Width-80)),Math.Max(650,Math.Min(870,workspace.Height-100)));MinimumSize=new Size(900,650);Location=new Point(workspace.Left+(workspace.Width-Width)/2,workspace.Top+(workspace.Height-Height)/2);BackColor=Color.FromArgb(3,2,8);AutoScaleMode=AutoScaleMode.Dpi;
            string icon=Path.Combine(assets,"LAICA.ico");if(File.Exists(icon))Icon=new Icon(icon);
            dictation=new HostDictation((text,final)=>{try{BeginInvoke(new Action(()=>Post(new{Type="dictation",Text=text,Final=final})));}catch(Exception){}});web.Dock=DockStyle.Fill;web.DefaultBackgroundColor=Color.Transparent;Controls.Add(web);
            var menu=new ContextMenuStrip();menu.Items.Add("Open LAICA",null,(s,e)=>Restore());menu.Items.Add("Teams",null,(s,e)=>Navigate("teams"));menu.Items.Add("Activity",null,(s,e)=>Navigate("activity"));menu.Items.Add(new ToolStripSeparator());menu.Items.Add("Exit",null,async(s,e)=>await RequestClose(true));
            tray=new NotifyIcon{Icon=Icon,Text="LAICA · Agent workspace",Visible=true,ContextMenuStrip=menu};tray.DoubleClick+=(s,e)=>Restore();
            backend.Changed+=state=>Post(new {Type="state",State=state});backend.Harness.Event+=e=>Post(new {Type="harness",Event=e});backend.Harness.Changed+=()=>Post(new {Type="harnessSessions"});backend.Channels.Changed+=()=>Post(new {Type="channels"});
            Shown+=async(s,e)=>{if(preview){Hide();Show();}await Initialize();};
            FormClosing+=async(s,e)=>{if(e.CloseReason==CloseReason.UserClosing&&!exiting){e.Cancel=true;await RequestClose(false);}};
            FormClosed+=(s,e)=>{remote.Dispose();backend.Dispose();tray.Visible=false;tray.Dispose();};
        }
        protected override void OnHandleCreated(EventArgs e){base.OnHandleCreated(e);ApplyBackdrop();UpdateFrame();}
        void UpdateFrame(){int pad=WindowState==FormWindowState.Normal&&!fullscreen&&!SystemInformation.HighContrast?Math.Max(ResizeBand-1,(ResizeBand-1)*DeviceDpi/96):0;Padding=new Padding(pad);}
        protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);UpdateFrame();if(ready)Post(new{Type="window",Maximized=WindowState==FormWindowState.Maximized,Fullscreen=fullscreen});}
        void SetZoom(double factor){try{web.ZoomFactor=Math.Max(0.5,Math.Min(2.5,factor));}catch(Exception){}}
        void ToggleFullscreen(){if(!fullscreen){wasMaximized=WindowState==FormWindowState.Maximized;restoreBounds=WindowState==FormWindowState.Normal?Bounds:RestoreBounds;if(wasMaximized)WindowState=FormWindowState.Normal;fullscreen=true;UpdateFrame();Bounds=Screen.FromControl(this).Bounds;}else{fullscreen=false;UpdateFrame();if(wasMaximized)WindowState=FormWindowState.Maximized;else Bounds=restoreBounds;}Post(new{Type="window",Maximized=WindowState==FormWindowState.Maximized,Fullscreen=fullscreen});}
        void WindowControl(string action){switch(action){case "minimize":WindowState=FormWindowState.Minimized;break;case "maximize":if(fullscreen){ToggleFullscreen();break;}WindowState=WindowState==FormWindowState.Maximized?FormWindowState.Normal:FormWindowState.Maximized;break;case "fullscreen":ToggleFullscreen();break;case "close":var ignore=RequestClose(false);break;case "exit":exiting=true;Close();break;case "zoomIn":SetZoom(web.ZoomFactor+0.1);break;case "zoomOut":SetZoom(web.ZoomFactor-0.1);break;case "zoomReset":SetZoom(1.0);break;case "drag":if(WindowState==FormWindowState.Normal&&!fullscreen){ReleaseCapture();SendMessage(Handle,0x00A1,(IntPtr)2,IntPtr.Zero);}break;}}
        void ApplyBackdrop(){try{int dark=1,round=2,material=1;DwmSetWindowAttribute(Handle,20,ref dark,4);DwmSetWindowAttribute(Handle,33,ref round,4);int noBorder=unchecked((int)0xFFFFFFFE);DwmSetWindowAttribute(Handle,34,ref noBorder,4);DwmSetWindowAttribute(Handle,38,ref material,4);var margins=material==3?new Margins{Left=-1,Right=-1,Top=-1,Bottom=-1}:new Margins();DwmExtendFrameIntoClientArea(Handle,ref margins);}catch(DllNotFoundException){}catch(EntryPointNotFoundException){}}
        protected override void OnPaintBackground(PaintEventArgs e){if(SystemInformation.HighContrast){base.OnPaintBackground(e);return;}var saved=e.Graphics.Save();e.Graphics.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy;using(var brush=new SolidBrush(Color.FromArgb(0,0,0,0)))e.Graphics.FillRectangle(brush,ClientRectangle);e.Graphics.Restore(saved);}
        protected override void WndProc(ref Message m){
            if(m.Msg==0x8001){Restore();Navigate(m.WParam==new IntPtr(1)?"teams":m.WParam==new IntPtr(3)?"services":"home");return;}
            // LAICA draws its own title strip: the client area covers the whole window, so there is no native banner.
            if(m.Msg==0x0083&&m.WParam!=IntPtr.Zero&&!SystemInformation.HighContrast){
                if(WindowState==FormWindowState.Maximized&&!fullscreen){var p=(NcCalcSizeParams)Marshal.PtrToStructure(m.LParam,typeof(NcCalcSizeParams));int f=GetSystemMetrics(32)+GetSystemMetrics(92);p.Rect0.Left+=f;p.Rect0.Top+=f;p.Rect0.Right-=f;p.Rect0.Bottom-=f;Marshal.StructureToPtr(p,m.LParam,false);}
                m.Result=IntPtr.Zero;return;}
            if(m.Msg==0x0084&&WindowState==FormWindowState.Normal&&!fullscreen&&!SystemInformation.HighContrast){
                int x=(short)((long)m.LParam&0xFFFF),y=(short)(((long)m.LParam>>16)&0xFFFF);NcRect r;GetWindowRect(Handle,out r);int b=Math.Max(ResizeBand,ResizeBand*DeviceDpi/96);
                bool l=x<r.Left+b,rt=x>=r.Right-b,tp=y<r.Top+b,bt=y>=r.Bottom-b;
                int hit=tp&&l?13:tp&&rt?14:bt&&l?16:bt&&rt?17:l?10:rt?11:tp?12:bt?15:0;
                if(hit!=0){m.Result=(IntPtr)hit;return;}}
            base.WndProc(ref m);if(m.Msg==0x031E||m.Msg==0x031A||m.Msg==0x001A)ApplyBackdrop();}
        public void Restore(){Show();if(WindowState==FormWindowState.Minimized)WindowState=FormWindowState.Normal;Activate();}
        async Task RequestClose(bool exit){if(closingRequested)return;if(!ready){if(exit){exiting=true;Close();}else Hide();return;}closingRequested=true;pendingCloseId=Guid.NewGuid().ToString("N");pendingClose=new TaskCompletionSource<string>();try{Post(new{Type="flush",Id=pendingCloseId});var completed=await Task.WhenAny(pendingClose.Task,Task.Delay(10000));string error=completed==pendingClose.Task?await pendingClose.Task:"LAICA is still saving your draft. Wait for the current operation, then close it again.";if(!String.IsNullOrEmpty(error)){Restore();Post(new{Type="closeError",Error=error});return;}if(exit){exiting=true;Close();}else Hide();}finally{closingRequested=false;pendingClose=null;pendingCloseId=null;}}
        void Navigate(string page){Restore();Post(new {Type="navigate",Page=page});}
        async Task Initialize(){try{
            if(!Directory.Exists(assets)||!File.Exists(Path.Combine(assets,"index.html")))throw new FileNotFoundException("LAICA's interface files are missing. Reinstall the application.");
            string cache=Environment.GetEnvironmentVariable("LAICA_WEBVIEW_CACHE");if(String.IsNullOrWhiteSpace(cache))cache=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LAICA","WebView2");
            var environment=await CoreWebView2Environment.CreateAsync(null,cache);
            await web.EnsureCoreWebView2Async(environment);
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;web.CoreWebView2.Settings.AreDevToolsEnabled=Environment.GetCommandLineArgs().Contains("--preview");web.CoreWebView2.Settings.IsStatusBarEnabled=false;
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("laica.local",assets,CoreWebView2HostResourceAccessKind.DenyCors);
            web.CoreWebView2.NavigationStarting+=(s,e)=>{if(!e.Uri.StartsWith(Origin+"/",StringComparison.OrdinalIgnoreCase))e.Cancel=true;};
            web.CoreWebView2.NewWindowRequested+=(s,e)=>{e.Handled=true;OpenLink(e.Uri);};
            web.CoreWebView2.PermissionRequested+=(s,e)=>e.State=e.PermissionKind==CoreWebView2PermissionKind.Microphone&&e.Uri!=null&&e.Uri.StartsWith(Origin+"/",StringComparison.OrdinalIgnoreCase)?CoreWebView2PermissionState.Allow:CoreWebView2PermissionState.Deny;
            web.CoreWebView2.WebMessageReceived+=Receive;
            web.CoreWebView2.NavigationCompleted+=(s,e)=>{ready=e.IsSuccess;if(ready){Post(new{Type="state",State=backend.State()});Post(new{Type="navigate",Page=initialPage});}};
            web.CoreWebView2.Navigate(Origin+"/index.html");
        }catch(Exception ex){web.Visible=false;var text=new Label{Dock=DockStyle.Fill,Text="LAICA couldn't open its interface.\n\n"+ex.Message+"\n\nInstall Microsoft Edge WebView2 Runtime, then reopen LAICA.",Padding=new Padding(40),ForeColor=Color.White,BackColor=BackColor,Font=new Font("Segoe UI",12)};Controls.Add(text);text.BringToFront();}}
        async void Receive(object sender,CoreWebView2WebMessageReceivedEventArgs e){
            if(!e.Source.StartsWith(Origin+"/",StringComparison.OrdinalIgnoreCase))return;
            List<string> dropped=null;try{if(e.AdditionalObjects!=null&&e.AdditionalObjects.Count>0){dropped=new List<string>();foreach(object o in e.AdditionalObjects){var f=o as CoreWebView2File;if(f!=null&&!String.IsNullOrEmpty(f.Path))dropped.Add(f.Path);}}}catch(Exception){}
            string id=null;SemaphoreSlim commandGate=null;bool entered=false;
            try{if(e.WebMessageAsJson.Length>4*1024*1024)throw new ArgumentException("Request is too large.");var request=json.Deserialize<Dictionary<string,object>>(e.WebMessageAsJson);id=Convert.ToString(request["Id"]);string method=Convert.ToString(request["Method"]);object raw;var payload=request.TryGetValue("Payload",out raw)?raw as Dictionary<string,object>:null;payload=payload??new Dictionary<string,object>();if(method=="closeReady"){if(pendingClose!=null&&Convert.ToString(payload["Id"])==pendingCloseId){object error;pendingClose.TrySetResult(payload.TryGetValue("Error",out error)?Convert.ToString(error):null);}Post(new{Type="response",Id=id,Result=true});return;}bool observation=method=="sessions"||method=="activity"||method=="teamOverview"||method=="historyList";commandGate=observation?observations:method=="stop"||method=="state"||method=="dictateStart"||method=="dictateStop"?null:messages;if(commandGate!=null){await commandGate.WaitAsync();entered=true;}
                object response;
                if(observation)response=await Task.Run(()=>backend.HandleAsync(method,payload));
                else if(method=="chooseFolder"){using(var dialog=new FolderBrowserDialog{Description="Choose the folder LAICA may inspect",SelectedPath=backend.WorkingDirectory}){if(dialog.ShowDialog(this)!=DialogResult.OK)response=null;else response=await backend.HandleAsync("chooseWorkingDirectory",new Dictionary<string,object>{{"Path",dialog.SelectedPath}});}}
                else if(method=="importFile"){using(var dialog=new OpenFileDialog{Filter="LAICA team (*.json)|*.json"}){if(dialog.ShowDialog(this)!=DialogResult.OK)response=null;else{if(new FileInfo(dialog.FileName).Length>4*1024*1024)throw new ArgumentException("Plan exceeds 4 MB.");response=await backend.HandleAsync("importPlan",new Dictionary<string,object>{{"Plan",json.DeserializeObject(File.ReadAllText(dialog.FileName))}});}}}
                else if(method=="exportFile"){object plan=await backend.HandleAsync("exportPlan",payload);using(var dialog=new SaveFileDialog{Filter="LAICA team (*.json)|*.json",FileName="my-team.json"}){if(dialog.ShowDialog(this)!=DialogResult.OK)response=null;else{File.WriteAllText(dialog.FileName,json.Serialize(plan));response=new{Message="Team exported."};}}}
                else if(method=="saveResult"){using(var dialog=new SaveFileDialog{Filter="Text file (*.txt)|*.txt",FileName="laica-result.txt"}){if(dialog.ShowDialog(this)!=DialogResult.OK)response=null;else{var state=json.Deserialize<Dictionary<string,object>>(json.Serialize(backend.State()));File.WriteAllText(dialog.FileName,Convert.ToString(state["Answer"]));response=new{Message="Answer saved."};}}}
                else if(method=="windowControl"){WindowControl(Convert.ToString(payload["Action"]));response=new{Maximized=WindowState==FormWindowState.Maximized,Fullscreen=fullscreen};}
                else if(method=="revealPath"){string rp=Convert.ToString(payload["Path"]);if(Directory.Exists(rp))Process.Start("explorer.exe","\""+rp+"\"");else if(File.Exists(rp))Process.Start("explorer.exe","/select,\""+rp+"\"");response=true;}
                else if(method=="pickFiles"){using(var dialog=new OpenFileDialog{Multiselect=true,Title="Attach files",CheckFileExists=true}){response=dialog.ShowDialog(this)==DialogResult.OK?(object)dialog.FileNames:null;}}
                else if(method=="resolveFiles")response=(dropped??new List<string>()).ToArray();
                else if(method=="dictateStart"){string problem=dictation.Start();response=new{Ok=problem==null,Error=problem};}
                else if(method=="dictateStop"){dictation.Stop();response=true;}
                else if(method=="saveText"){using(var dialog=new SaveFileDialog{Filter="Markdown (*.md)|*.md|Text file (*.txt)|*.txt",FileName=Convert.ToString(payload["Name"])}){if(dialog.ShowDialog(this)!=DialogResult.OK)response=null;else{File.WriteAllText(dialog.FileName,Convert.ToString(payload["Text"]));response=new{Message="Saved."};}}}
                else if(method=="pickFolder"){using(var dialog=new FolderBrowserDialog{Description="Choose a project folder",SelectedPath=backend.WorkingDirectory}){response=dialog.ShowDialog(this)==DialogResult.OK?dialog.SelectedPath:null;}}
                else if(method=="remoteStatus")response=remote.Status();
                else if(method=="remoteConfigure")response=remote.Configure(Convert.ToBoolean(payload["Enabled"]),Convert.ToInt32(payload["Port"]),Convert.ToBoolean(payload["Lan"]),payload.ContainsKey("Password")&&payload["Password"]!=null?Convert.ToString(payload["Password"]):null);
                else if(method=="openLink"){OpenLink(Convert.ToString(payload["Url"]));response=null;}
                else if(method=="desktopMode"){string config=Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex"),"config.toml");string text=File.Exists(config)?File.ReadAllText(config):"";var agents=System.Text.RegularExpressions.Regex.Match(text,@"(?ms)^\[agents\][ \t]*\r?\n(?<body>.*?)(?=^\[|\z)");var enabled=System.Text.RegularExpressions.Regex.Match(agents.Groups["body"].Value,@"(?m)^enabled\s*=\s*(true|false)");response=new{Mode=enabled.Success?(enabled.Groups[1].Value=="true"?"multi":"solo"):"unknown"};}
                else if(method=="restartCodex")response=await RestartCodex(Convert.ToString(payload["Mode"]));
                else response=await backend.HandleAsync(method,payload);
                Post(new{Type="response",Id=id,Result=response});
            }catch(Exception ex){Post(new{Type="response",Id=id,Error=ex.Message});}finally{if(entered)commandGate.Release();}
        }
        async Task<object> RestartCodex(string mode){if(mode!="multi"&&mode!="solo")throw new ArgumentException("Choose solo or multi.");string helper=Path.Combine(appDir,"Laica.Mode.ps1");if(!File.Exists(helper))throw new FileNotFoundException("The existing Codex mode helper is missing.");string result=Path.Combine(Path.GetTempPath(),"laica-mode-"+Guid.NewGuid().ToString("N")+".json");try{var info=new ProcessStartInfo{FileName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell/v1.0/powershell.exe"),Arguments="-NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File \""+helper+"\" -Mode "+mode+" -ResultFile \""+result+"\"",UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};using(var p=Process.Start(info))await Task.Run(()=>p.WaitForExit());if(!File.Exists(result))throw new Exception("The mode helper didn't finish. Reopen Codex normally.");var response=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(result));if(!Convert.ToBoolean(response["Success"]))throw new Exception(Convert.ToString(response["Error"]));return new{Message="Codex restarted. Start a fresh chat."};}finally{if(File.Exists(result))File.Delete(result);}}
        void OpenLink(string value){Uri uri;if(!Uri.TryCreate(value,UriKind.Absolute,out uri)||uri.Scheme!="https"||!(uri.Host=="github.com"||uri.Host=="learn.microsoft.com"||uri.Host=="ollama.com"||uri.Host=="lmstudio.ai"||uri.Host=="moelueker.com"))return;Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});}
        void Post(object message){if(IsDisposed||!IsHandleCreated)return;if(InvokeRequired){try{BeginInvoke(new Action(()=>Post(message)));}catch(InvalidOperationException){}return;}if(ready&&web.CoreWebView2!=null)web.CoreWebView2.PostWebMessageAsJson(json.Serialize(message));}
    }
    public static class Program {
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string cls,string name);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h,int message,IntPtr w,IntPtr l);
        [STAThread] public static void Main(string[] args){string exeDir=Path.GetDirectoryName(Application.ExecutablePath);string data=Option(args,"--data-dir")??exeDir;string assets=Option(args,"--assets")??Path.Combine(exeDir,"ui");bool preview=args.Contains("--preview");using(var mutex=new Mutex(false,"Local\\LAICA-"+(preview?"preview":"v05")+"-"+Environment.UserName)){bool owns=false;try{owns=mutex.WaitOne(0);}catch(AbandonedMutexException){owns=true;}if(!owns){var hwnd=FindWindow(null,preview?"LAICA · Desktop preview":"LAICA · Lightweight AI Harness");if(hwnd!=IntPtr.Zero)PostMessage(hwnd,0x8001,args.Contains("--mode")?new IntPtr(1):args.Contains("--codex-mode")?new IntPtr(3):IntPtr.Zero,IntPtr.Zero);return;}try{Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new GlassHost(data,assets,args.Contains("--mode")?"mode":args.Contains("--codex-mode")?"services":"harness",preview));}catch(Exception ex){MessageBox.Show(ex.Message,"LAICA",MessageBoxButtons.OK,MessageBoxIcon.Error);}finally{mutex.ReleaseMutex();}}}
        static string Option(string[] args,string key){int index=Array.IndexOf(args,key);return index>=0&&index+1<args.Length?Path.GetFullPath(args[index+1]):null;}
    }
}
