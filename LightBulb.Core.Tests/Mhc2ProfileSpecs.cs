using System;
using System.IO;
using System.Security.Cryptography;
using FluentAssertions;
using Xunit;
using static LightBulb.Core.Tests.HdrProfileTestData;

namespace LightBulb.Core.Tests;

public class Mhc2ProfileSpecs
{
    [Fact]
    public void I_generate_aligned_ICC_tags_and_required_HDR_metadata()
    {
        var bytes = NeutralProfile();
        ReadUInt(bytes, 0).Should().Be((uint)bytes.Length);
        ReadUInt(bytes, 36).Should().Be(Signature("acsp"));
        var tags = Tags(bytes);
        foreach (
            var name in new[]
            {
                "rXYZ",
                "gXYZ",
                "bXYZ",
                "wtpt",
                "lumi",
                "rTRC",
                "gTRC",
                "bTRC",
                "desc",
                "cprt",
                "MHC2",
            }
        )
            tags.Should().ContainKey(Signature(name));
        for (var i = 0; i < ReadUInt(bytes, 128); i++)
            (ReadUInt(bytes, 132 + i * 12 + 4) % 4).Should().Be(0);

        var mhc = tags[Signature("MHC2")];
        ReadFixed(mhc, 12).Should().Be(Metadata.MinimumLuminance);
        ReadFixed(mhc, 16).Should().Be(Metadata.PeakLuminance);
        ReadFixed(tags[Signature("lumi")], 12).Should().Be(Metadata.FullFrameLuminance);
        var matrix = Matrix(bytes);
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
            matrix[row, column].Should().Be(row == column ? 1 : 0);

        var idInput = (byte[])bytes.Clone();
        idInput.AsSpan(44, 4).Clear();
        idInput.AsSpan(64, 4).Clear();
        idInput.AsSpan(84, 16).Clear();
        bytes.AsSpan(84, 16).ToArray().Should().Equal(MD5.HashData(idInput));
    }

    [Fact]
    public void I_apply_sRGB_white_point_gains_in_linear_XYZ_instead_of_multiplying_PQ_values()
    {
        var configuration = new ColorConfiguration(3900, 0.85);
        var matrix = Matrix(Mhc2Profile.Create([], Metadata, configuration, "Warm"));
        var srgb = new double[,]
        {
            { 0.4124564, 0.3575761, 0.1804375 },
            { 0.2126729, 0.7151522, 0.0721750 },
            { 0.0193339, 0.1191920, 0.9503041 },
        };
        var (r, g, b) = configuration.GetGammaMultipliers();
        double Decode(double value) =>
            value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        var gains = new[] { Decode(r), Decode(g), Decode(b) };
        var white = new double[3];
        for (var row = 0; row < 3; row++)
        for (var i = 0; i < 3; i++)
            white[row] += srgb[row, i];
        for (var row = 0; row < 3; row++)
        {
            double actual = 0,
                expected = 0;
            for (var i = 0; i < 3; i++)
            {
                actual += matrix[row, i] * white[i];
                expected += srgb[row, i] * gains[i];
            }
            actual.Should().BeApproximately(expected, 4e-5);
        }
    }

    [Fact]
    public void I_preserve_existing_HDR_metadata_LUTs_and_calibration_matrix_composition()
    {
        var original = NeutralProfile();
        var mhc = new byte[132];
        Tags(original)[Signature("MHC2")].CopyTo(mhc, 0);
        WriteUInt(mhc, 8, 2);
        WriteFixed(mhc, 12, 0.01);
        WriteFixed(mhc, 16, 800);
        WriteFixed(mhc, 36, 1.05);
        WriteFixed(mhc, 40, 0.02);
        for (var channel = 0; channel < 3; channel++)
        {
            var offset = 84 + channel * 16;
            WriteUInt(mhc, 24 + channel * 4, (uint)offset);
            WriteUInt(mhc, offset, Signature("sf32"));
            WriteFixed(mhc, offset + 8, 0);
            WriteFixed(mhc, offset + 12, 0.9 - channel * 0.1);
        }
        original = ReplaceTag(original, "MHC2", mhc);
        var untouched = (byte[])original.Clone();
        var configuration = new ColorConfiguration(3500, 0.8);
        var adjusted = Mhc2Profile.Create(original, Metadata, configuration, "Filtered");
        original.Should().Equal(untouched);

        var before = Tags(original);
        var after = Tags(adjusted);
        foreach (var (tag, value) in before)
            if (tag != Signature("MHC2") && tag != Signature("desc"))
                after[tag].Should().Equal(value);
        var newMhc = after[Signature("MHC2")];
        newMhc.AsSpan(12, 8).ToArray().Should().Equal(mhc.AsSpan(12, 8).ToArray());
        for (var channel = 0; channel < 3; channel++)
            newMhc
                .AsSpan((int)ReadUInt(newMhc, 24 + channel * 4), 16)
                .ToArray()
                .Should()
                .Equal(mhc.AsSpan(84 + channel * 16, 16).ToArray());

        var calibration = Matrix(original);
        var filter = Matrix(Mhc2Profile.Create([], Metadata, configuration, "Filter"));
        var combined = Matrix(adjusted);
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
        {
            double expected = 0;
            for (var i = 0; i < 3; i++)
                expected += calibration[row, i] * filter[i, column];
            combined[row, column].Should().BeApproximately(expected, 4e-5);
        }
    }

    [Fact]
    public void I_reject_truncated_or_invalid_existing_calibration_without_falling_back()
    {
        Action truncated = () =>
            Mhc2Profile.Create(new byte[128], Metadata, ColorConfiguration.Default, "Invalid");
        truncated.Should().Throw<InvalidDataException>();
        var original = NeutralProfile();
        var mhc = Tags(original)[Signature("MHC2")];
        WriteUInt(mhc, 8, 4097);
        original = ReplaceTag(original, "MHC2", mhc);
        Action tooLarge = () =>
            Mhc2Profile.Create(original, Metadata, ColorConfiguration.Default, "Invalid");
        tooLarge.Should().Throw<InvalidDataException>();
        Action noMetadata = () =>
            Mhc2Profile.Create(
                [],
                Metadata with
                {
                    PeakLuminance = 0,
                },
                ColorConfiguration.Default,
                "Invalid"
            );
        noMetadata.Should().Throw<InvalidDataException>();
    }
}
