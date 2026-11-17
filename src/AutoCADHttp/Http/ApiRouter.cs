using System;
using System.Collections.Concurrent;

namespace AutoCADHttp.Http
{
    /// <summary>
    /// HTTP transport only: ping, incoming IPC envelopes and static widgets.
    /// Does not dispatch commands or access application APIs.
    /// </summary>
    public sealed class ApiRouter
    {
        private readonly string _pingJson;

        private readonly WidgetFiles _widgets;

        // Keeps the JSONView output order identical to the Incoming queue order under concurrent requests.
        private readonly object _jsonViewSync = new object();

        public ConcurrentQueue<IpcMessage> Incoming { get; private set; }

        /// <summary>
        /// Setting <c>JSONView = 1</c>: receives one ready-to-print line for every request to <c>/ipc</c>, on the
        /// HTTP thread, valid or not, so a rejected message can be seen and fixed:
        /// <list type="bullet">
        /// <item><c>IPC JSON: &lt;body&gt;</c> - accepted (<c>202</c>), shown before it is queued, in queue order;</item>
        /// <item><c>IPC JSON INVALID (&lt;status&gt; &lt;code&gt;: &lt;reason&gt;): &lt;body&gt;</c> - rejected
        /// (<c>400</c>, <c>405</c>, <c>415</c>, and via <see cref="ReportRejected"/> the server-level errors), not queued.</item>
        /// </list>
        /// The body is shown exactly as received. Null (default) disables the output. The callback must only queue
        /// the text (e.g. <c>CommandLineLog.Post</c>); its failures never change the HTTP response.
        /// </summary>
        public Action<string> JsonView { get; set; }

        /// <summary>Prefix of a <see cref="JsonView"/> line for an accepted message.</summary>
        public const string JsonViewPrefix = "IPC JSON: ";

        /// <summary>Prefix of a <see cref="JsonView"/> line for a rejected message.</summary>
        public const string JsonViewInvalidPrefix = "IPC JSON INVALID";

        public ApiRouter(string application, string version, ConcurrentQueue<IpcMessage> incoming = null, string widgetsDirectory = null)
        {
            Incoming = incoming ?? new ConcurrentQueue<IpcMessage>();
            _widgets = new WidgetFiles(widgetsDirectory);
            _pingJson = "{\"status\":\"ok\",\"application\":\"" + HttpResponseInfo.JsonEscape(application) +
                        "\",\"version\":\"" + HttpResponseInfo.JsonEscape(version) + "\"}";
        }

        /// <summary>JSON returned by <c>GET /ping</c>.</summary>
        public string PingJson
        {
            get { return _pingJson; }
        }

        public HttpResponseInfo Handle(HttpRequestInfo request)
        {
            if (string.Equals(request.Path, "/ping", StringComparison.Ordinal))
            {
                if (request.Method == "GET" || request.Method == "HEAD")
                    return HttpResponseInfo.Json(200, "OK", _pingJson);

                var notAllowed = HttpResponseInfo.JsonError(405, "Method Not Allowed", "Method not allowed");
                notAllowed.Headers["Allow"] = "GET, HEAD";
                return notAllowed;
            }

            if (request.Path == "/ipc")
            {
                if (request.Method != "POST")
                {
                    var response = HttpResponseInfo.JsonError(405, "Method Not Allowed", "Method not allowed");
                    response.Headers["Allow"] = "POST";
                    ShowInvalid(request, "405 METHOD_NOT_ALLOWED: " + request.Method + " /ipc, use POST");
                    return response;
                }
                string contentType;
                if (!request.Headers.TryGetValue("Content-Type", out contentType) ||
                    !string.Equals(contentType.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
                {
                    ShowInvalid(request, "415 INVALID_CONTENT_TYPE: " + (contentType == null
                        ? "no Content-Type header" : "Content-Type is '" + contentType + "'") + ", use application/json");
                    return HttpResponseInfo.Json(415, "Unsupported Media Type", IpcMessage.ErrorJson(null, "INVALID_CONTENT_TYPE"));
                }
                IpcMessage message;
                try { message = IpcMessage.Parse(request.Body); }
                catch (FormatException ex)
                {
                    ShowInvalid(request, "400 INVALID_JSON: " + ex.Message);
                    return HttpResponseInfo.Json(400, "Bad Request", IpcMessage.ErrorJson(null, "INVALID_JSON"));
                }
                var jsonView = JsonView;
                if (jsonView == null)
                    Incoming.Enqueue(message);
                else
                {
                    lock (_jsonViewSync)
                    {
                        try { jsonView(JsonViewPrefix + request.Body); } catch { }
                        Incoming.Enqueue(message);
                    }
                }
                return HttpResponseInfo.Json(202, "Accepted", IpcMessage.CreateResponse(message.Id, "{\"queued\":true}").Json);
            }

            if (request.Path == "/widgets" || request.Path.StartsWith("/widgets/", StringComparison.Ordinal))
                return _widgets.Handle(request);

            return HttpResponseInfo.JsonError(404, "Not Found", "Not found");
        }

        /// <summary>
        /// For <see cref="LocalHttpServer.RequestRejected"/>: shows a <c>/ipc</c> request that the server rejected
        /// before routing (body not UTF-8, too large, incomplete, no Content-Length, host not allowed) in
        /// <see cref="JsonView"/>. The HTTP response is not changed.
        /// </summary>
        public void ReportRejected(HttpRequestInfo request, HttpResponseInfo response)
        {
            if (request == null || response == null || request.Path != "/ipc")
                return;
            string error = null;
            try
            {
                IpcJson body = IpcJson.Parse(response.Body);
                IpcJson member;
                if (body.Members != null && body.Members.TryGetValue("error", out member) && member.Kind == "string")
                    error = member.String;
            }
            catch (FormatException) { }
            ShowInvalid(request, response.StatusCode + " " + response.ReasonPhrase + (error != null ? ": " + error : ""));
        }

        private void ShowInvalid(HttpRequestInfo request, string reason)
        {
            var jsonView = JsonView;
            if (jsonView == null)
                return;
            string body = request.Body == null ? "<body not read>" : request.Body.Length == 0 ? "<empty body>" : request.Body;
            lock (_jsonViewSync)
            {
                try { jsonView(JsonViewInvalidPrefix + " (" + reason + "): " + body); } catch { }
            }
        }
    }
}
