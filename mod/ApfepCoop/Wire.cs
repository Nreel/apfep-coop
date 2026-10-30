using System;
using System.Text;
using UnityEngine;

// Wire format (contract slice 1): every message is
//   [kind:byte][seq:uint32][peerFrom:uint32] payload...
// Numbers little-endian; strings UTF8 length-prefixed (int32 len + bytes);
// Vector3 = three float32; Quaternion = four float32.
namespace ApfepCoop.Net
{
    public enum MsgKind : byte
    {
        Hello = 1,
        PeerList = 2,
        Ping = 3,
        Pong = 4,
        Snapshot = 5,
        Delta = 6,
        StateChange = 7,
        PuppetMove = 8,
        Bye = 9,
        Ready = 10,
        Error = 11,
    }

    public static class Wire
    {
        public const int HeaderSize = 9; // 1 + 4 + 4

        public static byte[] Build(MsgKind kind, uint seq, uint peer, byte[] payload)
        {
            if (payload == null) payload = Array.Empty<byte>();
            byte[] buf = new byte[HeaderSize + payload.Length];
            buf[0] = (byte)kind;
            PutUInt(buf, 1, seq);
            PutUInt(buf, 5, peer);
            if (payload.Length > 0) Buffer.BlockCopy(payload, 0, buf, HeaderSize, payload.Length);
            return buf;
        }

        public static bool TryParse(byte[] raw, out MsgKind kind, out uint seq, out uint peer, out byte[] payload)
        {
            kind = MsgKind.Error;
            seq = 0;
            peer = 0;
            payload = Array.Empty<byte>();
            if (raw == null || raw.Length < HeaderSize) return false;
            byte k = raw[0];
            if (k < (byte)MsgKind.Hello || k > (byte)MsgKind.Error) return false;
            kind = (MsgKind)k;
            seq = GetUInt(raw, 1);
            peer = GetUInt(raw, 5);
            int len = raw.Length - HeaderSize;
            if (len > 0)
            {
                payload = new byte[len];
                Buffer.BlockCopy(raw, HeaderSize, payload, 0, len);
            }
            return true;
        }

        // ---- primitive helpers (static; no allocation churn per frame) ----

        public static void PutInt(byte[] buf, int off, int v)
        {
            buf[off] = (byte)v;
            buf[off + 1] = (byte)(v >> 8);
            buf[off + 2] = (byte)(v >> 16);
            buf[off + 3] = (byte)(v >> 24);
        }

        public static int GetInt(byte[] raw, int off)
        {
            return raw[off] | (raw[off + 1] << 8) | (raw[off + 2] << 16) | (raw[off + 3] << 24);
        }

        public static void PutUInt(byte[] buf, int off, uint v)
        {
            buf[off] = (byte)v;
            buf[off + 1] = (byte)(v >> 8);
            buf[off + 2] = (byte)(v >> 16);
            buf[off + 3] = (byte)(v >> 24);
        }

        public static uint GetUInt(byte[] raw, int off)
        {
            return (uint)(raw[off] | (raw[off + 1] << 8) | (raw[off + 2] << 16) | (raw[off + 3] << 24));
        }

        public static void PutLong(byte[] buf, int off, long v)
        {
            for (int i = 0; i < 8; i++) buf[off + i] = (byte)(v >> (8 * i));
        }

        public static long GetLong(byte[] raw, int off)
        {
            long v = 0;
            for (int i = 7; i >= 0; i--) v = (v << 8) | raw[off + i];
            return v;
        }

        public static void PutFloat(byte[] buf, int off, float v)
        {
            PutInt(buf, off, BitConverter.SingleToInt32Bits(v));
        }

        public static float GetFloat(byte[] raw, int off)
        {
            return BitConverter.Int32BitsToSingle(GetInt(raw, off));
        }

        public static void PutByte(byte[] buf, int off, byte v) => buf[off] = v;

        public static byte GetByte(byte[] raw, int off) => raw[off];

        // ---- length-prefixed UTF8 string ----

        public static byte[] Enc(string s)
        {
            byte[] bytes = s == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(s);
            byte[] buf = new byte[4 + bytes.Length];
            PutInt(buf, 0, bytes.Length);
            if (bytes.Length > 0) Buffer.BlockCopy(bytes, 0, buf, 4, bytes.Length);
            return buf;
        }

        public static string Dec(byte[] raw, ref int off)
        {
            if (raw == null || off + 4 > raw.Length) return "";
            int len = GetInt(raw, off);
            off += 4;
            if (len < 0 || off + len > raw.Length) return "";
            string s = Encoding.UTF8.GetString(raw, off, len);
            off += len;
            return s;
        }

        // ---- math types ----

        public static byte[] Enc(Vector3 v)
        {
            byte[] buf = new byte[12];
            PutFloat(buf, 0, v.x);
            PutFloat(buf, 4, v.y);
            PutFloat(buf, 8, v.z);
            return buf;
        }

        public static Vector3 DecV3(byte[] raw, ref int off)
        {
            if (raw == null || off + 12 > raw.Length) return Vector3.zero;
            Vector3 v = new Vector3(GetFloat(raw, off), GetFloat(raw, off + 4), GetFloat(raw, off + 8));
            off += 12;
            return v;
        }

        public static byte[] Enc(Quaternion q)
        {
            byte[] buf = new byte[16];
            PutFloat(buf, 0, q.x);
            PutFloat(buf, 4, q.y);
            PutFloat(buf, 8, q.z);
            PutFloat(buf, 12, q.w);
            return buf;
        }

        public static Quaternion DecQ(byte[] raw, ref int off)
        {
            if (raw == null || off + 16 > raw.Length) return Quaternion.identity;
            Quaternion q = new Quaternion(GetFloat(raw, off), GetFloat(raw, off + 4), GetFloat(raw, off + 8), GetFloat(raw, off + 12));
            off += 16;
            return q;
        }
    }

    // PeerList payload used by the transports themselves (opaque to sync/puppet slices):
    //   [count:int32] then per entry [peerId:uint32][steamId64:int64][isHost:byte][name:string]
    internal static class PeerListCodec
    {
        public static byte[] Encode(System.Collections.Generic.IList<PeerInfo> peers)
        {
            byte[] payload = new byte[4];
            Wire.PutInt(payload, 0, peers.Count);
            foreach (PeerInfo p in peers)
            {
                byte[] entry = new byte[4 + 8 + 1];
                Wire.PutUInt(entry, 0, p.PeerId);
                ulong steam = 0;
                if (!string.IsNullOrEmpty(p.SteamId)) ulong.TryParse(p.SteamId, out steam);
                Wire.PutLong(entry, 4, (long)steam);
                Wire.PutByte(entry, 12, p.IsHost ? (byte)1 : (byte)0);
                byte[] name = Wire.Enc(p.Name ?? "");
                byte[] joined = new byte[payload.Length + entry.Length + name.Length];
                Buffer.BlockCopy(payload, 0, joined, 0, payload.Length);
                Buffer.BlockCopy(entry, 0, joined, payload.Length, entry.Length);
                Buffer.BlockCopy(name, 0, joined, payload.Length + entry.Length, name.Length);
                payload = joined;
            }
            return payload;
        }

        public static void Decode(byte[] payload, System.Action<uint, long, bool, string> onEntry)
        {
            if (payload == null || payload.Length < 4) return;
            int count = Wire.GetInt(payload, 0);
            int off = 4;
            for (int i = 0; i < count; i++)
            {
                if (off + 13 > payload.Length) break;
                uint peerId = Wire.GetUInt(payload, off);
                long steam = Wire.GetLong(payload, off + 4);
                bool isHost = Wire.GetByte(payload, off + 12) == 1;
                off += 13;
                string name = Wire.Dec(payload, ref off);
                onEntry(peerId, steam, isHost, name);
            }
        }
    }
}
