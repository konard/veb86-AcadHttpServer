using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace AutoCADHttp.Tests
{
    internal static class HttpTestHelpers
    {
        public static async Task<string> SendRaw(int port, byte[] request, int splitAt = 0)
        {
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                using (var stream = client.GetStream())
                using (var result = new MemoryStream())
                {
                    Task exchange = Exchange(stream, result, request, splitAt);
                    Assert.Same(exchange, await Task.WhenAny(exchange, Task.Delay(5000)));
                    await exchange;
                    return Encoding.UTF8.GetString(result.ToArray());
                }
            }
        }

        private static async Task Exchange(NetworkStream stream, MemoryStream result, byte[] request, int splitAt)
        {
            if (splitAt > 0)
            {
                await stream.WriteAsync(request, 0, splitAt);
                await Task.Delay(20);
                await stream.WriteAsync(request, splitAt, request.Length - splitAt);
            }
            else
                await stream.WriteAsync(request, 0, request.Length);
            await stream.CopyToAsync(result);
        }

        public static async Task WaitFor(Func<bool> condition)
        {
            for (int i = 0; i < 500 && !condition(); i++)
                await Task.Delay(10);
            Assert.True(condition(), "Condition did not become true within five seconds.");
        }
    }
}
