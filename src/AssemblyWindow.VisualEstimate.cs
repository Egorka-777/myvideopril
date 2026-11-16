using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace VideoBatch {
    public sealed partial class AssemblyWindow {
        Label visualEstimateLabel;
        string visualEstimateDetails="Предварительная оценка ещё не выполнена.";
        System.Windows.Forms.Timer visualEstimateTimer;
        CancellationTokenSource visualEstimateCancellation;
        int visualEstimateVersion;
        bool visualEstimateDisposed;
        void InitializeVisualEstimate() {
            var panel=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,110));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            visualEstimateLabel=new Label { Name="VisualDifferenceEstimate",Dock=DockStyle.Fill,Text="Отличие кадра: — · цель ≥40%",ForeColor=Theme.TextSecondary,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft,Margin=new Padding(8,0,0,0) };
            var details=Button("Подробности",()=>MessageBox.Show(this,visualEstimateDetails,"Предварительное визуальное отличие",MessageBoxButtons.OK,MessageBoxIcon.Information));
            details.Name="VisualDifferenceDetails"; details.AutoSize=false; details.MinimumSize=Size.Empty; details.Margin=new Padding(2); details.Padding=new Padding(4); details.Dock=DockStyle.Fill;
            panel.Controls.Add(visualEstimateLabel,0,0); panel.Controls.Add(details,1,0);
            previewColumn.Controls.Add(panel,0,3);
            visualEstimateTimer=new System.Windows.Forms.Timer { Interval=1200 };
            visualEstimateTimer.Tick+=(s,e)=> {visualEstimateTimer.Stop(); StartVisualEstimate();};
            RequestVisualEstimate();
        }
        void RequestVisualEstimate() {
            if(visualEstimateDisposed || visualEstimateTimer==null) return;
            visualEstimateVersion++; visualEstimateCancellation?.Cancel();
            visualEstimateTimer.Stop();
            visualEstimateLabel.Text="Отличие кадра: пересчёт… · цель ≥40%";
            visualEstimateLabel.ForeColor=Theme.TextSecondary;
            visualEstimateDetails="Настройки изменились. Предыдущая оценка больше не актуальна; ожидается новый расчёт.";
            visualEstimateTimer.Start();
        }
        async void StartVisualEstimate() {
            int version=visualEstimateVersion;
            var source=new CancellationTokenSource(); visualEstimateCancellation=source;
            string work=Path.Combine(previewFolder,"visual-estimate-"+Guid.NewGuid().ToString("N"));
            try {
                if(template.Scenes.Count==0) { visualEstimateLabel.Text="Отличие кадра: — · добавьте сцену"; visualEstimateDetails="Для оценки нужна хотя бы одна сцена с исходным видео."; return; }
                if(!ToolsLocator.TryResolve(out string ffmpeg,out string probe,out string hint)) throw new Exception(hint);
                var snapshot=Store.Clone(template);
                var report=await AssemblyVisualEstimate.Analyze(snapshot,ffmpeg,probe,work,source.Token);
                if(visualEstimateDisposed || source.IsCancellationRequested || version!=visualEstimateVersion) return;
                visualEstimateDetails=report.Summary+Environment.NewLine+Environment.NewLine+report.Details;
                if(!report.Completed || !report.DifferencePercent.HasValue) {
                    visualEstimateLabel.Text="Отличие кадра: оценка недоступна"; visualEstimateLabel.ForeColor=Theme.Warning;
                    AppDiagnostics.Write("Сборка",studioProfile,"","visual-estimate",visualEstimateDetails,"warning"); return;
                }
                double value=report.DifferencePercent.Value;
                bool below=!report.MinimumPercent.HasValue || report.MinimumPercent.Value<AssemblyVisualEstimate.TargetPercent;
                bool partial=report.Details.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Skip(1).Any(line=>line.Contains("не учт") || line.Contains("не моделируются") || line.Contains("приближённый"));
                visualEstimateLabel.Text="Отличие кадра: ≈"+value.ToString("0.0")+"%"+Environment.NewLine+"Мин. "+(report.MinimumPercent.HasValue ? report.MinimumPercent.Value.ToString("0.0")+"%" : "—")+" · "+(below ? "ниже цели 40%" : "выборка ≥40%")+(partial ? " · частично*" : "");
                visualEstimateLabel.ForeColor=below || partial ? Theme.Warning : Theme.Success;
                visualEstimateDetails=report.Summary+Environment.NewLine+"Контрольных кадров: "+report.Samples+Environment.NewLine+Environment.NewLine+report.Details+Environment.NewLine+Environment.NewLine+DescribeVisualSettings(snapshot)+Environment.NewLine+Environment.NewLine+"Это оценка контрольных кадров одной будущей связки, не гарантия каждого кадра или всей пачки и не оценка платформ. Музыка и метаданные в процент изображения не входят. Сборка не блокируется.";
            } catch(OperationCanceledException) { }
            catch(Exception e) {
                if(visualEstimateDisposed || version!=visualEstimateVersion) return;
                visualEstimateLabel.Text="Отличие кадра: ошибка расчёта"; visualEstimateLabel.ForeColor=Theme.Warning;
                visualEstimateDetails=e.Message;
                AppDiagnostics.Write("Сборка",studioProfile,"","visual-estimate",e.ToString(),"error");
            } finally {
                if(ReferenceEquals(visualEstimateCancellation,source)) visualEstimateCancellation=null;
                source.Dispose();
                try { if(Directory.Exists(work)) Directory.Delete(work,true); }
                catch(IOException e) { AppDiagnostics.Write("Сборка",studioProfile,"","visual-estimate-cleanup",e.Message,"warning"); }
                catch(UnauthorizedAccessException e) { AppDiagnostics.Write("Сборка",studioProfile,"","visual-estimate-cleanup",e.Message,"warning"); }
            }
        }
        static string DescribeVisualSettings(AssemblyTemplate t) {
            var objects=t.GlobalLayers.Where(l=>l.Enabled).Select(l=>new {Layer=l,Scope="весь ролик"})
                .Concat(t.Scenes.SelectMany((scene,i)=>scene.Layers.Where(l=>l.Enabled).Select(l=>new {Layer=l,Scope="сцена "+(i+1)})));
            return "Что влияет на изображение (параметры, не дополнительные баллы):"+Environment.NewLine
                +"Основное видео: X="+t.BaseVideoX.ToString("0.##")+", Y="+t.BaseVideoY.ToString("0.##")+", ширина="+(t.BaseVideoWidth*100).ToString("0.#")+"%, высота="+(t.BaseVideoHeight*100).ToString("0.#")+"%."+Environment.NewLine
                +"Фон: "+(string.IsNullOrWhiteSpace(t.Backgrounds) ? "не задан" : t.BackgroundForeground ? "передний план" : "за видео; виден в свободной области")+"."+Environment.NewLine
                +string.Join(Environment.NewLine,objects.Select(o=>o.Scope+" · "+o.Layer.Name+": рамка "+(o.Layer.Width*100).ToString("0.#")+"×"+(o.Layer.Height*100).ToString("0.#")+"%, непрозрачность "+(o.Layer.Opacity*100).ToString("0.#")+"%, X="+o.Layer.X.ToString("0.##")+", Y="+o.Layer.Y.ToString("0.##")+", время "+o.Layer.Start.ToString("0.#")+"–"+(o.Layer.End>0 ? o.Layer.End.ToString("0.#")+" с" : "до конца")+"."))
                +Environment.NewLine+"Прозрачные пиксели PNG не считаются покрытием. Вклады перекрываются и не суммируются.";
        }
        void DisposeVisualEstimate() {
            if(visualEstimateDisposed) return;
            visualEstimateDisposed=true; visualEstimateVersion++;
            visualEstimateTimer?.Stop(); visualEstimateTimer?.Dispose(); visualEstimateCancellation?.Cancel();
        }
    }
}
