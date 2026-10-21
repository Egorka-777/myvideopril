using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VideoBatch {
    public static class HttpRegressionSelfTests {
        public static bool RunAll() {
            if (!ProcessingRegressionTests.RunSelfTests()) return false;
            if (!ProcessingRegressionTests.RunUiSelfTests()) return false;
            if (!RunAccountRemovalSelfTest()) return false;
            if (!HttpUploadRunner.RunParallelSerializationSelfTest(10)) return false;
            if (!ScheduleGenerator.RunSelfTests()) return false;
            if (!TikTokScheduleGenerator.RunSelfTests()) return false;
            if (!TikTokCaptionTemplates.RunSelfTests()) return false;
            if (!YouTubeDescriptionTemplates.RunSelfTests()) return false;
            if (!UploadStaging.RunSelfTests()) return false;
            if (!DolphinProfileCatalogTests.RunSelfTests()) return false;
            if (!RunLog160617Scenario()) return false;
            if (!RunYouTubeChannelUrlSelfTest()) return false;
            if (!RunDiagnosticsSelfTest()) return false;
            if (!RunThumbnailFallbackSelfTest()) return false;
            if (!RunYouTubeMetadataSelfTest()) return false;
            if (!TaskQueueWriter.RunConcurrentWriteSelfTest(40, 25)) return false;
            return RunWorkerPoolScenario();
        }

        public static bool RunAccountRemovalSelfTest() {
            string oldRoot = Store.Root;
            string temp = Path.Combine(Path.GetTempPath(), "videobatch-delete-" + Guid.NewGuid().ToString("N"));
            try {
                Store.Root = temp;
                var first = new YouTubeChannel { ChannelId="a", Name="A", ProfileId="123", Market="RU" };
                var sibling = new YouTubeChannel { ChannelId="b", Name="B", ProfileId="123", Market="RU" };
                var other = new YouTubeChannel { ChannelId="c", Name="C", ProfileId="456", Market="EN" };
                var tk = new TikTokAccount { Name="A", ProfileId="123", Market="RU" };
                var prefs = new Preferences { YouTubeChannels=new List<YouTubeChannel>{first,sibling,other}, TikTokAccounts=new List<TikTokAccount>{tk} };
                var before = prefs.YouTubeChannels;
                try { AccountRemoval.Remove(prefs, new[]{first}, new[]{tk}, p=>{throw new IOException("fixture save failure");}); return false; }
                catch (IOException) { }
                if (!ReferenceEquals(before,prefs.YouTubeChannels) || !prefs.TikTokAccounts.Contains(tk) || prefs.TikTokSyncExcludedProfiles.Count!=0) return false;
                AccountRemoval.Remove(prefs, new[]{first}, new[]{tk});
                if (prefs.YouTubeChannels.Count!=2 || !prefs.YouTubeChannels.Contains(sibling)) return false;
                var reloaded = Store.Load();
                if (reloaded.YouTubeChannels.Exists(x=>x.ChannelId=="a")) return false;
                if (reloaded.TikTokAccounts.Exists(x=>x.ProfileId=="123"&&x.Market=="RU")) return false;
                if (!reloaded.TikTokAccounts.Exists(x=>x.ProfileId=="456"&&x.Market=="EN")) return false;
                // Explicit manual re-add works even when auto-import was suppressed.
                reloaded.TikTokAccounts.Add(new TikTokAccount { ProfileId="123", Market="RU", Name="manual" });
                Store.Save(reloaded);
                return Store.Load().TikTokAccounts.Exists(x=>x.Name=="manual"&&x.ProfileId=="123");
            } finally {
                Store.Root=oldRoot;
                if (Directory.Exists(temp)) Directory.Delete(temp,true);
            }
        }

        public static bool RunDiagnosticsSelfTest() {
            string previous=Store.Root,temp=Path.Combine(Path.GetTempPath(),"vb-diagnostics-"+Guid.NewGuid().ToString("N"));
            try {
                Store.Root=temp;
                Parallel.For(0,25,i=>AppDiagnostics.Write(i%2==0?"YouTube":"TikTok","profile-"+i,"job-"+i,"thumbnail_warning","https://name:password@example.com/edit?token=secret", "warning"));
                var lines=Directory.GetFiles(AppDiagnostics.Folder,"app-*.jsonl");
                int count=0;
                foreach(var file in lines)foreach(var line in File.ReadLines(file)) {
                    var item=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string,string>>(line);
                    if(!item.ContainsKey("task")||!item.ContainsKey("account")||item["level"]!="warning"||item["message"].Contains("password")||item["message"].Contains("secret"))return false;
                    count++;
                }
                if(count!=25)return false;
                using(var form=new System.Windows.Forms.Form())using(var panel=new LogsPanel(new Preferences())) {
                    form.Controls.Add(panel);form.Show();System.Windows.Forms.Application.DoEvents();
                    Func<string,object> field=name=>typeof(LogsPanel).GetField(name,System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(panel);
                    var list=(System.Windows.Forms.ListBox)field("logList");
                    ((System.Windows.Forms.ComboBox)field("levelFilter")).SelectedItem="error";panel.RefreshData();if(list.Items.Count!=0)return false;
                    ((System.Windows.Forms.ComboBox)field("levelFilter")).SelectedItem="warning";
                    ((System.Windows.Forms.TextBox)field("accountFilter")).Text="profile-24";
                    ((System.Windows.Forms.ComboBox)field("platformFilter")).SelectedItem="YouTube";
                    ((System.Windows.Forms.TextBox)field("taskFilter")).Text="job-24";panel.RefreshData();if(list.Items.Count!=1)return false;
                    if(!list.Items[0].ToString().Contains("thumbnail_warning"))return false;
                    form.Close();
                }
                return true;
            }finally {Store.Root=previous;if(Directory.Exists(temp))Directory.Delete(temp,true);}
        }

        public static bool RunThumbnailFallbackSelfTest() {
            var channel=new YouTubeChannel {Thumbnail="channel.png",Items=new List<YouTubeItem>{new YouTubeItem(),new YouTubeItem(),new YouTubeItem {Thumbnail="own.webp"}}};
            if(YouTubeThumbnailSync.ForItem(channel,channel.Items[0],0)!="channel.png")return false;
            if(YouTubeThumbnailSync.ForItem(channel,channel.Items[1],1)!="channel.png")return false;
            if(YouTubeThumbnailSync.ForItem(channel,channel.Items[2],2)!="own.webp")return false;
            return true;
        }

        public static bool RunYouTubeMetadataSelfTest() {
            var a = YouTubeMetadata.ShuffleTags("527453777", YouTubeMetadata.AllRuTags);
            var b = YouTubeMetadata.ShuffleTags("797494773", YouTubeMetadata.AllRuTags);
            if (a.Length != YouTubeMetadata.AllRuTags.Length || b.Length != YouTubeMetadata.AllRuTags.Length) return false;
            if (string.Join("|", a) == string.Join("|", b)) return false;
            var same = YouTubeMetadata.ShuffleTags("527453777", YouTubeMetadata.AllRuTags);
            if (string.Join("|", a) != string.Join("|", same)) return false;
            string baseText = "Текст описания\n\nBinodex обзор • трейдинг\n\n#BinoDex #трейдинг";
            string prepared = YouTubeMetadata.AppendTagsFooter(baseText, a);
            if (!prepared.Contains(YouTubeMetadata.TagsFooterPrefix)) return false;
            if (prepared.Contains("#BinoDex")) return false;
            if (!prepared.StartsWith("Текст описания")) return false;
            return true;
        }

        public static bool RunYouTubeChannelUrlSelfTest() {
            const string good = "https://www.youtube.com/@OMONXONFAN55/videos";
            const string broken = "https://www.youtube.com@OMONXONFAN55/videos";
            if (!Store.IsValidYouTubeChannelUrl(good)) return false;
            if (!Store.IsValidYouTubeChannelUrl(broken)) return false;
            if (Store.ResolveYouTubeChannelUrl(broken, "OMONXONFAN @OMONXONFAN55", good) != good) return false;
            if (Store.ResolveYouTubeChannelUrl("", "OMONXONFAN @OMONXONFAN55", null) != good) return false;
            if (Store.ResolveYouTubeChannelUrl(broken, "OMONXONFAN @OMONXONFAN55", null) != good) return false;
            return true;
        }

        public static bool RunLog160617Scenario() {
            var prefs = new Preferences { YouTubeScheduleLeadMinutes = 30 };
            var batches = BuildSixAccountBatchesTooSoon();
            if (!AnyScheduleTooSoon(batches, prefs)) return false;
            if (!ScheduleGenerator.EnsureValidYouTubeSchedule(batches, prefs)) return false;
            int lead = ScheduleGenerator.ResolveLeadMinutes(prefs);
            DateTime minAt = DateTime.Now.AddMinutes(lead - 1);
            DateTime apiMin = DateTime.Now.AddMinutes(ScheduleGenerator.YouTubeApiMinLeadMinutes);
            foreach (var batch in batches) {
                foreach (var it in batch.Items) {
                    if (!ScheduleGenerator.TryParseSlot(it.ScheduleDate, it.ScheduleTime, out var at)) return false;
                    if (at < minAt) return false;
                    if (at <= apiMin) return false;
                }
            }
            if (!HttpUploadRunner.RunParallelSerializationSelfTest(6)) return false;
            return true;
        }

        /// <summary>10 account batches, max 3 concurrent, unique job paths, queue writer, pool resilience.</summary>
        public static string LastPoolError = "";

        public static bool RunWorkerPoolScenario() {
            try {
            if (HttpWorkerSettings.ResolveWorkerCount(new Preferences()) != 3) return false;
            if (HttpWorkerSettings.ResolveWorkerCount(new Preferences { HttpWorkerPreset = "safe" }) != 1) return false;
            if (HttpWorkerSettings.ResolveWorkerCount(new Preferences { HttpWorkerPreset = "fast" }) != 5) return false;
            if (HttpWorkerSettings.ResolveWorkerCount(new Preferences { HttpWorkerPreset = "custom", HttpWorkerCustomCount = 7 }) != 7) return false;

            var batches = BuildTenAccountBatches();
            int maxConcurrent = 0;
            int peak = 0;
            var jobPaths = new ConcurrentBag<string>();
            var active = 0;
            using (var pool = new HttpWorkerPool()) {
                pool.RunAsync(batches, 3, false, async (batch, metrics, ct) => {
                    int now = Interlocked.Increment(ref active);
                    int observed;
                    do {
                        observed = maxConcurrent;
                        if (now <= observed) break;
                    } while (Interlocked.CompareExchange(ref maxConcurrent, now, observed) != observed);
                    if (now > peak) peak = now;
                    metrics.MarkStarted();
                    string runId = Guid.NewGuid().ToString("N");
                    string path = Path.Combine(Store.Root, "jobs", "http-" + runId + ".json");
                    jobPaths.Add(path);
                    await Task.Delay(80, ct).ConfigureAwait(false);
                    metrics.OnStage("dolphin");
                    metrics.OnStage("upload");
                    metrics.Finish("success");
                    Interlocked.Decrement(ref active);
                }, CancellationToken.None).GetAwaiter().GetResult();
                if (pool.Metrics.Processed != 10) return false;
            }
            if (peak > 3 || maxConcurrent > 3) return false;
            if (jobPaths.Count != 10) return false;
            var unique = new HashSet<string>(jobPaths, StringComparer.OrdinalIgnoreCase);
            if (unique.Count != 10) return false;

            // One failing worker must not stop the pool
            int completed = 0;
            using (var pool2 = new HttpWorkerPool()) {
                pool2.RunAsync(batches, 3, false, async (batch, metrics, ct) => {
                    if (batch.ProfileId == "pid-3") throw new Exception("simulated worker failure");
                    Interlocked.Increment(ref completed);
                    metrics.Finish("success");
                    await Task.Delay(10, ct).ConfigureAwait(false);
                }, CancellationToken.None).GetAwaiter().GetResult();
            }
            if (completed != 9) return false;
            return true;
            } catch (Exception ex) {
                LastPoolError = ex.ToString();
                return false;
            }
        }

        static List<PreparedProfileBatch> BuildTenAccountBatches() {
            var list = new List<PreparedProfileBatch>();
            var at = DateTime.Now.AddHours(1);
            for (int i = 0; i < 10; i++) {
                list.Add(new PreparedProfileBatch {
                    ProfileId = "pid-" + (i + 1),
                    Channel = new YouTubeChannel {
                        Name = "Account " + (i + 1),
                        ProfileId = "pid-" + (i + 1),
                        ExpectedIp = "1.2.3." + (i + 1),
                        Kind = i % 2 == 0 ? "long" : "shorts"
                    },
                    Items = new List<PreparedUploadItem> {
                        new PreparedUploadItem {
                            UploadTitle = "Title " + (i + 1),
                            StagedVideo = "C:\\VideoBatch\\Upload\\pid-" + (i + 1) + "\\video.mp4",
                            ScheduleDate = at.ToString("yyyy-MM-dd"),
                            ScheduleTime = at.ToString("HH:mm"),
                            Source = new YouTubeItem { Thumbnail = "C:\\VideoBatch\\thumb-" + (i + 1) + ".jpg" }
                        }
                    }
                });
            }
            return list;
        }

        static List<PreparedProfileBatch> BuildSixAccountBatchesTooSoon() {
            var list = new List<PreparedProfileBatch>();
            string[] names = {
                "Трейдинг для избранных (MY2) @lotusninja666",
                "Lucky Tom | Smart Trading @filmrezzzka",
                "Блог Трейдера - Предпринимателя @traderprdp",
                "Trader Max | Обучение трейдингу (LT2) @tepluy_veter",
                "Школа Трейдинга (ТП2) @marinhoffer",
                "Торговец из Binodex (Бинодекс)"
            };
            var tooSoon = DateTime.Now.AddMinutes(5);
            for (int i = 0; i < names.Length; i++) {
                list.Add(new PreparedProfileBatch {
                    ProfileId = "pid-" + (i + 1),
                    Channel = new YouTubeChannel { Name = names[i], ProfileId = "pid-" + (i + 1), ExpectedIp = "1.2.3." + (i + 1) },
                    Items = new List<PreparedUploadItem> {
                        new PreparedUploadItem {
                            UploadTitle = "Title " + (i + 1),
                            StagedVideo = "C:\\VideoBatch\\Upload\\pid-" + (i + 1) + "\\video.mp4",
                            ScheduleDate = tooSoon.ToString("yyyy-MM-dd"),
                            ScheduleTime = tooSoon.ToString("HH:mm")
                        }
                    }
                });
            }
            return list;
        }

        static bool AnyScheduleTooSoon(IList<PreparedProfileBatch> batches, Preferences prefs) {
            DateTime floor = ScheduleGenerator.ScheduleFloor(prefs);
            foreach (var batch in batches) {
                foreach (var it in batch.Items ?? new List<PreparedUploadItem>()) {
                    if (!ScheduleGenerator.TryParseSlot(it.ScheduleDate, it.ScheduleTime, out var at)) continue;
                    if (at < floor) return true;
                }
            }
            return false;
        }
    }
}
