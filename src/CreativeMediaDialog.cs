using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace VideoBatch {
    public static class CreativeMediaDialog {
        public static List<AssemblyLayer> Edit(IWin32Window owner, CreativeProject project, Preferences preferences) {
            CreativeProject.RequireFile(project.Source,"Исходное видео");
            string folder=Path.Combine(Path.GetTempPath(),"creative-layers-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try {
                var p=project.Preset;
                var size=p.PreserveSourceSize?new Size(p.Width,p.Height):AssemblyOverlayMedia.Size(project.Source);
                var bounds=CreativeGeometry.SourceBounds(p,size);
                var scene=new AssemblyScene {Name="Медиаслои · поверх креатива",Folder=folder,Videos=project.Source,VideoMode="own",VoiceMode="none",Duration=60,Layers=Store.Clone(p.MediaLayers)};
                var template=new AssemblyTemplate {Width=p.Width,Height=p.Height,Fps=p.Fps,Materials=folder,Output=folder,Videos=project.Source,Transition="cut",TransitionDuration=0,ExportSubtitleText=false,Scenes=new List<AssemblyScene>{scene},PackStudioVersion=1,ObjectStudioVersion=1,SceneTimingVersion=1};
                template.BaseVideoX=bounds.X/p.Width;template.BaseVideoY=bounds.Y/p.Height;template.BaseVideoWidth=bounds.Width/p.Width;template.BaseVideoHeight=bounds.Height/p.Height;
                string path=Path.Combine(folder,"layers.xml");AssemblyFiles.Save(path,template);
                using(var editor=new AssemblyWindow(preferences:Store.Clone(preferences),workspacePath:path,creativeLayersOnly:true)) {
                    editor.Text="Медиаслои креатива · время от начала итогового ролика";
                    if(editor.ShowDialog(owner)!=DialogResult.OK)return null;
                    var result=editor.CreativeLayers();
                    // Pack import copies files into this isolated workspace; keep those sources owned by the project.
                    string assets=Path.Combine(Store.Root,"creative-layer-assets",Guid.NewGuid().ToString("N"));
                    foreach(var layer in result) {
                        if(string.IsNullOrWhiteSpace(layer.Source))continue;
                        string full=Path.GetFullPath(Path.IsPathRooted(layer.Source)?layer.Source:Path.Combine(folder,layer.Source));
                        layer.Source=full;
                        if(!full.StartsWith(folder+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))continue;
                        Directory.CreateDirectory(assets);
                        string relative=full.Substring(folder.Length+1),destination=Path.Combine(assets,relative);
                        if(File.Exists(full)){Directory.CreateDirectory(Path.GetDirectoryName(destination));File.Copy(full,destination,true);}
                        else if(Directory.Exists(full))CopyDirectory(full,destination);
                        layer.Source=destination;
                    }
                    return result;
                }
            } finally { if(Directory.Exists(folder))Directory.Delete(folder,true); }
        }
        static void CopyDirectory(string source,string destination) {
            Directory.CreateDirectory(destination);
            foreach(string file in Directory.GetFiles(source))File.Copy(file,Path.Combine(destination,Path.GetFileName(file)),true);
            foreach(string dir in Directory.GetDirectories(source))CopyDirectory(dir,Path.Combine(destination,Path.GetFileName(dir)));
        }
    }
}
