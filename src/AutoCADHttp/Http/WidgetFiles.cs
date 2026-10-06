using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace AutoCADHttp.Http
{
    /// <summary>Widget directory listings and static resources, independent of IPC and command dispatch.</summary>
    public sealed class WidgetFiles
    {
        public const int MaxFileBytes = 8 * 1024 * 1024;
        private readonly string _root;
        private readonly StringComparison _pathComparison = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private static readonly IDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".html", "text/html; charset=utf-8" }, { ".htm", "text/html; charset=utf-8" },
            { ".js", "application/javascript; charset=utf-8" }, { ".css", "text/css; charset=utf-8" },
            { ".json", "application/json; charset=utf-8" }, { ".txt", "text/plain; charset=utf-8" },
            { ".svg", "image/svg+xml" }, { ".png", "image/png" }, { ".jpg", "image/jpeg" },
            { ".jpeg", "image/jpeg" }, { ".gif", "image/gif" }, { ".ico", "image/x-icon" },
            { ".webp", "image/webp" }, { ".woff", "font/woff" }, { ".woff2", "font/woff2" },
            { ".wasm", "application/wasm" }
        };

        public WidgetFiles(string root)
        {
            if (!string.IsNullOrWhiteSpace(root))
                _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        public HttpResponseInfo Handle(HttpRequestInfo request)
        {
            if (request.Method != "GET" && request.Method != "HEAD")
            {
                var response = HttpResponseInfo.JsonError(405, "Method Not Allowed", "Method not allowed");
                response.Headers["Allow"] = "GET, HEAD";
                return response;
            }
            if (_root == null)
                return HttpResponseInfo.JsonError(404, "Not Found", "Widgets directory is not configured");
            if (request.Path == "/widgets")
            {
                var redirect = new HttpResponseInfo(308, "Permanent Redirect", "text/plain; charset=utf-8", "");
                redirect.Headers["Location"] = "/widgets/" + request.Target.Substring(request.Path.Length);
                return redirect;
            }
            try
            {
                string relative = Uri.UnescapeDataString(request.Path.Substring("/widgets".Length)).TrimStart('/');
                string[] segments = relative.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string segment in segments)
                {
                    if (segment == "." || segment == ".." || segment.IndexOfAny(new[] { '\\', ':', '\0' }) >= 0)
                        return Forbidden();
                }
                string path = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(_root, _pathComparison) &&
                    !string.Equals(path, Path.GetFullPath(_root), _pathComparison))
                    return Forbidden();
                // Reject links/junctions, including the configured root, to avoid escaping through a nested link.
                string current = _root;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return Forbidden();
                foreach (string segment in segments)
                {
                    current = Path.Combine(current, segment);
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        return Forbidden();
                }
                if (Directory.Exists(path))
                {
                    if (!request.Path.EndsWith("/", StringComparison.Ordinal))
                    {
                        var redirect = new HttpResponseInfo(308, "Permanent Redirect", "text/plain; charset=utf-8", "");
                        redirect.Headers["Location"] = request.Path + "/" + request.Target.Substring(request.Path.Length);
                        return redirect;
                    }
                    return ListDirectory(path, request.Path, segments.Length > 0);
                }
                if (!File.Exists(path))
                    return HttpResponseInfo.JsonError(404, "Not Found", "Widget file not found");
                byte[] bytes;
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (file.Length > MaxFileBytes)
                        return HttpResponseInfo.JsonError(413, "Payload Too Large", "Widget file too large");
                    bytes = new byte[(int)file.Length];
                    int total = 0;
                    while (total < bytes.Length)
                    {
                        int read = file.Read(bytes, total, bytes.Length - total);
                        if (read == 0)
                            throw new IOException("Widget file changed while reading.");
                        total += read;
                    }
                }
                string contentType;
                if (!ContentTypes.TryGetValue(Path.GetExtension(path), out contentType))
                    contentType = "application/octet-stream";
                var result = HttpResponseInfo.Bytes(200, "OK", contentType, bytes);
                result.Headers["X-Content-Type-Options"] = "nosniff";
                return result;
            }
            catch (FileNotFoundException) { return HttpResponseInfo.JsonError(404, "Not Found", "Widget file not found"); }
            catch (DirectoryNotFoundException) { return HttpResponseInfo.JsonError(404, "Not Found", "Widget file not found"); }
            catch (UnauthorizedAccessException) { return Forbidden(); }
            catch (ArgumentException) { return Forbidden(); }
            catch (NotSupportedException) { return Forbidden(); }
        }

        private static HttpResponseInfo ListDirectory(string path, string requestPath, bool includeParent)
        {
            var directories = new List<string>();
            var files = new List<string>();
            foreach (string entry in Directory.EnumerateFileSystemEntries(path))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                // Keep links/junctions out of the listing, just as direct requests to them are forbidden.
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                var names = (attributes & FileAttributes.Directory) != 0 ? directories : files;
                names.Add(Path.GetFileName(entry));
            }
            directories.Sort(StringComparer.Ordinal);
            files.Sort(StringComparer.Ordinal);

            string title = "Index of " + WebUtility.HtmlEncode(Uri.UnescapeDataString(requestPath));
            var html = new StringBuilder("<!doctype html>\n<html lang=\"en\">\n<head><meta charset=\"utf-8\"><title>");
            html.Append(title).Append("</title></head>\n<body><h1>").Append(title).Append("</h1>\n<ul>\n");
            if (includeParent) html.Append("<li><a href=\"../\">../</a></li>\n");
            AppendEntries(html, directories, "/");
            AppendEntries(html, files, "");
            html.Append("</ul>\n</body></html>\n");
            var response = new HttpResponseInfo(200, "OK", "text/html; charset=utf-8", html.ToString());
            response.Headers["X-Content-Type-Options"] = "nosniff";
            return response;
        }

        private static void AppendEntries(StringBuilder html, IEnumerable<string> names, string suffix)
        {
            foreach (string name in names)
                html.Append("<li><a href=\"").Append(Uri.EscapeDataString(name)).Append(suffix)
                    .Append("\">").Append(WebUtility.HtmlEncode(name)).Append(suffix).Append("</a></li>\n");
        }

        private static HttpResponseInfo Forbidden()
        {
            return HttpResponseInfo.JsonError(403, "Forbidden", "Widget path not allowed");
        }
    }
}
