using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LightBulb.PlatformInterop;

namespace LightBulb.Core.Tests;

internal static class HdrProfileTestData
{
    public static HdrDisplayMetadata Metadata { get; } =
        new(0.68, 0.32, 0.265, 0.69, 0.15, 0.06, 0.3127, 0.329, 0, 1000, 250);

    public static DisplayDevice Display { get; } =
        new(
            "stable-monitor-id",
            @"\\.\DISPLAY1",
            "Test HDR display",
            "test-device-key",
            1,
            0,
            true,
            false,
            true,
            Metadata
        );

    public static byte[] NeutralProfile() =>
        Mhc2Profile.Create([], Metadata, ColorConfiguration.Default, "Original profile");

    public static uint Signature(string text) =>
        BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes(text));

    public static uint ReadUInt(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));

    public static void WriteUInt(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);

    public static double ReadFixed(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset)) / 65536.0;

    public static void WriteFixed(byte[] bytes, int offset, double value) =>
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset), (int)Math.Round(value * 65536));

    public static IReadOnlyDictionary<uint, byte[]> Tags(byte[] profile)
    {
        var tags = new Dictionary<uint, byte[]>();
        for (var i = 0; i < ReadUInt(profile, 128); i++)
        {
            var index = 132 + i * 12;
            tags.Add(
                ReadUInt(profile, index),
                profile
                    .AsSpan((int)ReadUInt(profile, index + 4), (int)ReadUInt(profile, index + 8))
                    .ToArray()
            );
        }
        return tags;
    }

    public static byte[] ReplaceTag(byte[] profile, string name, byte[] replacement)
    {
        var tags = new Dictionary<uint, byte[]>(Tags(profile)) { [Signature(name)] = replacement };
        var bytes = new byte[132 + tags.Count * 12 + tags.Values.Sum(t => (t.Length + 3) & ~3)];
        profile.AsSpan(0, 128).CopyTo(bytes);
        WriteUInt(bytes, 0, (uint)bytes.Length);
        WriteUInt(bytes, 128, (uint)tags.Count);
        var index = 132;
        var offset = 132 + tags.Count * 12;
        foreach (var (signature, tag) in tags)
        {
            WriteUInt(bytes, index, signature);
            WriteUInt(bytes, index + 4, (uint)offset);
            WriteUInt(bytes, index + 8, (uint)tag.Length);
            tag.CopyTo(bytes, offset);
            index += 12;
            offset += (tag.Length + 3) & ~3;
        }
        return bytes;
    }

    public static double[,] Matrix(byte[] profile)
    {
        var tag = Tags(profile)[Signature("MHC2")];
        var offset = (int)ReadUInt(tag, 20);
        var matrix = new double[3, 3];
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
            matrix[row, column] = ReadFixed(tag, offset + (row * 4 + column) * 4);
        return matrix;
    }
}
