using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BetterRawAccel.Core
{
    /// <summary>
    /// A small Model Context Protocol server (Streamable HTTP, JSON responses) that lives inside the GUI process.
    /// It only listens on the loopback address. Tool calls are handed to the window, so every change is visible there.
    /// </summary>
    public sealed class McpServer : IDisposable
    {
        static readonly string[] Versions = { "2025-06-18", "2025-03-26", "2024-11-05" };
        const int MaxBody = 1024 * 1024;

        readonly Func<IEnumerable<ToolDef>> tools;
        readonly Func<string, JObject, JToken> invoke;
        HttpListener listener;
        Thread thread;
        int port;

        public McpServer(Func<IEnumerable<ToolDef>> tools, Func<string, JObject, JToken> invoke)
        {
            this.tools = tools;
            this.invoke = invoke;
        }

        public bool Running => listener != null && listener.IsListening;
        public string Url => $"http://127.0.0.1:{port}/mcp";
        public string Error { get; private set; }
        public int Calls { get; private set; }
        public string LastTool { get; private set; }
        public DateTime? LastCall { get; private set; }

        public void Start(int port)
        {
            Stop();
            this.port = port;
            Error = null;
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add($"http://127.0.0.1:{port}/");
                l.Start();
                listener = l;
                thread = new Thread(Loop) { IsBackground = true, Name = "mcp" };
                thread.Start(l);
            }
            catch (Exception e)
            {
                listener = null;
                Error = e is HttpListenerException ? $"Port {port} could not be opened ({e.Message.Trim()})" : e.Message;
                Log.Error("mcp start", e);
            }
        }

        public void Stop()
        {
            var l = listener;
            listener = null;
            if (l == null) return;
            try { l.Stop(); l.Close(); } catch { }
        }

        public void Dispose() => Stop();

        void Loop(object state)
        {
            var l = (HttpListener)state;
            while (l.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = l.GetContext(); }
                catch { break; }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { Handle(ctx); }
                    catch (Exception e)
                    {
                        Log.Error("mcp request", e);
                        try { ctx.Response.Abort(); } catch { }
                    }
                });
            }
        }

        static bool LocalName(string host) =>
            host == "127.0.0.1" || host == "localhost" || host == "[::1]";

        void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;

            // DNS-rebinding protection: the Host must be loopback, and a browser page from another origin is refused.
            var hostName = (req.UserHostName ?? "").Split(':')[0];
            if (req.UserHostName != null && req.UserHostName.StartsWith("[")) hostName = req.UserHostName.Substring(0, req.UserHostName.IndexOf(']') + 1);
            var origin = req.Headers["Origin"];
            bool originOk = string.IsNullOrEmpty(origin) ||
                (Uri.TryCreate(origin, UriKind.Absolute, out var o) && LocalName(o.Host.Contains(":") ? "[" + o.Host + "]" : o.Host));
            if (!LocalName(hostName) || !originOk || !req.IsLocal)
            {
                Send(res, 403, "{\"error\":\"forbidden\"}");
                return;
            }

            var path = req.Url.AbsolutePath.TrimEnd('/');
            if (path != "/mcp" && path != "")
            {
                Send(res, 404, "{\"error\":\"not found\"}");
                return;
            }
            if (req.HttpMethod != "POST")
            {
                res.AddHeader("Allow", "POST");
                Send(res, 405, "{\"error\":\"use POST\"}");
                return;
            }
            if (req.ContentLength64 > MaxBody)
            {
                Send(res, 413, "{\"error\":\"too large\"}");
                return;
            }

            string body;
            using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = r.ReadToEnd();

            JToken parsed;
            try { parsed = JToken.Parse(body); }
            catch
            {
                Send(res, 400, Rpc(null, null, RpcError(-32700, "Parse error")).ToString(Formatting.None));
                return;
            }

            if (parsed is JArray batch)
            {
                var replies = new JArray(batch.OfType<JObject>().Select(HandleMessage).Where(x => x != null));
                if (replies.Count == 0) Send(res, 202, null);
                else Send(res, 200, replies.ToString(Formatting.None));
            }
            else if (parsed is JObject msg)
            {
                var reply = HandleMessage(msg);
                if (reply == null) Send(res, 202, null);
                else Send(res, 200, reply.ToString(Formatting.None));
            }
            else Send(res, 400, Rpc(null, null, RpcError(-32600, "Invalid request")).ToString(Formatting.None));
        }

        static void Send(HttpListenerResponse res, int status, string json)
        {
            res.StatusCode = status;
            if (json == null) { res.Close(); return; }
            var bytes = Encoding.UTF8.GetBytes(json);
            res.ContentType = "application/json";
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.Close();
        }

        static JObject RpcError(int code, string message) => new JObject { ["code"] = code, ["message"] = message };

        static JObject Rpc(JToken id, JToken result, JObject error)
        {
            var o = new JObject { ["jsonrpc"] = "2.0", ["id"] = id ?? JValue.CreateNull() };
            if (error != null) o["error"] = error; else o["result"] = result ?? new JObject();
            return o;
        }

        JObject HandleMessage(JObject msg)
        {
            var id = msg["id"];
            var method = (string)msg["method"];
            bool notification = id == null;
            if (method == null) return notification ? null : Rpc(id, null, RpcError(-32600, "Invalid request"));
            var p = msg["params"] as JObject ?? new JObject();

            try
            {
                switch (method)
                {
                    case "initialize":
                        {
                            var asked = (string)p["protocolVersion"];
                            return Rpc(id, new JObject
                            {
                                ["protocolVersion"] = Versions.Contains(asked) ? asked : Versions[0],
                                ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
                                ["serverInfo"] = new JObject { ["name"] = "better-rawaccel", ["title"] = AppInfo.Name, ["version"] = AppInfo.Version.ToString(3) },
                                ["instructions"] =
                                    "Controls the Better Raw Accel window, a front end for the Raw Accel mouse acceleration driver. " +
                                    "Edits appear live in the window but do not reach the driver until you call apply. " +
                                    "Call list_modes before changing parameters, and capture_window to see the result. " +
                                    "Speeds are in counts per millisecond; sensitivity is a multiplier where 1 means unchanged.",
                            }, null);
                        }
                    case "ping":
                        return Rpc(id, new JObject(), null);
                    case "tools/list":
                        return Rpc(id, new JObject
                        {
                            ["tools"] = new JArray(tools().Where(t => !t.UiOnly).Select(t => new JObject
                            {
                                ["name"] = t.Name,
                                ["description"] = t.Description,
                                ["inputSchema"] = t.Schema,
                            })),
                        }, null);
                    case "tools/call":
                        return Rpc(id, CallTool((string)p["name"], p["arguments"] as JObject), null);
                    case "resources/list":
                        return Rpc(id, new JObject { ["resources"] = new JArray() }, null);
                    case "resources/templates/list":
                        return Rpc(id, new JObject { ["resourceTemplates"] = new JArray() }, null);
                    case "prompts/list":
                        return Rpc(id, new JObject { ["prompts"] = new JArray() }, null);
                    default:
                        if (notification || method.StartsWith("notifications/")) return null;
                        return Rpc(id, null, RpcError(-32601, "Method not found: " + method));
                }
            }
            catch (Exception e)
            {
                Log.Error("mcp " + method, e);
                return notification ? null : Rpc(id, null, RpcError(-32603, e.Message));
            }
        }

        JObject CallTool(string name, JObject args)
        {
            var tool = tools().FirstOrDefault(t => t.Name == name && !t.UiOnly);
            if (tool == null) throw new InvalidOperationException("Unknown tool: " + name);

            Calls++;
            LastTool = name;
            LastCall = DateTime.Now;
            try
            {
                var result = invoke(name, args ?? new JObject());
                if (result is JObject ro && ro["__image"] != null)
                {
                    return new JObject
                    {
                        ["content"] = new JArray(new JObject { ["type"] = "image", ["data"] = ro["__image"], ["mimeType"] = ro["mimeType"] }),
                        ["isError"] = false,
                    };
                }
                return new JObject
                {
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = (result ?? new JObject()).ToString(Formatting.Indented) }),
                    ["isError"] = false,
                };
            }
            catch (Exception e)
            {
                var inner = e is AggregateException ae ? ae.Flatten().InnerException ?? e : e;
                return new JObject
                {
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = inner.Message }),
                    ["isError"] = true,
                };
            }
        }
    }
}
