param([string]$Assembly = "$PSScriptRoot\..\bin\Release\net48\VideoBatch.exe")
$ErrorActionPreference = 'Stop'
$assemblyPath = (Resolve-Path $Assembly).Path
$source = @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using VideoBatch;
public static class SocialDashboardSmoke {
    [STAThread] public static void Main(){Console.WriteLine(Run());}
    static BindingFlags Flags=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static void Check(bool value,string message){if(!value)throw new Exception(message);}
    public static string Run(){
        var assembly=typeof(Preferences).Assembly;
        var state=assembly.GetType("VideoBatch.MetaProfileState");
        var observation=assembly.GetType("VideoBatch.MetaProfileObservation");
        var row=Activator.CreateInstance(observation,true);
        observation.GetProperty("Id").SetValue(row,"202",null);
        observation.GetProperty("Name").SetValue(row,"Meta fixture",null);
        observation.GetProperty("CheckedUtc").SetValue(row,DateTime.UtcNow,null);
        var label=state.GetMethod("InstagramStatus",Flags);
        Check(((string)label.Invoke(null,new object[]{row,DateTime.UtcNow})).Contains("\u043d\u0435\u0438\u0437\u0432\u0435\u0441\u0442\u0435\u043d"),"Unknown must stay unknown");
        observation.GetProperty("InstagramCookie").SetValue(row,true,null);
        observation.GetProperty("CookieExpiresUtc").SetValue(row,DateTime.UtcNow.AddHours(1),null);
        Check(((string)label.Invoke(null,new object[]{row,DateTime.UtcNow})).Contains("\u043e\u043d\u043b\u0430\u0439\u043d \u043d\u0435 \u043f\u0440\u043e\u0432\u0435\u0440\u0435\u043d"),"Cookie must not confirm online login");
        Check(((string)label.Invoke(null,new object[]{row,DateTime.UtcNow.AddDays(1)})).Contains("\u0438\u0441\u0442\u0451\u043a"),"Expired cookie must not appear current");
        var directory=Path.Combine(Path.GetTempPath(),"VideoBatchDashboardSmoke-"+Guid.NewGuid().ToString("N"));
        var path=Path.Combine(directory,"profiles.json");
        try{
            state.GetMethod("Save",Flags).Invoke(null,new object[]{row,path});
            var saved=(IList)state.GetMethod("Load",Flags).Invoke(null,new object[]{path});
            Check(saved.Count==1,"Observation must persist");
            state.GetMethod("Save",Flags).Invoke(null,new object[]{row,path});
            saved=(IList)state.GetMethod("Load",Flags).Invoke(null,new object[]{path});
            Check(saved.Count==1,"Same profile must be updated, not duplicated");
            var prefs=new Preferences();
            prefs.YouTubeChannels=new List<YouTubeChannel>{new YouTubeChannel{ProfileId="101",Name="YT fixture",Status="Готов"}};
            prefs.TikTokAccounts=new List<TikTokAccount>();
            var dashboardType=assembly.GetType("VideoBatch.SocialDashboard");
            var groupMethod=dashboardType.GetMethod("ProfileGroup",Flags);
            Check((string)groupMethod.Invoke(null,new object[]{new DolphinBrowserProfile{Name="RU misleading",FolderName="EN"}})=="EN","Group must ignore name");
            Check((string)groupMethod.Invoke(null,new object[]{new DolphinBrowserProfile{Name="EN only"}})!="EN","Name alone must not classify");
            Check((string)groupMethod.Invoke(null,new object[]{new DolphinBrowserProfile{Tags=new List<string>{"RU"}}})=="RU","Explicit RU tag must classify");
            using(var dashboard=(Control)Activator.CreateInstance(dashboardType,Flags,null,new object[]{prefs},null)){
                dashboardType.GetField("profiles",Flags).SetValue(dashboard,new List<DolphinBrowserProfile>{new DolphinBrowserProfile{Id="303",Name="Empty fixture"}});
                dashboardType.GetField("proxies",Flags).SetValue(dashboard,new List<TelegramProxy>{new TelegramProxy{url="http://secret:password@localhost:8888",source_id="dolphin:proxy:1"}});
                dashboardType.GetMethod("Render",Flags).Invoke(dashboard,new object[]{saved,new TelegramData()});
                var grid=(DataGridView)dashboardType.GetField("grid",Flags).GetValue(dashboard);
                Check(grid.Rows.Count==3,"Only profile rows, no spare proxies");
                Check(grid.Columns.Count==8,"Compact columns, no Dolphin ID");
                foreach(Button button in (IEnumerable)dashboardType.GetField("checkButtons",Flags).GetValue(dashboard))Check(!button.Enabled,"Unwired check buttons must be disabled");
                bool yt=false,empty=false,meta=false;
                foreach(DataGridViewRow r in grid.Rows){
                    string name=Convert.ToString(r.Cells[0].Value);
                    Check(r.Height==28,"Rows must be fixed 28px");
                    if(name=="YT fixture"){yt=true;Check(Convert.ToString(r.Cells[2].Value)=="?","Ready is not logged in");}
                    if(name=="Empty fixture"){empty=true;Check(Convert.ToString(r.Cells[7].Value).Contains("YouTube"),"Missing networks not shown");}
                    if(name=="Meta fixture"){meta=true;Check(Convert.ToString(r.Cells[4].Value)=="?"&&Convert.ToString(r.Cells[5].Value)=="?","Instagram cookie must not authenticate Threads");}
                    foreach(DataGridViewCell cell in r.Cells)Check(!Convert.ToString(cell.Value).Contains("password")&&!cell.ToolTipText.Contains("password"),"Proxy password leaked");
                }
                Check(yt&&empty&&meta,"Expected fixture profiles absent");
                using(var host=new Form()){
                    host.ClientSize=new System.Drawing.Size(1100,590);host.BackColor=Theme.Background;host.Controls.Add(dashboard);
                    host.CreateControl();dashboard.CreateControl();host.PerformLayout();dashboard.PerformLayout();
                    using(var bitmap=new System.Drawing.Bitmap(host.Width,host.Height)){
                        host.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,host.Width,host.Height));
                        string screenshot=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"SocialDashboardFixture.png");bitmap.Save(screenshot);Console.WriteLine("Screenshot: "+screenshot);
                    }
                }
            }
            File.WriteAllText(path,"broken json");
            bool failed=false;try{state.GetMethod("Load",Flags).Invoke(null,new object[]{path});}catch(TargetInvocationException){failed=true;}
            Check(failed,"Corrupt metadata must not silently become empty");
        }finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
        Check(AppPlatformNav.IsNavVisible(AppPlatform.Instagram,NavSection.Home),"Home inaccessible from Instagram");
        return "PASS: compact eight-column 28px profile-only table; missing vs unverified marks; cookie not verified; persistence; no spare proxies/credentials; fixture screenshot; no network or user media accessed.";
    }
}
'@
$sourcePath = [IO.Path]::Combine([IO.Path]::GetTempPath(), ('SocialDashboardSmoke-' + [Guid]::NewGuid().ToString('N') + '.cs'))
$exe = [IO.Path]::Combine([IO.Path]::GetDirectoryName($assemblyPath), 'SocialDashboardSmoke.exe')
try {
    [IO.File]::WriteAllText($sourcePath, $source, [Text.Encoding]::UTF8)
    & "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe "/out:$exe" "/reference:$assemblyPath" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll $sourcePath
    if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed' }
    & $exe
    if ($LASTEXITCODE -ne 0) { throw 'Dashboard fixtures failed' }
} finally { [IO.File]::Delete($sourcePath) }
