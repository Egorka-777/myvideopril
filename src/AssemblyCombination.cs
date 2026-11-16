using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Drawing;
using System.Windows.Forms;

namespace VideoBatch {
    public class AssemblyCombinationSettings {
        public bool Video = true, Background = true, Music, Voiceover;
        public bool SecondaryVideo = true;
        public AssemblyCombinationSettings Copy() { return (AssemblyCombinationSettings)MemberwiseClone(); }
    }
    public sealed partial class AssemblyWindow {
        void OpenCombinationSettings() { Safe(() => {
            if (cancellation != null) return;
            var settings = (template.Combination ?? new AssemblyCombinationSettings()).Copy();
            var changes = new List<Action>(); var videoSwitches = new List<CheckBox>(); var decorSwitches = new List<CheckBox>();
            using (var dialog = new Form { Name="AssemblyCombinationDialog", Text="Правила роликов · " + AssemblyProfile.ShortLabel(studioProfile, template), ClientSize=new Size(820,700), MinimumSize=new Size(700,540), StartPosition=FormStartPosition.CenterParent, BackColor=Theme.Background, ForeColor=Theme.TextPrimary, Font=Theme.FontBody }) {
                var root = new TableLayoutPanel { Dock=DockStyle.Fill, RowCount=4, ColumnCount=1, Padding=new Padding(22) };
                root.RowStyles.Add(new RowStyle(SizeType.Absolute,92)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,52)); root.RowStyles.Add(new RowStyle(SizeType.Percent,100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,52)); dialog.Controls.Add(root);
                var heading = new Label { Dock=DockStyle.Fill, Text="Что делает ролик новым?\r\n\r\nОтметь главное. Всё остальное — оформление: оно остаётся в видео, но не умножает количество роликов.", ForeColor=Theme.TextPrimary }; root.Controls.Add(heading,0,0);
                var presets = new FlowLayoutPanel { Dock=DockStyle.Fill, WrapContents=false };
                var summary = new Label { AutoSize=true, ForeColor=Theme.TextSecondary, Margin=new Padding(12,10,0,0) };
                var mainPreset = Theme.MakeButton("По основным видео", accent:true); mainPreset.Name="CombinationMainVideos"; presets.Controls.Add(mainPreset); presets.Controls.Add(summary); root.Controls.Add(presets,0,1);
                var rows = new FlowLayoutPanel { Dock=DockStyle.Fill, AutoScroll=true, FlowDirection=FlowDirection.TopDown, WrapContents=false }; root.Controls.Add(rows,0,2);
                Action updateSummary = () => { summary.Text=videoSwitches.Count(b=>b.Checked)+" источн. видео · "+decorSwitches.Count(b=>b.Checked)+" оформл."; };
                Func<string,string,IEnumerable<string>,bool,Action<bool>,bool,CheckBox> card = (title,description,files,value,set,isVideo) => {
                    var panel = new Panel { BackColor=Theme.Card, Width=735, Height=132, Padding=new Padding(14), Margin=new Padding(0,0,0,12) };
                    var label = new Label { Text=title, AutoSize=false, Left=14, Top=12, Width=400, Height=24, ForeColor=Theme.TextPrimary };
                    var toggle = new CheckBox { Text="Определяет новый ролик", Checked=value, AutoSize=true, Left=440, Top=14, Name="CombinationRule" };
                    var info = new Label { Text=description, Left=14, Top=43, Width=680, Height=22, ForeColor=Theme.TextSecondary };
                    var list = new ListBox { Left=14, Top=69, Width=680, Height=48, BackColor=Theme.Card, ForeColor=Theme.TextSecondary, BorderStyle=BorderStyle.None, HorizontalScrollbar=true, IntegralHeight=false, TabStop=false };
                    var names=files.Where(p=>!string.IsNullOrWhiteSpace(p)).Select(Path.GetFileName).Distinct().ToArray(); list.Items.AddRange(names.Length==0?new[]{"Нет загруженных файлов"}:names);
                    panel.Controls.Add(label); panel.Controls.Add(toggle); panel.Controls.Add(info); panel.Controls.Add(list); rows.Controls.Add(panel);
                    (isVideo?videoSwitches:decorSwitches).Add(toggle);
                    toggle.CheckedChanged+=(s,e)=>{set(toggle.Checked);updateSummary();};
                    rows.ClientSizeChanged+=(s,e)=>{panel.Width=Math.Max(620,rows.ClientSize.Width-24);};
                    return toggle;
                };
                Func<string,string[],IEnumerable<string>> pool=(path,extensions)=>AssemblyFiles.Pool(template.Resolve(path),extensions);
                var main = new List<CheckBox>(); var previews = new List<CheckBox>();
                foreach(var scene in template.Scenes) {
                    var current=scene; bool selected=settings.Video && current.VideoInCombination;
                    var files=pool(template.VideoSource(scene),AssemblyFiles.VideoExtensions).ToArray();
                    var box=card((scene.IsPreview?"ПРЕВЬЮ · ":"ВИДЕО · ")+scene.Name, files.Length+" файлов · "+(scene.IsPreview?"Вступление перед основным видео":"Основные материалы сцены"),files,selected,v=>selected=v,true);
                    box.Name="CombinationSceneVideo"+template.Scenes.IndexOf(scene);
                    (scene.IsPreview?previews:main).Add(box); changes.Add(()=>current.VideoInCombination=selected);
                }
                if(template.SplitLayout!="none") {
                    bool selected=settings.Video && settings.SecondaryVideo;
                    var box=card("ВТОРОЕ ВИДЕО", "Материалы второй половины экрана", pool(template.SecondaryVideos,AssemblyFiles.VideoExtensions),selected,v=>selected=v,true);
                    changes.Add(()=>settings.SecondaryVideo=selected); main.Add(box);
                }
                card("ФОН", "Подложка ролика",pool(template.Backgrounds,AssemblyFiles.BackgroundExtensions),settings.Background,v=>settings.Background=v,false);
                Action<AssemblyLayer,string> image=(layer,scope)=>{
                    bool selected=layer.InCombination;
                    var box=card(scope+" · "+layer.Name,layer.Enabled?(layer.Unique?"Картинки без повторов: расходуются независимо от правила":"Оформление сцены"):"Объект выключен — не участвует в сборке",pool(layer.Source,template.PictureExtensions),selected,v=>selected=v,false);
                    box.Enabled=layer.Enabled; changes.Add(()=>layer.InCombination=selected);
                };
                foreach(var scene in template.Scenes) foreach(var layer in scene.Layers) image(layer,scene.IsPreview?"Превью":scene.Name);
                foreach(var layer in template.GlobalLayers) image(layer,"Весь ролик");
                card("МУЗЫКА", "Фоновая музыка всего ролика и сцен",pool(template.Music,AssemblyFiles.MusicExtensions).Concat(template.Scenes.SelectMany(s=>pool(s.Music,AssemblyFiles.MusicExtensions))),settings.Music,v=>settings.Music=v,false);
                card("ОЗВУЧКА", "Речь всего ролика и сцен",template.HasSceneVoiceovers?template.Scenes.SelectMany(s=>AssemblySpeechPlan.SceneVoiceOptions(template,s)):AssemblySpeechPlan.SharedVoiceOptions(template),settings.Voiceover,v=>settings.Voiceover=v,false);
                mainPreset.Click+=(s,e)=>{
                    foreach(var box in main)box.Checked=true; foreach(var box in previews)box.Checked=false; foreach(var box in decorSwitches)box.Checked=false;
                    updateSummary();
                };
                updateSummary();
                var buttons=new FlowLayoutPanel { Dock=DockStyle.Fill, FlowDirection=FlowDirection.RightToLeft, Padding=new Padding(0,8,0,0) };
                var save=Button("Сохранить правила",()=>{ settings.Video=videoSwitches.Any(b=>b.Checked); template.Combination=settings; foreach(var change in changes)change(); dialog.DialogResult=DialogResult.OK;},true); save.Name="SaveCombinationSettings";
                var cancel=Button("Отмена",()=>dialog.DialogResult=DialogResult.Cancel); buttons.Controls.Add(save);buttons.Controls.Add(cancel);root.Controls.Add(buttons,0,3);dialog.AcceptButton=save;dialog.CancelButton=cancel;
                if(dialog.ShowDialog(this)==DialogResult.OK) { capacityKey=null; Changed(true); Save(); RequestCapacity(); status.Text="Правила сохранены. Количество роликов пересчитано с учётом истории."; }
            }
        }); }
    }
}
