using System;
using System.Linq;
using AutoCADHttp.Http;
using Xunit;

namespace AutoCADHttp.Tests
{
    public class IpcMessageTests
    {
        [Fact]
        public void Envelopes_PreserveIdsAndOpaquePayloads()
        {
            const string id = "корреляция\n\"123";
            var command = IpcMessage.CreateCommand(id, "INSERT_DEV", "{\"nested\":{\"rows\":[1,true,null,\"中文\"]},\"a b\":2}");
            Assert.Equal(id, command.Id);
            Assert.Equal("command", command.Type);
            Assert.Equal("INSERT_DEV", command.Command);
            Assert.Contains("中文", command.ParametersJson);
            Assert.Contains("\"a b\":2", command.ParametersJson);
            Assert.Equal(id, IpcMessage.Parse(command.Json).Id);
            Assert.Equal("ok", IpcMessage.CreateResponse(id).Status);
            Assert.Equal("UNKNOWN_COMMAND", IpcMessage.CreateError(id, "UNKNOWN_COMMAND").ErrorCode);
            Assert.Equal("OBJECT_CREATED", IpcMessage.CreateEvent(id, "OBJECT_CREATED").Event);
        }

        [Theory]
        [InlineData("")]
        [InlineData("null")]
        [InlineData("[]")]
        [InlineData("{broken")]
        [InlineData("{\"id\":1,\"type\":\"command\",\"command\":\"PING\",\"parameters\":{}}")]
        [InlineData("{\"id\":\"\",\"type\":\"command\",\"command\":\"PING\",\"parameters\":{}}")]
        [InlineData("{\"id\":\"1\",\"type\":\"other\"}")]
        [InlineData("{\"id\":\"1\",\"type\":\"command\",\"parameters\":{}}")]
        [InlineData("{\"id\":\"1\",\"type\":\"command\",\"command\":\"PING\",\"parameters\":[]}")]
        [InlineData("{\"id\":\"1\",\"type\":\"event\",\"parameters\":{}}")]
        [InlineData("{\"id\":\"1\",\"type\":\"response\",\"status\":\"ok\"}")]
        [InlineData("{\"id\":\"1\",\"type\":\"response\",\"status\":\"error\",\"error\":{}}")]
        [InlineData("{\"id\":\"1\",\"type\":\"response\",\"status\":\"pending\"}")]
        [InlineData("{\"id\":\"1\",\"id\":\"2\",\"type\":\"response\",\"status\":\"ok\",\"result\":{}}")]
        [InlineData("{\"id\":\"1\",\"type\":\"command\",\"command\":\"PING\",\"parameters\":{},}")]
        [InlineData("{\"id\":\"1\",\"type\":\"command\",\"command\":\"PING\",\"parameters\":{}} false")]
        public void MalformedEnvelopes_AreRejected(string json)
        {
            Assert.Throws<FormatException>(() => IpcMessage.Parse(json));
        }

        [Theory]
        [InlineData("{\"x\":01}")]
        [InlineData("{\"x\":+1}")]
        [InlineData("{\"x\":1.}")]
        [InlineData("{\"x\":.1}")]
        [InlineData("{\"x\":1e}")]
        [InlineData("{\"x\":NaN}")]
        [InlineData("{\"x\":[1,]}")]
        [InlineData("{\"x\":'single quote'}")]
        [InlineData("{\"x\":\"bad\\q\"}")]
        [InlineData("{\"x\":\"bad\\uZZZZ\"}")]
        [InlineData("{\"x\":1,\"x\":2}")]
        public void InvalidPayloadSyntax_IsRejected(string parameters)
        {
            Assert.Throws<FormatException>(() => IpcMessage.CreateCommand("1", "PING", parameters));
        }

        [Fact]
        public void ValidPayloads_PreserveNumberPrecisionAndStringEscapes()
        {
            const string payload = "{\"numbers\":[-0,1.5,1e+30000,123456789012345678901234567890],\"text\":\"\\u4e2d\\n\\t\\/\\b\\f\\r\"}";
            Assert.Equal(payload, IpcMessage.CreateCommand("1", "PING", payload).ParametersJson);
            const string json = "{\"id\":\"\\u0031\",\"type\":\"command\",\"command\":\"PING\",\"parameters\":{}}";
            Assert.Equal("1", IpcMessage.Parse(json).Id);
        }

        [Fact]
        public void ExcessiveNesting_IsRejectedWithFiniteInput()
        {
            string payload = string.Concat(Enumerable.Repeat("{\"x\":", 40)) + "0" + new string('}', 40);
            Assert.Throws<FormatException>(() => IpcMessage.CreateCommand("1", "PING", payload));
        }
    }
}
