using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace VideoBatch {
    public class CreativeRect {
        public double X, Y, Width = 1, Height = 1;
        public CreativeRect() { }
        public CreativeRect(double x, double y, double width, double height) { X=x; Y=y; Width=width; Height=height; }
        internal void Validate(string name) {
            if (new[] { X,Y,Width,Height }.Any(v=>double.IsNaN(v)||double.IsInfinity(v)) || X<0 || Y<0 || Width<=0 || Height<=0 || X+Width>1.000001 || Y+Height>1.000001)
                throw new Exception(name+": прямоугольник должен находиться внутри кадра (координаты 0–1).");
        }
    }
    public class CreativeComment {
        public string ImagePath="", Text="", AudioPath="";
    }
    public class CreativePreset {
        public int Width=1080, Height=1920, Fps=30;
        public bool PreserveSourceSize=true;
        internal Size ApplySourceSize(Media media) {
            if(PreserveSourceSize) { Width=media.Width; Height=media.Height; }
            // Regions are normalized: changing the canvas must not replace custom placement.
            return new Size(Width,Height);
        }
        public double SourceZoom=1, SourcePanX=0, SourcePanY=0;
        public double SourceSpeed=1;
        public bool RewriteOutputTitle=true;
        [XmlIgnore] public List<string> OutputHashtags=new List<string>{"#трейдинг","#бинарныеопционы","#binodex"};
        [XmlArray("OutputHashtags")] public string[] SavedOutputHashtags {
            get => OutputHashtags?.ToArray();
            set => OutputHashtags=value==null?new List<string>():new List<string>(value);
        }
        public AssemblySpeechSettings Speech=new AssemblySpeechSettings();
        public string Model="google/gemini-2.5-flash", Language="ru";
        public bool AutoIntroLanguage=true, TranslateTexts;
        public string TranslationLanguage="en";
        public double MixDuration=.35, MotionDuration=.35;
        public bool AudioLevelingEnabled=true;
        public List<string> BackgroundMusic=new List<string>();
        public double BackgroundMusicVolume=.1;
        public CreativeEffectsSettings Effects=new CreativeEffectsSettings();
        public string CardAppearance="fade";
        public double CardAppearanceDuration=.25, CardExitDuration=.2;
        public CreativeRect FinalVideo=new CreativeRect(.12,.08,.76,.43), Card=new CreativeRect(.06,.57,.88,.30);
        public CreativeRect IntroMask=new CreativeRect(.05,.65,.9,.2), IntroCoverBounds=new CreativeRect(.05,.65,.9,.2);
        public bool IntroBlur;
        public double BlurStrength=12;
        public string IntroCover="", Banner="";
        public CreativeRect BannerBounds=new CreativeRect(0,0,1,.08);
        public List<AssemblyInsertion> Insertions=new List<AssemblyInsertion>();
        public List<AssemblyLayer> MediaLayers=new List<AssemblyLayer>();
        public List<CreativeRect> CommentCards=new List<CreativeRect>();
        // Old presets used Card as a single envelope; split it without modifying the saved project.
        public CreativeRect CommentBounds(int index) {
            if (CommentCards != null && CommentCards.Count == 3) return CommentCards[index];
            double gap=Math.Min(.015,Card.Height/10), height=(Card.Height-2*gap)/3;
            return new CreativeRect(Card.X,Card.Y+index*(height+gap),Card.Width,height);
        }
        public void EnsureCommentCards() {
            if(CommentCards!=null && CommentCards.Count==3)return;
            CommentCards=Enumerable.Range(0,3).Select(CommentBounds).ToList();
        }
        public CreativePreset Clone() {
            using(var stream=new MemoryStream()) { var xml=new XmlSerializer(typeof(CreativePreset)); xml.Serialize(stream,this);stream.Position=0;return (CreativePreset)xml.Deserialize(stream); }
        }
        public static CreativePreset Load(string path) { return AssemblyFiles.Load<CreativePreset>(path); }
        public void Save(string path) { AssemblyFiles.Save(path,this); }
        public void Validate() {
            if(BackgroundMusic==null)BackgroundMusic=new List<string>();
            if(double.IsNaN(BackgroundMusicVolume)||double.IsInfinity(BackgroundMusicVolume)||BackgroundMusicVolume<0||BackgroundMusicVolume>2)throw new Exception("Громкость фоновой музыки должна быть от 0 до 200%.");
            if(Effects==null)Effects=new CreativeEffectsSettings();
            Effects.Validate();
            if(TranslateTexts&&!System.Text.RegularExpressions.Regex.IsMatch(TranslationLanguage??"",@"\A[a-z]{2,3}\z"))throw new Exception("Укажите код языка перевода: en, ru, es, de и т. п.");
            if(double.IsNaN(SourceSpeed)||double.IsInfinity(SourceSpeed)||SourceSpeed<.5||SourceSpeed>2)throw new Exception("Скорость оригинала: от 0,50 до 2,00×.");
            if(Width<2 || Height<2 || Width%2!=0 || Height%2!=0 || Fps<1 || Fps>60) throw new Exception("Креатив: нужны чётные размеры кадра и FPS 1–60.");
            foreach(var value in new[]{MixDuration,MotionDuration,CardAppearanceDuration,CardExitDuration})
                if(double.IsNaN(value)||double.IsInfinity(value)||value<0) throw new Exception("Длительность анимации должна быть неотрицательным числом.");
            if(double.IsNaN(SourceZoom)||double.IsInfinity(SourceZoom)||SourceZoom<.1 || double.IsNaN(SourcePanX)||double.IsInfinity(SourcePanX)||Math.Abs(SourcePanX)>1 || double.IsNaN(SourcePanY)||double.IsInfinity(SourcePanY)||Math.Abs(SourcePanY)>1) throw new Exception("Кадрирование: масштаб от 0,1, сдвиг от −1 до 1.");
            if(!AssemblyTemplate.Transitions.Contains(CardAppearance)) throw new Exception("Неизвестная анимация карточки.");
            if(FinalVideo==null || Card==null || IntroMask==null || IntroCoverBounds==null || BannerBounds==null) throw new Exception("Не заданы области оформления.");
            FinalVideo.Validate("Финальное видео");Card.Validate("Карточка");IntroMask.Validate("Маска");IntroCoverBounds.Validate("Плашка");BannerBounds.Validate("Баннер");
            if(IntroBlur && (double.IsNaN(BlurStrength)||double.IsInfinity(BlurStrength)||BlurStrength<=0)) throw new Exception("Сила размытия должна быть положительной.");
            if(!string.IsNullOrWhiteSpace(IntroCover)) CreativeProject.RequireFile(IntroCover,"Плашка вступления");
            if(!string.IsNullOrWhiteSpace(Banner)) CreativeProject.RequireFile(Banner,"Баннер");
            if(CommentCards!=null && CommentCards.Count!=0 && CommentCards.Count!=3) throw new Exception("Нужны три области комментариев.");
            for(int i=0;i<3;i++)CommentBounds(i).Validate("Комментарий "+(i+1));
            if(MediaLayers==null || MediaLayers.Any(l=>l==null))throw new Exception("Некорректный список медиаслоёв.");
            if(Insertions==null || Insertions.Any(i=>i==null)) throw new Exception("Некорректный список рекламных пауз.");
        }
    }
    public class CreativeProject {
        public string Source="", IntroText="", IntroAudio="";
        public string DetectedLanguage="", LanguageSource="", TranslatedTextSignature="";
        public string SourceTitle="", SourceUrl="", OutputTitle="", OutputTitleKey="";
        public string IntroLanguage => Preset.TranslateTexts?Preset.TranslationLanguage:Preset.AutoIntroLanguage&&LanguageSource=="title:"+Source+"\n"+SourceTitle&&!string.IsNullOrWhiteSpace(DetectedLanguage)?DetectedLanguage:Preset.Language;
        public List<CreativeComment> Comments=new List<CreativeComment>();
        public CreativePreset Preset=new CreativePreset();
        public static CreativeProject Load(string path) { return AssemblyFiles.Load<CreativeProject>(path); }
        public void Save(string path) { AssemblyFiles.Save(path,this); }
        public async Task<Size> ResolveSourceSizeAsync(string probe, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            if(Preset==null)throw new Exception("Не задан пресет креатива.");
            if(!Preset.PreserveSourceSize)return new Size(Preset.Width,Preset.Height);
            RequireFile(Source,"Исходное видео");
            var media=Core.VideoInfo(await Core.Probe(probe,Source,ct).ConfigureAwait(false));
            ct.ThrowIfCancellationRequested();
            return Preset.ApplySourceSize(media);
        }
        public void Validate() {
            RequireFile(Source,"Исходное видео"); RequireFile(IntroAudio,"Озвучка вступления");
            if(Preset==null) throw new Exception("Не задан пресет креатива."); Preset.Validate();
            if(Comments==null || Comments.Count!=3 || Comments.Any(c=>c==null)) throw new Exception("Добавьте три комментария в нужном порядке.");
            for(int i=0;i<Comments.Count;i++) { RequireFile(Comments[i].ImagePath,"Скриншот комментария "+(i+1));RequireFile(Comments[i].AudioPath,"Озвучка комментария "+(i+1)); }
        }
        internal static void RequireFile(string path,string name) { if(string.IsNullOrWhiteSpace(path)||!File.Exists(path)) throw new FileNotFoundException(name+": файл не найден.",path); }
    }
}
