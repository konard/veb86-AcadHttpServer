using System;

namespace AutoCADHttp.Http
{
    /// <summary>
    /// Maps HTTP requests to responses. At this stage only the test endpoint
    /// <c>GET /ping</c> exists; it does not touch AutoCAD objects, so it is safe
    /// to run on a background (non-AutoCAD) thread.
    /// </summary>
    public sealed class ApiRouter
    {
        private readonly string _pingJson;

        public ApiRouter(string application, string version)
        {
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

            return HttpResponseInfo.JsonError(404, "Not Found", "Not found");
        }
    }
}
