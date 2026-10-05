using System;
using System.Collections.Generic;
using System.Text;

namespace AutoCADHttp.Http
{
    /// <summary>Parsed HTTP request, including its bounded UTF-8 body.</summary>
    public sealed class HttpRequestInfo
    {
        public HttpRequestInfo(string method, string target, string protocol, IDictionary<string, string> headers, string body = "")
        {
            Method = method;
            Target = target;
            Protocol = protocol;
            Headers = headers;
            Body = body;

            int q = target.IndexOf('?');
            Path = q >= 0 ? target.Substring(0, q) : target;
        }

        /// <summary>HTTP method, e.g. "GET".</summary>
        public string Method { get; private set; }

        /// <summary>Raw request target, e.g. "/ping?x=1".</summary>
        public string Target { get; private set; }

        /// <summary>Request path without query string, e.g. "/ping".</summary>
        public string Path { get; private set; }

        /// <summary>Protocol, e.g. "HTTP/1.1".</summary>
        public string Protocol { get; private set; }

        public string Body { get; internal set; }

        /// <summary>Headers (case-insensitive names).</summary>
        public IDictionary<string, string> Headers { get; private set; }
    }

    /// <summary>HTTP response produced by a request handler.</summary>
    public sealed class HttpResponseInfo
    {
        public HttpResponseInfo(int statusCode, string reasonPhrase, string contentType, string body)
        {
            StatusCode = statusCode;
            ReasonPhrase = reasonPhrase;
            ContentType = contentType;
            Body = body ?? string.Empty;
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public int StatusCode { get; private set; }
        public string ReasonPhrase { get; private set; }
        public string ContentType { get; private set; }
        public string Body { get; private set; }

        public byte[] BodyBytes { get; private set; }

        public static HttpResponseInfo Bytes(int statusCode, string reasonPhrase, string contentType, byte[] body)
        {
            return new HttpResponseInfo(statusCode, reasonPhrase, contentType, null) { BodyBytes = body };
        }

        /// <summary>Additional response headers.</summary>
        public IDictionary<string, string> Headers { get; private set; }

        public static HttpResponseInfo Json(int statusCode, string reasonPhrase, string json)
        {
            return new HttpResponseInfo(statusCode, reasonPhrase, "application/json; charset=utf-8", json);
        }

        public static HttpResponseInfo JsonError(int statusCode, string reasonPhrase, string message)
        {
            return Json(statusCode, reasonPhrase,
                "{\"status\":\"error\",\"error\":\"" + JsonEscape(message) + "\"}");
        }

        /// <summary>Serializes the response as an HTTP/1.1 message with "Connection: close".</summary>
        public byte[] ToBytes(bool includeBody)
        {
            byte[] body = BodyBytes ?? Encoding.UTF8.GetBytes(Body);

            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(StatusCode).Append(' ').Append(ReasonPhrase).Append("\r\n");
            sb.Append("Content-Type: ").Append(ContentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("Connection: close\r\n");
            foreach (var header in Headers)
                sb.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            sb.Append("\r\n");

            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            if (!includeBody)
                return head;

            var result = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, result, 0, head.Length);
            Buffer.BlockCopy(body, 0, result, head.Length, body.Length);
            return result;
        }

        internal static string JsonEscape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
