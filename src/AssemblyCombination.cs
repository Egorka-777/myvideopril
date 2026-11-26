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
            using (var dialog = new Form { Name="AssemblyCombinationDialog", Text="Правила роликов · " + AssemblyProfile.ShortLabel(studioProfile, template), ClientSize=new Size(820,700), MinimumSize=new Size(680,540), StartPosition=FormStartPosition.CenterParent, BackColor=Theme.Background, ForeColor=Theme.TextPrimary, Font=Theme.FontBody }) {
                var root = new TableLayoutPanel { Dock=DockStyle.Fill, RowCount=4, ColumnCount=1, Padding=new Padding(20) };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute,90)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,48)); root.RowStyles.Add(new RowStyle(SizeType.Percent,100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,54)); dialog.Controls.Add(root);
                var heading = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=2, Margin=Padding.Empty };
                heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); heading.RowStyles.Add(new RowStyle(SizeType.Absolute,36)); heading.RowStyles.Add(new RowStyle(SizeType.Percent,100));
                heading.Controls.Add(new Label { Dock=DockStyle.Fill, Text="Что делает ролик новым?", Font=Theme.FontCardTitle, ForeColor=Theme.TextPrimary, Margin=Padding.Empty },0,0);
                int bgVideos=AssemblyFiles.Pool(template.Resolve(template.Videos),AssemblyFiles.VideoExtensions).Count;
                int splitVideos=template.SplitLayout!="none"?template.SplitPoolFileCount():0;
                string materialsHint=template.SplitLayout!="none"
                    ?"Сейчас: фон "+bgVideos+(template.SplitSharedPool?" видео · деление из общей пачки ("+splitVideos+")":" · верх "+bgVideos+", низ "+AssemblyFiles.Pool(template.Resolve(template.SecondaryVideos),AssemblyFiles.VideoExtensions).Count)+". Отметьте, что считается новой связкой."
                    :"Отметь материалы, которые определяют новый ролик. Остальные остаются в видео, но не умножают количество вариантов.";
                heading.Controls.Add(new Label { Dock=DockStyle.Fill, Text=materialsHint, ForeColor=Theme.TextSecondary, Margin=Padding.Empty },0,1); root.Controls.Add(heading,0,0);
                var presets = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=2, RowCount=1, Margin=Padding.Empty };
                presets.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); presets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                var summary = new Label { Dock=DockStyle.Fill, TextAlign=ContentAlignment.MiddleLeft, AutoEllipsis=true, ForeColor=Theme.TextSecondary, Margin=new Padding(12,0,0,8) };
                var mainPreset = Theme.MakeButton("По основным видео", ghost:true); mainPreset.Name="CombinationMainVideos"; presets.Controls.Add(mainPreset,0,0); presets.Controls.Add(summary,1,0); root.Controls.Add(presets,0,1);
                var scroll = new Panel { Name="CombinationScroll", Dock=DockStyle.Fill, AutoScroll=true, Margin=Padding.Empty }; root.Controls.Add(scroll,0,2);
                var groups = new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, ColumnCount=1, RowCount=0, Margin=Padding.Empty, Padding=new Padding(0,0,4,0) };
                groups.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); scroll.Controls.Add(groups);
                Action updateSummary = () => { summary.Text=videoSwitches.Count(b=>b.Checked)+" источн. видео · "+decorSwitches.Count(b=>b.Checked)+" оформл."; };
                Action<TableLayoutPanel,Control> append=(table,control)=>{ int row=table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize)); table.Controls.Add(control,0,row); };
                Func<string,string,TableLayoutPanel> group=(title,name)=>{
                    var panel=Theme.MakeCard(); panel.Name=name; panel.Dock=DockStyle.Fill; panel.AutoSize=true; panel.AutoSizeMode=AutoSizeMode.GrowAndShrink; panel.Padding=new Padding(14); panel.Margin=new Padding(0,0,0,12);
                    var body=new TableLayoutPanel { Dock=DockStyle.Top, AutoSize=true, AutoSizeMode=AutoSizeMode.GrowAndShrink, ColumnCount=1, RowCount=0, Margin=Padding.Empty, BackColor=Theme.Card };
                    body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
                    append(body,new Label { Name=name+"Title", Text=title, Font=Theme.FontCardTitle, ForeColor=Theme.TextPrimary, Dock=DockStyle.Fill, Height=30, AutoEllipsis=true, Margin=Padding.Empty });
                    panel.Controls.Add(body); append(groups,panel); return body;
                };
                Func<TableLayoutPanel,string,string,IEnumerable<string>,bool,Action<bool>,bool,CheckBox> material = (body,title,description,files,value,set,isVideo) => {
                    var row = new TableLayoutPanel { Dock=DockStyle.Fill, Height=86, ColumnCount=2, RowCount=3, Margin=new Padding(0,3,0,3), BackColor=Theme.Card };
                    row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                    row.RowStyles.Add(new RowStyle(SizeType.Absolute,24)); row.RowStyles.Add(new RowStyle(SizeType.Absolute,23)); row.RowStyles.Add(new RowStyle(SizeType.Percent,100));
                    var names=files.Where(p=>!string.IsNullOrWhiteSpace(p)).Select(Path.GetFileName).Distinct().ToArray();
                    row.Controls.Add(new Label { Text=title+" · "+names.Length+" файлов", Dock=DockStyle.Fill, TextAlign=ContentAlignment.MiddleLeft, AutoEllipsis=true, ForeColor=Theme.TextPrimary, Margin=Padding.Empty },0,0);
                    row.Controls.Add(new Label { Text=description, Dock=DockStyle.Fill, AutoEllipsis=true, ForeColor=Theme.TextSecondary, Font=Theme.FontSmall, Margin=Padding.Empty },0,1);
                    var list = new ListBox { Dock=DockStyle.Fill, BackColor=Theme.Card, ForeColor=Theme.TextMuted, Font=Theme.FontSmall, BorderStyle=BorderStyle.None, HorizontalScrollbar=true, IntegralHeight=false, TabStop=false, Margin=new Padding(0,0,12,0) };
                    list.Items.AddRange(names.Length==0?new[]{"Нет загруженных файлов"}:names); row.Controls.Add(list,0,2);
                    append(body,row);
                    if(set==null) return null;
                    var toggle = new CheckBox { Text="Новый ролик", Checked=value, AutoSize=true, Anchor=AnchorStyles.Right, ForeColor=Theme.TextPrimary, Margin=new Padding(14,0,0,0), Name="CombinationRule" };
                    row.Controls.Add(toggle,1,0); row.SetRowSpan(toggle,3);
                    (isVideo?videoSwitches:decorSwitches).Add(toggle);
                    toggle.CheckedChanged+=(s,e)=>{set(toggle.Checked);updateSummary();}; return toggle;
                };
                Func<string,string[],IEnumerable<string>> pool=(path,extensions)=>AssemblyFiles.Pool(template.Resolve(path),extensions);
                Action<TableLayoutPanel,AssemblyLayer,string> image=(body,layer,name)=>{
                    bool selected=layer.InCombination;
                    var box=material(body,layer.Name,layer.Enabled?(layer.Unique?"Без повторов: расходуются независимо от правила":"Оформление сцены"):"Объект выключен — не участвует в сборке",pool(layer.Source,template.PictureExtensions),selected,v=>selected=v,false);
                    box.Name=name; box.Enabled=layer.Enabled; changes.Add(()=>layer.InCombination=selected);
                };
                var main = new List<CheckBox>(); var previews = new List<CheckBox>();
                for(int i=0;i<template.Scenes.Count;i++) {
                    var scene=template.Scenes[i]; bool selected=settings.Video && scene.VideoInCombination;
                    var body=group(SceneLabel(scene,i),"CombinationScene"+i);
                    var box=material(body,"Видео",scene.IsPreview?"Вступление перед основным видео":"Основные материалы сцены",pool(template.VideoSource(scene),AssemblyFiles.VideoExtensions),selected,v=>selected=v,true);
                    box.Name="CombinationSceneVideo"+i; (scene.IsPreview?previews:main).Add(box); changes.Add(()=>scene.VideoInCombination=selected);
                    for(int j=0;j<scene.Layers.Count;j++) image(body,scene.Layers[j],"CombinationSceneLayer"+i+"_"+j);
                    append(body,new Label { Name="CombinationSceneVoice"+i, Text="Озвучка · "+AssemblySpeechPlan.SceneVoiceOptions(template,scene).Count+" файлов · Общее правило ниже", Dock=DockStyle.Fill, Height=28, TextAlign=ContentAlignment.MiddleLeft, AutoEllipsis=true, ForeColor=Theme.TextSecondary, Font=Theme.FontSmall, Margin=Padding.Empty });
                }
                var common=group("Общие материалы","CombinationCommon");
                bool poolVideo=settings.Video && template.Scenes.Any(s=>!s.IsPreview && s.VideoInCombination);
                var poolBox=material(common,"Фоновое видео", "Общая пачка клипов · верх при делении экрана", pool(template.Videos,AssemblyFiles.VideoExtensions),poolVideo,v=>poolVideo=v,true);
                poolBox.Name="CombinationBackgroundVideos"; main.Add(poolBox);
                changes.Add(()=>{
                    foreach(var s in template.Scenes) if(!s.IsPreview) s.VideoInCombination=poolVideo;
                    settings.Video=poolVideo || (template.SplitLayout!="none" && settings.SecondaryVideo) || template.Scenes.Any(s=>s.IsPreview && s.VideoInCombination);
                });
                if(template.SplitLayout!="none") {
                    bool shared=template.SplitSharedPool;
                    var secondPaths=shared ? template.SplitVideoPaths() : pool(template.SecondaryVideos,AssemblyFiles.VideoExtensions);
                    bool secondarySelected=settings.Video && settings.SecondaryVideo;
                    string secondHint=shared ? "Низ при общей пачке · те же файлы, другая половина" : "Пачка для низа / правой половины";
                    var box=material(common, shared ? "Вторая половина · из общей пачки" : "Второе видео", secondHint, secondPaths,secondarySelected,v=>secondarySelected=v,true);
                    box.Name="CombinationSecondaryVideo"; changes.Add(()=>settings.SecondaryVideo=secondarySelected); main.Add(box);
                }
                material(common,"Фон · картинки", "Подложка-картинка или рамка",pool(template.Backgrounds,AssemblyFiles.BackgroundExtensions),settings.Background,v=>settings.Background=v,false).Name="CombinationBackground";
                for(int j=0;j<template.GlobalLayers.Count;j++) image(common,template.GlobalLayers[j],"CombinationGlobalLayer"+j);
                material(common,"Музыка", "Одно правило для всего ролика и всех сцен",pool(template.Music,AssemblyFiles.MusicExtensions).Concat(template.Scenes.SelectMany(s=>pool(s.Music,AssemblyFiles.MusicExtensions))),settings.Music,v=>settings.Music=v,false).Name="CombinationMusic";
                material(common,"Озвучка", "Одно правило для всего ролика и всех сцен",template.HasSceneVoiceovers?template.Scenes.SelectMany(s=>AssemblySpeechPlan.SceneVoiceOptions(template,s)):AssemblySpeechPlan.SharedVoiceOptions(template),settings.Voiceover,v=>settings.Voiceover=v,false).Name="CombinationVoiceover";
                mainPreset.Click+=(s,e)=>{
                    foreach(var box in main)box.Checked=true; foreach(var box in previews)box.Checked=false; foreach(var box in decorSwitches)box.Checked=false;
                    updateSummary();
                };
                updateSummary();
                var buttons=new FlowLayoutPanel { Dock=DockStyle.Fill, FlowDirection=FlowDirection.RightToLeft, WrapContents=false, Padding=new Padding(0,12,0,0), Margin=Padding.Empty };
                var save=Theme.MakeButton("Сохранить правила",accent:true,action:()=>{ settings.Video=videoSwitches.Any(b=>b.Checked); template.Combination=settings; foreach(var change in changes)change(); dialog.DialogResult=DialogResult.OK;}); save.Name="SaveCombinationSettings";
                var cancel=Theme.MakeButton("Отмена",ghost:true,action:()=>dialog.DialogResult=DialogResult.Cancel); cancel.Name="CancelCombinationSettings";
                buttons.Controls.Add(save);buttons.Controls.Add(cancel);root.Controls.Add(buttons,0,3);dialog.AcceptButton=save;dialog.CancelButton=cancel;
                if(dialog.ShowDialog(this)==DialogResult.OK) { capacityKey=null; Changed(true); Save(); RequestCapacity(); status.Text="Правила сохранены. Количество роликов пересчитано с учётом истории."; }
            }
        }); }
    }
}
