#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LCQuest
{
    /// <summary>
    /// The wire format between the Quest (SteamBridgeTransport) and the Steam bridge on a PC (tools/SteamBridge, which
    /// compiles this file too), over one TCP connection: frames of [int32 length][byte kind][body], little-endian,
    /// strings as BinaryWriter writes them. The length counts the kind byte and the body.
    ///   Hello   Quest -> bridge   uint32 magic, uint16 version, string game version ("81")
    ///   Joined  bridge -> Quest   uint64 the bridge's Steam id, string lobby name: the host's connection is up
    ///   Data    both ways         byte reliable (1/0), then one Netcode transport message
    ///   Names   bridge -> Quest   int32 count, then (uint64 Steam id, string persona name) per lobby member
    ///   Closed  bridge -> Quest   string reason; the bridge then closes the socket
    /// </summary>
    public static class BridgeProtocol
    {
        public const ushort DefaultPort = 7777;
        public const uint Magic = 0x4251434C;  // "LCQB"
        public const ushort Version = 1;
        private const int MaxFrame = 16 << 20;

        public enum Kind : byte
        {
            Hello = 1,
            Joined = 2,
            Data = 3,
            Names = 4,
            Closed = 5,
        }

        public static byte[] Hello(string gameVersion) => Frame(Kind.Hello, w =>
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(gameVersion);
        });

        public static byte[] Joined(ulong steamId, string lobbyName) => Frame(Kind.Joined, w =>
        {
            w.Write(steamId);
            w.Write(lobbyName ?? "");
        });

        public static byte[] Names(IEnumerable<KeyValuePair<ulong, string>> names) => Frame(Kind.Names, w =>
        {
            var list = new List<KeyValuePair<ulong, string>>(names);
            w.Write(list.Count);
            foreach (var pair in list)
            {
                w.Write(pair.Key);
                w.Write(pair.Value ?? "");
            }
        });

        public static byte[] Closed(string reason) => Frame(Kind.Closed, w => w.Write(reason ?? ""));

        /// <summary>A Data frame, built without a BinaryWriter: it carries every game message</summary>
        public static byte[] Data(bool reliable, byte[] data, int offset, int count)
        {
            var frame = new byte[4 + 2 + count];
            WriteInt32(frame, 0, 2 + count);
            frame[4] = (byte)Kind.Data;
            frame[5] = reliable ? (byte)1 : (byte)0;
            Buffer.BlockCopy(data, offset, frame, 6, count);
            return frame;
        }

        /// <summary>Reads one frame; false when the stream ended cleanly between frames</summary>
        public static bool Read(Stream stream, out Kind kind, out byte[] body)
        {
            var header = new byte[5];
            if (!ReadExactly(stream, header, 0, 4, true))
            {
                kind = 0;
                body = null;
                return false;
            }
            var length = header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24;
            if (length < 1 || length > MaxFrame)
                throw new InvalidDataException($"bad frame length {length}");
            ReadExactly(stream, header, 4, 1, false);
            kind = (Kind)header[4];
            body = new byte[length - 1];
            ReadExactly(stream, body, 0, body.Length, false);
            return true;
        }

        public static BinaryReader Reader(byte[] body) => new BinaryReader(new MemoryStream(body), Encoding.UTF8);

        private static byte[] Frame(Kind kind, Action<BinaryWriter> write)
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(0);
                writer.Write((byte)kind);
                write(writer);
            }
            var frame = stream.ToArray();
            WriteInt32(frame, 0, frame.Length - 4);
            return frame;
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        private static bool ReadExactly(Stream stream, byte[] buffer, int offset, int count, bool endAllowed)
        {
            var read = 0;
            while (read < count)
            {
                var n = stream.Read(buffer, offset + read, count - read);
                if (n == 0)
                {
                    if (endAllowed && read == 0) return false;
                    throw new EndOfStreamException();
                }
                read += n;
            }
            return true;
        }
    }
}
