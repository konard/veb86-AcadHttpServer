using System;
namespace AutoCADHttp.Http
{
    /// <summary>Immutable envelope. Application payloads stay JSON; transport does not interpret commands.</summary>
    public sealed class IpcMessage
    {
        private IpcMessage() { }

        public string Id { get; private set; }
        public string Type { get; private set; }
        public string Command { get; private set; }
        public string Event { get; private set; }
        public string Status { get; private set; }
        public string ParametersJson { get; private set; }
        public string ResultJson { get; private set; }
        public string ErrorCode { get; private set; }
        public string Json { get; private set; }

        public static IpcMessage Parse(string json)
        {
            IpcJson root = IpcJson.Parse(json);
            RequireType(root, "object");
            var message = new IpcMessage
            {
                Id = RequiredString(root, "id"),
                Type = RequiredString(root, "type"),
                Json = json
            };
            switch (message.Type)
            {
                case "command":
                    message.Command = RequiredString(root, "command");
                    message.ParametersJson = ObjectJson(root, "parameters");
                    break;
                case "event":
                    message.Event = RequiredString(root, "event");
                    message.ParametersJson = ObjectJson(root, "parameters");
                    break;
                case "response":
                    message.Status = RequiredString(root, "status");
                    if (message.Status == "ok")
                        message.ResultJson = ObjectJson(root, "result");
                    else if (message.Status == "error")
                    {
                        IpcJson error = Member(root, "error");
                        RequireType(error, "object");
                        message.ErrorCode = RequiredString(error, "code");
                    }
                    else
                        throw new FormatException("Response status must be ok or error.");
                    break;
                default:
                    throw new FormatException("IPC type must be command, response or event.");
            }
            return message;
        }

        public static IpcMessage CreateCommand(string id, string command, string parametersJson = "{}")
        {
            return Parse("{\"id\":" + Quote(id) + ",\"type\":\"command\",\"command\":" + Quote(command) +
                         ",\"parameters\":" + parametersJson + "}");
        }

        public static IpcMessage CreateEvent(string id, string eventName, string parametersJson = "{}")
        {
            return Parse("{\"id\":" + Quote(id) + ",\"type\":\"event\",\"event\":" + Quote(eventName) +
                         ",\"parameters\":" + parametersJson + "}");
        }

        public static IpcMessage CreateResponse(string id, string resultJson = "{}")
        {
            return Parse("{\"id\":" + Quote(id) + ",\"type\":\"response\",\"status\":\"ok\",\"result\":" + resultJson + "}");
        }

        public static IpcMessage CreateError(string id, string code)
        {
            return Parse(ErrorJson(id, code));
        }

        // Invalid input may have no usable id, so its HTTP error need not be a valid queued envelope.
        internal static string ErrorJson(string id, string code)
        {
            return "{" + (id == null ? "" : "\"id\":" + Quote(id) + ",") +
                   "\"type\":\"response\",\"status\":\"error\",\"error\":{\"code\":" + Quote(code) + "}}";
        }

        internal static string Quote(string value)
        {
            return "\"" + HttpResponseInfo.JsonEscape(value) + "\"";
        }

        private static IpcJson Member(IpcJson parent, string name)
        {
            IpcJson member;
            if (parent.Members == null || !parent.Members.TryGetValue(name, out member))
                throw new FormatException("Expected " + name + " field.");
            return member;
        }

        private static void RequireType(IpcJson element, string type)
        {
            if (element.Kind != type)
                throw new FormatException("Expected JSON " + type + ".");
        }

        private static string RequiredString(IpcJson parent, string name)
        {
            IpcJson member = Member(parent, name);
            RequireType(member, "string");
            if (string.IsNullOrWhiteSpace(member.String))
                throw new FormatException(name + " must not be empty.");
            return member.String;
        }

        private static string ObjectJson(IpcJson parent, string name)
        {
            IpcJson member = Member(parent, name);
            RequireType(member, "object");
            return member.Raw;
        }
    }
}
