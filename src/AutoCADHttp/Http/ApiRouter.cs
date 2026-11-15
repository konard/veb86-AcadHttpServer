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
        /// Setting <c>JSONView = 1</c>: receives the original body of every accepted <c>POST /ipc</c> JSON
        /// on the HTTP thread, before it is queued. Null (default) disables the output. The callback must
        /// only queue the text (e.g. <c>CommandLineLog.Post</c>); its failures never change the HTTP response.
        /// </summary>
        public Action<string> JsonView { get; set; }

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
                    return response;
                }
                string contentType;
                if (!request.Headers.TryGetValue("Content-Type", out contentType) ||
                    !string.Equals(contentType.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
                    return HttpResponseInfo.Json(415, "Unsupported Media Type", IpcMessage.ErrorJson(null, "INVALID_CONTENT_TYPE"));
                IpcMessage message;
                try { message = IpcMessage.Parse(request.Body); }
                catch (FormatException)
                {
                    return HttpResponseInfo.Json(400, "Bad Request", IpcMessage.ErrorJson(null, "INVALID_JSON"));
                }
                var jsonView = JsonView;
                if (jsonView == null)
                    Incoming.Enqueue(message);
                else
                {
                    lock (_jsonViewSync)
                    {
                        try { jsonView(request.Body); } catch { }
                        Incoming.Enqueue(message);
                    }
                }
                return HttpResponseInfo.Json(202, "Accepted", IpcMessage.CreateResponse(message.Id, "{\"queued\":true}").Json);
            }

            if (request.Path == "/widgets" || request.Path.StartsWith("/widgets/", StringComparison.Ordinal))
                return _widgets.Handle(request);

            return HttpResponseInfo.JsonError(404, "Not Found", "Not found");
        }
    }
}
