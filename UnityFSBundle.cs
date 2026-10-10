using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AssetStudioExporter;

/// <summary>
/// UnityFS (AssetBundle) 容器解析器.
/// <para>
/// AssetsTools.NET 3.0.0 无法解析本作的 <c>UnityFS format 8</c> + flags <c>0x243</c>
/// (BlocksAndDirectoryInfoCombined + BlockInfoNeedPaddingAtStart) bundle,
/// 因此这里自行实现容器层: 解出数据块与文件节点 (SerializedFile / resS 等),
/// 再重组为无压缩的简单 bundle 交给 AssetsTools 处理 AssetsFile/类型树/导出.
/// </para>
/// </summary>
public sealed class UnityFSBundle
{
    public sealed class Block
    {
        public uint CompressedSize;
        public uint UncompressedSize;
        public ushort Flags;
        public int Compression => Flags & 0x3f;
    }

    public sealed class Node
    {
        public long Offset;
        public long Size;
        public uint Flags;
        public string Name = "";
    }

    public string Signature = "UnityFS";
    public uint FormatVersion;
    public string GenerationVersion = "";
    public string EngineVersion = "";

    public List<Block> Blocks = new();
    public List<Node> Nodes = new();

    /// <summary>输出诊断信息</summary>
    public static bool Debug = false;

    /// <summary>解压并拼接所有 block 后的完整数据</summary>
    public byte[] Data = Array.Empty<byte>();

    public static UnityFSBundle Read(Stream stream)
    {
        var ret = new UnityFSBundle();

        string ReadCStringBE(BinaryReader br)
        {
            var sb = new StringBuilder();
            byte b;
            while ((b = br.ReadByte()) != 0)
                sb.Append((char)b);
            return sb.ToString();
        }
        static int Be32(byte[] d, int p) => (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
        static long Be64(byte[] d, int p)
        {
            long v = 0;
            for (int i = 0; i < 8; i++) v = (v << 8) | d[p + i];
            return v;
        }
        static ushort Be16(byte[] d, int p) => (ushort)((d[p] << 8) | d[p + 1]);
        static string ReadCString(byte[] d, ref int p)
        {
            int s = p;
            while (p < d.Length && d[p] != 0) p++;
            var r = Encoding.ASCII.GetString(d, s, p - s);
            p++;
            return r;
        }

        long start = stream.Position;
        using var br = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        ret.Signature = ReadCStringBE(br);
        ret.FormatVersion = ReadBE32(br);
        ret.GenerationVersion = ReadCStringBE(br);
        ret.EngineVersion = ReadCStringBE(br);

        long totalSize = ReadBE64(br);
        uint cbiSize = ReadBE32(br);
        uint ubiSize = ReadBE32(br);
        uint flags = ReadBE32(br);

        if ((flags & 0x80) != 0) // BlockAndDirAtEnd
        {
            stream.Position = start + totalSize - cbiSize;
        }
        else
        {
            long pos = stream.Position;
            if (ret.FormatVersion >= 7)
                pos = (pos + 15) & ~15L;
            if ((flags & 0x400) != 0) // 变体: 数据块前也有对齐
                pos = (pos + 15) & ~15L;
            stream.Position = pos; // 修正: 必须真正移动读取位置
        }

        byte[] infoBytes = br.ReadBytes((int)cbiSize);
        long dataStart = stream.Position;
        if ((flags & 0x200) != 0) // BlockInfoNeedPaddingAtStart
            dataStart = (dataStart + 15) & ~15L;

        if (Debug) Console.WriteLine($"[UnityFS] cbi={cbiSize} ubi={ubiSize} flags=0x{flags:X} comp={flags & 0x3f} infoBytes={infoBytes.Length} dataStart={dataStart}");
        byte[] info = Decompress(infoBytes, (int)(flags & 0x3f), (int)ubiSize);
        if (Debug) Console.WriteLine($"[UnityFS] info.Length={info.Length} head={BitConverter.ToString(info.Take(32).ToArray())}");

        // 注意: blocksAndDirectoryInfo 与 bundle 头部同为 BIG ENDIAN (AT3 亦如此)
        int p = 16; // uncompressed data hash
        int blockCount = Be32(info, p); p += 4;
        for (int i = 0; i < blockCount; i++)
        {
            ret.Blocks.Add(new Block
            {
                UncompressedSize = (uint)Be32(info, p),
                CompressedSize = (uint)Be32(info, p + 4),
                Flags = Be16(info, p + 8)
            });
            p += 10;
        }
        int nodeCount = Be32(info, p); p += 4;
        for (int i = 0; i < nodeCount; i++)
        {
            var n = new Node
            {
                Offset = Be64(info, p),
                Size = Be64(info, p + 8),
                Flags = (uint)Be32(info, p + 16)
            };
            p += 20;
            n.Name = ReadCString(info, ref p);
            ret.Nodes.Add(n);
        }

        stream.Position = dataStart;
        using var ms = new MemoryStream(ret.Blocks.Sum(b => (int)b.UncompressedSize));
        foreach (var b in ret.Blocks)
        {
            byte[] comp = br.ReadBytes((int)b.CompressedSize);
            ms.Write(Decompress(comp, b.Compression, (int)b.UncompressedSize), 0, (int)b.UncompressedSize);
        }
        ret.Data = ms.ToArray();
        return ret;
    }

    /// <summary>把解析出的数据重组为无压缩 bundle (format 7), 让 AssetsTools 无条件可读</summary>
    public static MemoryStream BuildSimpleBundle(string genVer, string engineVer, string[] names, (long offset, long size)[] ranges, byte[] data)
    {
        var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        void WriteCString(string s) => w.Write(Encoding.ASCII.GetBytes(s + "\0"));
        void WriteBE32(uint v) { var b = BitConverter.GetBytes(v); Array.Reverse(b); w.Write(b); }
        void WriteBE64(long v) { var b = BitConverter.GetBytes(v); Array.Reverse(b); w.Write(b); }

        byte[] info;
        using (var iw = new MemoryStream())
        {
            iw.Write(new byte[16]);          // uncompressed data hash
            Action<uint> w32 = v => { var b = BitConverter.GetBytes(v); Array.Reverse(b); iw.Write(b, 0, 4); };
            Action<long> w64 = v => { var b = BitConverter.GetBytes(v); Array.Reverse(b); iw.Write(b, 0, 8); };
            Action<ushort> w16 = v => { var b = BitConverter.GetBytes(v); Array.Reverse(b); iw.Write(b, 0, 2); };

            w32(1);                          // block count
            w32((uint)data.Length);          // uncompressed size
            w32((uint)data.Length);          // compressed size
            w16(0);                          // flags: 未压缩
            w32((uint)names.Length);         // dir count
            for (int i = 0; i < names.Length; i++)
            {
                w64(ranges[i].offset);
                w64(ranges[i].size);
                w32((uint)(names[i].EndsWith(".resS") ? 1 : 0));
                iw.Write(Encoding.ASCII.GetBytes(names[i] + "\0"));
            }
            info = iw.ToArray();
        }

        long sizePos = 8 + 4 + genVer.Length + 1 + engineVersionLength(engineVer) + 1;
        WriteCString("UnityFS");
        WriteBE32(7);
        WriteCString(genVer);
        WriteCString(engineVer);
        long sizeFieldPos = ms.Position;
        WriteBE64(0);
        WriteBE32((uint)info.Length);
        WriteBE32((uint)info.Length);
        WriteBE32(0x40); // HasDirectoryInfo, 无压缩

        while (ms.Position % 16 != 0) w.Write((byte)0);   // 头部对齐 (version>=7)
        w.Write(info);
        // 注意: 不要在 info 与 data 之间再对齐, AT3 的 GetFileDataOffset 假定数据紧跟在 info 之后
        w.Write(data);

        long endPos = ms.Position;
        ms.Position = sizeFieldPos;
        WriteBE64(endPos);
        ms.Position = endPos;
        ms.Position = 0;
        _ = sizePos;
        return ms;

        static int engineVersionLength(string s) => s.Length;
    }

    public static byte[] Decompress(byte[] data, int comp, int dstSize) => comp switch
    {
        0 => data,
        2 or 3 => Lz4(data, dstSize),
        1 => Lzma(data, dstSize),
        _ => throw new NotSupportedException($"unsupported bundle compression {comp}")
    };

    private static byte[] Lz4(byte[] src, int dstSize)
    {
        using var inMs = new MemoryStream(src, 0, src.Length, false);
        using var dec = new AssetsTools.NET.Extra.Decompressors.LZ4.Lz4DecoderStream(inMs, dstSize);
        var res = new byte[dstSize];
        int n = dec.Read(res, 0, dstSize);
        if (n != dstSize) throw new InvalidDataException($"LZ4 size mismatch {n} != {dstSize}");
        return res;
    }

    private static byte[] Lzma(byte[] src, int dstSize)
    {
        // Unity LZMA 封装: 12 字节头 + LZMA 流
        using var inMs = new MemoryStream(src, 12, src.Length - 12, false);
        using var resMs = new MemoryStream(dstSize);
        SevenZip.Compression.LZMA.SevenZipHelper.StreamDecompress(inMs, resMs, src.Length - 12, dstSize);
        var res = resMs.ToArray();
        return res.Length == dstSize ? res : throw new InvalidDataException("LZMA size mismatch");
    }

    #region big-endian helpers

    private static uint ReadBE32(BinaryReader br) { var b = br.ReadBytes(4); Array.Reverse(b); return BitConverter.ToUInt32(b, 0); }
    private static long ReadBE64(BinaryReader br) { var b = br.ReadBytes(8); Array.Reverse(b); return BitConverter.ToInt64(b, 0); }

    #endregion
}
