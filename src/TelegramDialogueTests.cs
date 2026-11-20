using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace VideoBatch {
    public static class TelegramDialogueTests {
        static void Check(bool ok, string name) { if (!ok) throw new Exception("Telegram dialogue: " + name); }
        static readonly string Script = "{\"steps\":[{\"account_id\":\"a\",\"text\":\"Что известно о продукте?\",\"reply_to\":0},{\"account_id\":\"b\",\"text\":\"В этой AI-сценке пока нет данных о свойствах.\",\"reply_to\":1},{\"account_id\":\"a\",\"text\":\"Тогда уточним факты.\",\"reply_to\":2}]}";
        static List<TelegramDialogueParticipant> Participants() => new List<TelegramDialogueParticipant> {
            new TelegramDialogueParticipant { id = "a", name = "Аня", role = "Любопытный AI-персонаж" },
            new TelegramDialogueParticipant { id = "b", name = "Борис", role = "Осторожный AI-персонаж" }
        };
        static string Envelope(string content, string finish = "stop", string refusal = null) => new JavaScriptSerializer().Serialize(new {
            choices = new[] { new { finish_reason = finish, message = new { content, refusal } } }
        });
        static HttpResponseMessage Response(string content, HttpStatusCode status = HttpStatusCode.OK) => new HttpResponseMessage(status) {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };
        sealed class Handler : HttpMessageHandler {
            internal int Calls;
            internal string Body;
            internal Func<CancellationToken, Task<HttpResponseMessage>> Reply = ct => Task.FromResult(Response(Envelope(Script)));
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
                ct.ThrowIfCancellationRequested(); Calls++;
                Check(request.Method == HttpMethod.Post && request.RequestUri.ToString() == "https://openrouter.ai/api/v1/chat/completions", "official HTTPS POST endpoint");
                Check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "fixture-secret", "Bearer key only in header");
                Body = await request.Content.ReadAsStringAsync();
                Check(!Body.Contains("fixture-secret"), "credential absent from payload");
                return await Reply(ct);
            }
        }
        static async Task Reject(Func<Task> action, string name) {
            bool failed = false;
            try { await action(); }
            catch (ArgumentException) { failed = true; }
            catch (InvalidOperationException ex) { failed = true; Check(!ex.ToString().Contains("fixture-secret"), "error redacts credentials"); }
            Check(failed, "reject " + name);
        }
        static async Task Cancelled(Func<Task> action, string name) {
            bool cancelled = false;
            try { await action(); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, name);
        }
        public static bool RunSelfTests() => RunAsync().GetAwaiter().GetResult();
        static async Task<bool> RunAsync() {
            var participants = Participants();
            var handler = new Handler();
            using (var http = new HttpClient(handler)) {
                Func<CancellationToken, Task<List<TelegramDialogueStep>>> generate = ct => TelegramDialogue.GenerateAsync("fixture-secret", "", "gemini-2.5-flash", "Обсуждение продукта", "Только название: Пример", participants, 3, ct, http);
                var result = await generate(CancellationToken.None);
                Check(handler.Calls == 1 && result.Count == 3 && result[1].account_id == "b" && result[2].reply_to == 2, "whole response and 1-based replies preserved");
                Check(result[0].text == "Что известно о продукте?", "Russian text roundtrip");
                var body = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(handler.Body);
                Check((string)body["model"] == "google/gemini-2.5-flash", "legacy model ID normalization");
                var format = (Dictionary<string, object>)body["response_format"];
                var schemaWrapper = (Dictionary<string, object>)format["json_schema"];
                var schema = (Dictionary<string, object>)schemaWrapper["schema"];
                var steps = (Dictionary<string, object>)((Dictionary<string, object>)schema["properties"])["steps"];
                var row = (Dictionary<string, object>)steps["items"];
                var properties = (Dictionary<string, object>)row["properties"];
                var ids = (IList)((Dictionary<string, object>)properties["account_id"])["enum"];
                Check((string)format["type"] == "json_schema" && (bool)schemaWrapper["strict"] && !(bool)schema["additionalProperties"] && !(bool)row["additionalProperties"], "documented strict JSON schema");
                Check((int)steps["minItems"] == 3 && (int)steps["maxItems"] == 3 && ids.Cast<string>().SequenceEqual(new[] { "a", "b" }), "exact count and allowed account enum");
                Check(((IList)row["required"]).Cast<string>().OrderBy(x => x).SequenceEqual(new[] { "account_id", "reply_to", "text" }), "all three row fields required");
                Check((bool)((Dictionary<string, object>)body["provider"])["require_parameters"], "only structured-output capable providers");
                var messages = (IList)body["messages"];
                string system = (string)((Dictionary<string, object>)messages[0])["content"];
                string data = (string)((Dictionary<string, object>)messages[1])["content"];
                Check(system.Contains("AI-маркировку") && system.Contains("не отзывы настоящих людей") && system.Contains("НЕ подтверждают свойства") && system.Contains("условия возврата") && system.Contains("русский по умолчанию"), "transparent AI and factuality instructions");
                Check(data.Contains("product_facts") && data.Contains("Пример") && data.Contains("Любопытный"), "facts and character roles sent as data");
                TelegramDialogue.Validate(result, participants, 3);
                Check(TelegramDialogue.ParseScript(Script, participants, 3).Count == 3, "edited JSON and typed script validators reusable");

                var invalidScripts = new[] {
                    "not JSON", "null", "[]", "{}", "{\"steps\":null}", "{\"steps\":[]}",
                    Script.Replace(",\"reply_to\":0", ""),
                    Script.Replace("\"account_id\":\"a\",", ""),
                    Script.Replace("\"text\":\"Что известно о продукте?\",", ""),
                    Script.Replace("\"reply_to\":0", "\"reply_to\":\"0\""),
                    Script.Replace("\"reply_to\":0", "\"reply_to\":null"),
                    Script.Replace("\"reply_to\":0", "\"reply_to\":false"),
                    Script.Replace("\"reply_to\":0", "\"reply_to\":0.5"),
                    Script.Replace("\"reply_to\":0", "\"reply_to\":2147483648"),
                    Script.Replace("\"reply_to\":0", "\"reply_to\":1"),
                    Script.Replace("\"reply_to\":1", "\"reply_to\":2"),
                    Script.Replace("\"reply_to\":2", "\"reply_to\":-1"),
                    Script.Replace("\"account_id\":\"b\"", "\"account_id\":\"outsider\""),
                    Script.Replace("\"account_id\":\"b\"", "\"account_id\":\"a\""),
                    Script.Replace("Что известно о продукте?", "   "),
                    Script.Replace("Что известно о продукте?", "<b>Привет</b>"),
                    Script.Replace("Что известно о продукте?", "```json```"),
                    Script.Replace("\"reply_to\":0", "\"reply_to\":0,\"extra\":true"),
                    Script.Replace("{\"steps\":", "{\"extra\":true,\"steps\":")
                };
                foreach (string script in invalidScripts) {
                    handler.Reply = ct => Task.FromResult(Response(Envelope(script)));
                    int before = handler.Calls;
                    await Reject(async () => { await generate(CancellationToken.None); }, "invalid generated script");
                    Check(handler.Calls == before + 1, "invalid whole batch never retried");
                }
                foreach (string raw in new[] { "broken", "{}", "{\"choices\":[]}", "{\"error\":{\"message\":\"fixture-secret\"}}", Envelope(Script, "length"), Envelope(Script, "content_filter"), Envelope(Script, "stop", "refused"), Envelope(null) }) {
                    handler.Reply = ct => Task.FromResult(Response(raw));
                    int before = handler.Calls;
                    await Reject(async () => { await generate(CancellationToken.None); }, "invalid envelope or incomplete/refused result");
                    Check(handler.Calls == before + 1, "no envelope retries");
                }
                foreach (var status in new[] { HttpStatusCode.Redirect, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, (HttpStatusCode)429, HttpStatusCode.BadGateway }) {
                    handler.Reply = ct => Task.FromResult(Response("{\"error\":{\"message\":\"fixture-secret denied\"}}", status));
                    int before = handler.Calls; bool failed = false;
                    try { await generate(CancellationToken.None); }
                    catch (InvalidOperationException ex) { failed = true; Check(ex.Message.Contains("HTTP " + (int)status) && !ex.ToString().Contains("fixture-secret"), "visible redacted HTTP status"); }
                    Check(failed && handler.Calls == before + 1, "HTTP failures are not retried");
                }
                int noNetwork = handler.Calls;
                await Reject(async () => { await TelegramDialogue.GenerateAsync(" ", "", "model", "topic", "", participants, 3, CancellationToken.None, http); }, "missing key before network");
                // Public path also rejects before creating a client, regardless of invalid proxy.
                await Reject(async () => { await TelegramDialogue.GenerateAsync(null, "socks5://invalid", "model", "topic", "", participants, 3, CancellationToken.None); }, "public missing key");
                foreach (int count in new[] { 0, 1, 101 })
                    await Reject(async () => { await TelegramDialogue.GenerateAsync("fixture-secret", "", "model", "topic", "", participants, count, CancellationToken.None, http); }, "count bounds");
                await Reject(async () => { await TelegramDialogue.GenerateAsync("fixture-secret", "", "", "topic", "", participants, 3, CancellationToken.None, http); }, "missing model");
                await Reject(async () => { await TelegramDialogue.GenerateAsync("fixture-secret", "", "model", " ", "", participants, 3, CancellationToken.None, http); }, "missing topic");
                var duplicate = Participants(); duplicate[1].id = duplicate[0].id;
                await Reject(async () => { await TelegramDialogue.GenerateAsync("fixture-secret", "", "model", "topic", "", duplicate, 3, CancellationToken.None, http); }, "duplicate participant IDs");
                var tooMany = Participants(); tooMany.Add(new TelegramDialogueParticipant { id = "c", name = "C", role = "AI" });
                await Reject(async () => { await TelegramDialogue.GenerateAsync("fixture-secret", "", "model", "topic", "", tooMany, 2, CancellationToken.None, http); }, "not enough messages for participants");
                using (var cancel = new CancellationTokenSource()) {
                    cancel.Cancel();
                    await Cancelled(async () => { await generate(cancel.Token); }, "pre-cancelled call");
                }
                Check(handler.Calls == noNetwork, "invalid input and pre-cancellation do not contact transport");
                using (var cancel = new CancellationTokenSource()) {
                    var entered = new TaskCompletionSource<bool>();
                    handler.Reply = async ct => { entered.SetResult(true); await Task.Delay(Timeout.Infinite, ct); return Response(Envelope(Script)); };
                    var pending = generate(cancel.Token); await entered.Task; cancel.Cancel();
                    await Cancelled(async () => { await pending; }, "in-flight cancellation propagated");
                    Check(handler.Calls == noNetwork + 1, "cancellation is not retried");
                }
                var edited = TelegramDialogue.ParseScript(Script, participants, 3); edited[2].reply_to = 3;
                await Reject(() => { TelegramDialogue.Validate(edited, participants, 3); return Task.FromResult(true); }, "invalid edited typed script");
            }
            Console.WriteLine("PASS Telegram dialogue: mock OpenRouter schema, full batch validation, known unique participants, prior replies, missing keys, HTTP errors, no retry, missing key and cancellation. No real network used.");
            return true;
        }
    }
}
