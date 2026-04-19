using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli.Protocol
{
    [Serializable]
    public class BridgePipeRequest
    {
        public string token { get; set; }

        public string method { get; set; }

        public string path { get; set; }

        public string body { get; set; }
    }

    [Serializable]
    public class BridgePipeResponse
    {
        public int statusCode { get; set; }

        public string body { get; set; }
    }

    public static class BridgePipeProtocol
    {
        public const int MaxMessageBytes = 16 * 1024 * 1024;

        public static void WriteFrame(Stream stream, string payload)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            payload ??= string.Empty;
            var bytes = Encoding.UTF8.GetBytes(payload);
            if (bytes.Length > MaxMessageBytes)
            {
                throw new InvalidOperationException($"消息长度超出限制：{bytes.Length} bytes");
            }

            var lengthBytes = BitConverter.GetBytes(bytes.Length);
            stream.Write(lengthBytes, 0, lengthBytes.Length);
            if (bytes.Length > 0)
            {
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        public static string ReadFrame(Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            var lengthBytes = ReadExact(stream, sizeof(int));
            if (lengthBytes == null)
            {
                return null;
            }

            var length = BitConverter.ToInt32(lengthBytes, 0);
            if (length < 0 || length > MaxMessageBytes)
            {
                throw new InvalidDataException($"无效的消息长度：{length}");
            }

            if (length == 0)
            {
                return string.Empty;
            }

            var payloadBytes = ReadExact(stream, length);
            if (payloadBytes == null)
            {
                throw new EndOfStreamException("读取消息体时连接已关闭。");
            }

            return Encoding.UTF8.GetString(payloadBytes);
        }

        public static async Task WriteFrameAsync(Stream stream, string payload, CancellationToken cancellationToken)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            payload ??= string.Empty;
            var bytes = Encoding.UTF8.GetBytes(payload);
            if (bytes.Length > MaxMessageBytes)
            {
                throw new InvalidOperationException($"消息长度超出限制：{bytes.Length} bytes");
            }

            var lengthBytes = BitConverter.GetBytes(bytes.Length);
            await stream.WriteAsync(lengthBytes, 0, lengthBytes.Length, cancellationToken);
            if (bytes.Length > 0)
            {
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken);
            }
        }

        public static async Task<string> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            var lengthBytes = await ReadExactAsync(stream, sizeof(int), cancellationToken);
            if (lengthBytes == null)
            {
                return null;
            }

            var length = BitConverter.ToInt32(lengthBytes, 0);
            if (length < 0 || length > MaxMessageBytes)
            {
                throw new InvalidDataException($"无效的消息长度：{length}");
            }

            if (length == 0)
            {
                return string.Empty;
            }

            var payloadBytes = await ReadExactAsync(stream, length, cancellationToken);
            if (payloadBytes == null)
            {
                throw new EndOfStreamException("读取消息体时连接已关闭。");
            }

            return Encoding.UTF8.GetString(payloadBytes);
        }

        static byte[] ReadExact(Stream stream, int length)
        {
            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = stream.Read(buffer, offset, length - offset);
                if (read <= 0)
                {
                    return offset == 0 ? null : throw new EndOfStreamException("读取帧数据时连接意外结束。");
                }

                offset += read;
            }

            return buffer;
        }

        static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken cancellationToken)
        {
            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(buffer, offset, length - offset, cancellationToken);
                if (read <= 0)
                {
                    return offset == 0 ? null : throw new EndOfStreamException("读取帧数据时连接意外结束。");
                }

                offset += read;
            }

            return buffer;
        }
    }
}
