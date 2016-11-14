using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace ZWave.Channel.Protocol
{
    class Message : IEquatable<Message>
    {
        public static readonly Message ACK = new Message(FrameHeader.ACK);
        public static readonly Message NAK = new Message(FrameHeader.NAK);
        public static readonly Message CAN = new Message(FrameHeader.CAN);
        
        public static Byte[] ToAppend = null;

        public readonly FrameHeader Header;
        public readonly MessageType? Type;
        public readonly Function? Function;

        protected Message(FrameHeader header, MessageType? type = null, Function? function = null)
        {
            Header = header;
            Type = type;
            Function = function;
        }

        public override string ToString()
        {
            return $"{Header} {Type} {Function}";
        }

        protected virtual List<byte> GetPayload()
        {
            var buffer = new List<byte>();
            buffer.Add((byte)FrameHeader.SOF);
            buffer.Add(0x00);
            buffer.Add((byte)Type);
            buffer.Add((byte)Function);
            return buffer;
        }

        public Task Write(Stream stream)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            if (Header == FrameHeader.SOF)
            {
                var payload = GetPayload();

                // update length
                payload[1] = (byte)(payload.Count - 1);

                // add checksum 
                payload.Add(payload.Skip(1).Aggregate((byte)0xFF, (total, next) => total ^= next));

                return stream.WriteAsync(payload.ToArray(), 0, payload.Count);
            }

            switch (Header)
            {
                case FrameHeader.ACK:
                    return stream.WriteAsync(new[] { (byte)FrameHeader.ACK }, 0, 1);
                case FrameHeader.NAK:
                    return stream.WriteAsync(new[] { (byte)FrameHeader.NAK }, 0, 1);
                case FrameHeader.CAN:
                    return stream.WriteAsync(new[] { (byte)FrameHeader.CAN }, 0, 1);
            }

            throw new NotSupportedException("Frameheader is not supported");
        }

        public static string ByteArrayToString(byte[] ba, int length)
        {
            StringBuilder hex = new StringBuilder(length * 2);
            foreach (byte b in ba)
                hex.AppendFormat("{0:x2} ", b);
            return hex.ToString();
        }

        public static Message Read(FrameHeader header,int length, MessageType type, Function function, byte[] payload)
        {
            
            if (header == FrameHeader.SOF)
            {

                if (type == MessageType.Request)
                {
                    if (function == Channel.Function.ApplicationCommandHandler)
                    {
                        return new NodeEvent(payload);
                    }
                    if (function == Channel.Function.SendData)
                    {
                        return new NodeCommandCompleted(payload);
                    }
                }
                if (type == MessageType.Response)
                {
                    if (function != Channel.Function.SendData)
                    {
                        return new ControllerFunctionCompleted(function, payload);
                    }
                }
                return new UnknownMessage(header, type, function, payload);
            }

            throw new UnknownFrameException($"Frame {header} is not supported");
        }

        public override int GetHashCode()
        {
            return Header.GetHashCode();
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as Message);
        }

        public bool Equals(Message other)
        {
            if (Object.ReferenceEquals(other, null))
                return false;

            if (Object.ReferenceEquals(other, this))
                return true;

            if (GetType() != other.GetType())
                return false;

            return object.Equals(Header, other.Header) && object.Equals(Type, other.Type) && object.Equals(Function, other.Function);
        }

        public static bool operator ==(Message a, Message b)
        {
            if (Object.ReferenceEquals(a, b))
                return true;

            if (Object.ReferenceEquals(a, null) || Object.ReferenceEquals(b, null))
                return false;

            return object.Equals(a, b);
        }

        public static bool operator !=(Message a, Message b)
        {
            return !(a == b);
        }
    }

    static partial class Extensions
    {
        public static async Task ReadAsyncExact(this Stream stream, byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));

            var read = 0;
            while (read < count)
            {
                read += await stream.ReadAsync(buffer, offset + read, count - read);
            }
        }
    }

}
