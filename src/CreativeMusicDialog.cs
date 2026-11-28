using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VideoBatch {
    internal sealed class CreativeMusicDialog:Form {
        readonly ListBox list=new ListBox{Width=550,Height=220,SelectionMode=SelectionMode.MultiExtended};
        readonly NumericUpDown level=new NumericUpDown{Minimum=0,Maximum=200,Visible=false};
        public List<string> Paths=>list.Items.Cast<string>().ToList();
        public double Volume=>(double)level.Value/100;
        public CreativeMusicDialog(CreativePreset preset) {
            Text="Настройки звука · Фоновая музыка";ClientSize=new Size(600,510);StartPosition=FormStartPosition.CenterParent;Font=Theme.FontBody;BackColor=Theme.Background;ForeColor=Theme.TextPrimary;
            var body=new FlowLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(20),FlowDirection=FlowDirection.TopDown,WrapContents=false};Controls.Add(body);
            body.Controls.Add(new Label{Text="Непрерывная музыка на весь ролик. Исходный звук и озвучка сохраняются.",Width=550,Height=40});
            foreach(string path in preset.BackgroundMusic)list.Items.Add(path);body.Controls.Add(list);
            var buttons=new FlowLayoutPanel{Width=550,Height=42};
            buttons.Controls.Add(Theme.MakeButton("Добавить музыку…",action:()=>{using(var picker=new OpenFileDialog{Multiselect=true,Filter="Аудио|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg"})if(picker.ShowDialog(this)==DialogResult.OK)foreach(string path in picker.FileNames)if(!list.Items.Contains(path))list.Items.Add(path);}));
            buttons.Controls.Add(Theme.MakeButton("Убрать",action:()=>{foreach(var path in list.SelectedItems.Cast<object>().ToArray())list.Items.Remove(path);}));body.Controls.Add(buttons);
            level.Value=(decimal)(preset.BackgroundMusicVolume*100);body.Controls.Add(level);
            body.Controls.Add(new AssemblySlider("Громкость музыки",level,"%") {Width=550});
            var footer=new FlowLayoutPanel{Width=550,Height=45};footer.Controls.Add(Theme.MakeButton("Применить",action:()=>{DialogResult=DialogResult.OK;Close();}));footer.Controls.Add(Theme.MakeButton("Отмена",action:()=>Close()));body.Controls.Add(footer);
        }
    }
}
