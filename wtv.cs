// ============================================================================
//  W.TV chat bridge for RutonyChat
// ----------------------------------------------------------------------------
//  RutonyChat has no native W.TV support, so this autonomous script pulls the
//  channel chat and injects every line into the chat window via
//  RutonyBot.SayToWindow(user, text).
//
//  Transport: REST polling, read-only.
//
//  The W.TV realtime endpoint (wss://ws.w.tv/connection/websocket, Centrifugo)
//  rejects .NET's ClientWebSocket with HTTP 403 - Cloudflare bot management
//  rejects the TLS fingerprint, not the headers. The REST API is not blocked,
//  so this script polls the message history instead of holding a socket.
//
//  This script is strictly READ-ONLY. It never joins the chat, never opens a
//  realtime socket and never subscribes to anything. It just asks for the
//  public message list of one channel and prints it into RutonyChat.
//
//  Flow (2 requests, both anonymous):
//    1) GET  profiles-service.w.tv/api/v1/profiles/by-nickname/{nick}?platform=web
//         -> { profile: { userId } }                    (userId IS the chatId)
//    2) GET  chats-service.w.tv/api/v1/chats/{chatId}/messages?limit=&platform=web
//         -> { messages: [ { messageId, type, content,
//                            sender:{ userId, nickname, tags[] },
//                            createdAt } ] }
//
//  Everything is wrapped so a bad response can never take the chat client
//  down. Diagnostics go to scripts\wtv.log.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RutonyChat {
    public class Script {

        // ------------------------------ CONFIG ------------------------------
        // W.TV channel nickname, i.e. https://w.tv/mextrim
        private const string DEFAULT_NICKNAME = "mextrim";
        // Seconds between polls. Keep it >= 2 so we do not hammer the API.
        private const int POLL_SECONDS = 3;
        // How many recent messages to fetch each poll.
        private const int PAGE_LIMIT = 30;
        // Cap on remembered message ids (dedupe buffer).
        private const int SEEN_LIMIT = 400;
        // Invisible prefix on the nickname so chat themes can tag these lines.
        // U+200D is a zero-width joiner: invisible on screen, but detectable.
        // Bundled themes (minimal-chat, neon-cyber) strip it and show the
        // official W.TV logo instead. Other themes will just print the
        // characters as part of the nickname.
        private const string WTV_MARK = "\u200Dwtv\u200D";

        // ---- sending (opt-in) ------------------------------------------------
        // W.TV refuses anonymous writes: POST /messages answers 400
        // BAD_REQUEST_ERROR for every payload shape tried, and the site itself
        // says "log in to write in chat". Posting therefore needs the session
        // token of the account that should appear as the author. Supply it in
        // the parameters:
        //   {"nickname":"mextrim","token":"...","forwardPrefix":"!wtv"}
        // With no token the bridge stays strictly read-only.
        private const string DEFAULT_FORWARD_PREFIX = "!wtv";
        private string authToken;
        private string forwardPrefix = DEFAULT_FORWARD_PREFIX;
        // ---------------------------------------------------------------------

        private static readonly string LogPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts", "wtv.log");

        private const string PROFILE_API = "https://profiles-service.w.tv/api/v1";
        private const string CHAT_API = "https://chats-service.w.tv/api/v1";
        private const string ORIGIN = "https://w.tv";
        private const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36";

        private string nickname = DEFAULT_NICKNAME;

        private HttpClient http;
        private CancellationTokenSource cts;
        private Task worker;
        private volatile bool running;

        private string chatId;

        // message ids already shown - the API returns newest-first, so we track
        // what we have seen to only emit genuinely new lines.
        private readonly HashSet<string> seen = new HashSet<string>();
        private readonly Queue<string> seenOrder = new Queue<string>();

        // Lines waiting for the UI thread.
        private readonly Queue<string[]> pending = new Queue<string[]>();
        private int pendingCount;

        // ---------------------------------------------------------------------
        //  RutonyChat lifecycle
        // ---------------------------------------------------------------------

        public void InitParams(string param) {
            ApplyParams(param);
            Log("---- wtv bridge initialised (read-only, REST polling) ----");
            Log("nickname: " + nickname + ", interval: " + POLL_SECONDS + "s");
            Log("no join, no realtime socket, no subscription");
            Log("posting : " + (authToken == null
                                  ? "disabled (no token - read-only)"
                                  : "enabled, trigger '" + forwardPrefix + " <text>'"));

            Stop();
            lock (pending) { pending.Clear(); pendingCount = 0; }
            lock (seen) { seen.Clear(); seenOrder.Clear(); }

            running = true;
            cts = new CancellationTokenSource();
            worker = Task.Run(() => RunAsync(cts.Token));
        }

        public void Closing() {
            Log("wtv bridge closing");
            Stop();
        }

        // Runs on the chat/UI thread - our safe dispatch point.
        public void NewMessage(string site, string name, string text, bool system) {
            Drain();
            if (!system) TryForward(site, name, text);
        }

        public void NewAlert(string site, string typeEvent, string subplan, string name,
                             string text, float donate, string currency, int qty) {
            Drain();
        }

        private void Drain() {
            string[] item;
            int guard = 0;
            lock (pending) {
                while (pending.Count > 0 && guard++ < 100) {
                    item = pending.Dequeue();
                    pendingCount--;
                    Show(item[0], item[1]);
                }
            }
        }

        private void Show(string user, string text) {
            try {
                // Invisible marker so a chat theme can recognise these lines and
                // show a platform icon (SayToWindow carries no SiteEnum).
                RutonyBot.SayToWindow(WTV_MARK + user, text);
            } catch (Exception ex) {
                Log("!! SayToWindow failed: " + ex.Message);
            }
        }

        private void ApplyParams(string param) {
            if (string.IsNullOrWhiteSpace(param)) return;
            try {
                var j = JToken.Parse(param);
                if (j.Type != JTokenType.Object) return;
                var t = j["nickname"];
                if (t != null && t.Type != JTokenType.Null && !string.IsNullOrWhiteSpace(t.ToString()))
                    nickname = t.ToString().Trim().ToLowerInvariant();

                var tk = j["token"];
                authToken = (tk == null || tk.Type == JTokenType.Null) ? null : tk.ToString().Trim();
                if (string.IsNullOrEmpty(authToken)) authToken = null;

                var fp = j["forwardPrefix"];
                forwardPrefix = (fp == null || fp.Type == JTokenType.Null || string.IsNullOrWhiteSpace(fp.ToString()))
                                ? DEFAULT_FORWARD_PREFIX : fp.ToString().Trim();
            } catch (Exception ex) {
                Log("!! bad parameters: " + ex.Message);
            }
        }

        // ---------------------------------------------------------------------
        //  Optional: post messages to the W.TV chat
        // ---------------------------------------------------------------------

        // Triggered by a "!wtv <text>" line in the combined chat. Everything
        // else is ignored, and without an auth token nothing is sent at all.
        private void TryForward(string site, string name, string text) {
            if (authToken == null || string.IsNullOrEmpty(chatId)) return;
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!text.StartsWith(forwardPrefix, StringComparison.OrdinalIgnoreCase)) return;

            var body = text.Substring(forwardPrefix.Length).Trim();
            if (body.Length == 0) return;
            if (body.Length > 300) body = body.Substring(0, 300);

            Task.Run(async () => {
                try {
                    await SendAsync(body);
                } catch (Exception ex) {
                    Log("!! send failed: " + ex.Message);
                }
            });
        }

        private async Task SendAsync(string message) {
            var url = string.Format("{0}/chats/{1}/messages?platform=web", CHAT_API, chatId);

            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + authToken);

            var payload = new JObject {
                { "content", message },
                { "replyToMessageId", JValue.CreateNull() }
            };
            req.Content = new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None),
                                            Encoding.UTF8, "application/json");

            var resp = await http.SendAsync(req).ConfigureAwait(false);
            var res = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (resp.IsSuccessStatusCode) {
                Log("sent: " + Truncate(message, 60));
            } else {
                Log("!! send HTTP " + (int)resp.StatusCode + ": " + Truncate(res, 160));
            }
        }

        // ---------------------------------------------------------------------
        //  Main loop
        // ---------------------------------------------------------------------

        private async Task RunAsync(CancellationToken token) {
            int attempt = 0;
            bool ready = false;

            while (!token.IsCancellationRequested && running) {
                try {
                    if (!ready) {
                        http = NewHttp();
                        await ResolveAsync(token);
                        await PrimeSeenAsync(token);
                        ready = true;
                        attempt = 0;
                    }

                    await PollOnceAsync(token);

                    var wait = TimeSpan.FromSeconds(POLL_SECONDS);
                    await Task.Delay(wait, token);
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    Log("!! " + ex.GetType().Name + ": " + ex.Message);
                    ready = false;
                    attempt++;
                    var waitMs = Math.Min(30000, 1500 * Math.Min(20, attempt));
                    try { await Task.Delay(waitMs, token); } catch { break; }
                }
            }

            Log("worker exited");
        }

        private static HttpClient NewHttp() {
            var h = new HttpClient(new HttpClientHandler {
                CookieContainer = new CookieContainer(),
                UseCookies = true,
                AllowAutoRedirect = true,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                         System.Net.DecompressionMethods.Deflate
            });
            h.DefaultRequestHeaders.UserAgent.ParseAdd(UA);
            h.DefaultRequestHeaders.Add("Origin", ORIGIN);
            h.DefaultRequestHeaders.Add("Referer", ORIGIN + "/");
            return h;
        }

        // 1) nickname -> chatId
        private async Task ResolveAsync(CancellationToken token) {
            var url = string.Format("{0}/profiles/by-nickname/{1}?platform=web",
                                   PROFILE_API, Uri.EscapeDataString(nickname));
            Log("resolving " + url);

            var json = await http.GetStringAsync(url).ConfigureAwait(false);
            var root = JObject.Parse(json);

            chatId = (string)root["profile"]?["userId"];
            if (string.IsNullOrEmpty(chatId))
                throw new Exception("no userId for nickname '" + nickname + "'");

            Log("chatId : " + chatId);
        }

        // 2) one poll of the public message list (read-only, no join, no subscribe)
        private async Task PollOnceAsync(CancellationToken token) {
            var url = string.Format("{0}/chats/{1}/messages?limit={2}&platform=web",
                                   CHAT_API, chatId, PAGE_LIMIT);
            var json = await http.GetStringAsync(url).ConfigureAwait(false);
            HandleMessages(json);
        }

        private async Task PrimeSeenAsync(CancellationToken token) {
            var url = string.Format("{0}/chats/{1}/messages?limit={2}&platform=web",
                                   CHAT_API, chatId, PAGE_LIMIT);
            try
            {
                var json = await http.GetStringAsync(url).ConfigureAwait(false);
                var root = JObject.Parse(json);
                var arr = (JArray)root["messages"];
                if (arr != null)
                {
                    lock (seen)
                    {
                        foreach (var m in arr)
                        {
                            var id = (string)m["messageId"];
                            if (!string.IsNullOrEmpty(id)) Remember(id);
                        }
                    }
                    Log("primed with " + arr.Count + " existing messages");
                }
            }
            catch (Exception ex) { Log("prime failed: " + ex.Message); }
        }

        private void HandleMessages(string json) {
            JObject root;
            try { root = JObject.Parse(json); }
            catch { return; }

            var arr = root["messages"] as JArray;
            if (arr == null) return;

            // API returns newest first; emit oldest-first so chat reads in order.
            var fresh = new List<JObject>();
            lock (seen)
            {
                for (int i = arr.Count - 1; i >= 0; i--)
                {
                    var m = arr[i] as JObject;
                    if (m == null) continue;

                    var id = (string)m["messageId"];
                    if (string.IsNullOrEmpty(id) || seen.Contains(id)) continue;

                    var type = (string)m["type"];
                    if (!string.IsNullOrEmpty(type) && type != "MESSAGE") continue;

                    var sender = m["sender"] as JObject;
                    var user = sender == null ? null : (string)sender["nickname"];
                    var text = (string)m["content"];

                    if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(text)) continue;

                    Remember(id);
                    fresh.Add(m);
                }
            }

            if (fresh.Count == 0) return;

            foreach (var m in fresh)
            {
                var user = (string)m["sender"]?["nickname"];
                var text = (string)m["content"];
                lock (pending)
                {
                    pending.Enqueue(new[] { user, text });
                    pendingCount++;
                    while (pending.Count > 200) { pending.Dequeue(); pendingCount--; }
                }
            }

            Drain();
            Log("+" + fresh.Count + " message(s)");
        }

        private void Remember(string id) {
            if (seen.Contains(id)) return;
            seen.Add(id);
            seenOrder.Enqueue(id);
            while (seenOrder.Count > SEEN_LIMIT && seenOrder.Count > 0)
            {
                var old = seenOrder.Dequeue();
                seen.Remove(old);
            }
        }

        private void Stop() {
            running = false;
            if (cts != null) {
                try { cts.Cancel(); } catch { }
                cts = null;
            }
            worker = null;
        }

        private static string Truncate(string s, int max) {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        private static void Log(string message) {
            try {
                File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                    Encoding.UTF8);
            } catch { }
        }
    }
}
