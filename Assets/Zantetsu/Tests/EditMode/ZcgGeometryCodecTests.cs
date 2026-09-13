using System;
using System.IO;
using NUnit.Framework;
using Zantetsu.Core.Geometry;

namespace Zantetsu.Core.Tests
{
    public sealed class ZcgGeometryCodecTests
    {
        private const string GoldenHex = "5a4347310100000038000000000000000300000001000000000080400000a441000058410000b04000008441000030410000e04000008c4100005041000000000100000002000000";
        private const string ConvexGoldenHex = "5a434731020000007c0000000000000001000000040000000400000000000000000000000000000000000000000000000000803f000000000000803f000000000000803f000000000000000003000000000000000100000003000000030000000000000002000000010000000300000000000000030000000200000003000000010000000200000003000000";
        private const string AreaBelowHex = "5a434731010000003800000000000000030000000100000000000000000000000000000000000000bd37863500000000bd3786350000000000000000000000000100000002000000";
        private const string AreaAboveHex = "5a434731010000003800000000000000030000000100000000000000000000000000000000000000be37863500000000be3786350000000000000000000000000100000002000000";

        [Test]
        public void DesignGoldenDecodesAndReserializesExactly()
        {
            byte[] bytes = FromHex(GoldenHex);
            ZcgDocument document = ZcgGeometryCodec.Read(bytes, ZcgDecodeLimits.Phase02);
            Assert.That(document.Kind, Is.EqualTo(ZcgGeometryKind.TriangleMesh));
            Assert.That(document.Positions.Length, Is.EqualTo(3));
            Assert.That(document.Triangles.Length, Is.EqualTo(1));
            Assert.That(ZcgGeometryCodec.WriteCanonical(document), Is.EqualTo(bytes));
            Assert.That(ZcgGeometryCodec.ComputeSha256(bytes),
                Is.EqualTo("5210748ea4fe7a8f349b52e919af7dd1aad4c542a91fb741806bf517f2426cdb"));
        }

        [Test]
        public void NonzeroReservedTrailingDataAndNegativeZeroAreRejected()
        {
            byte[] bytes = FromHex(GoldenHex);
            byte[] reserved = (byte[])bytes.Clone(); reserved[5] = 1;
            Assert.Throws<InvalidDataException>(() => ZcgGeometryCodec.Read(reserved, ZcgDecodeLimits.Phase02));
            byte[] trailing = new byte[bytes.Length + 1]; Array.Copy(bytes, trailing, bytes.Length);
            Assert.Throws<InvalidDataException>(() => ZcgGeometryCodec.Read(trailing, ZcgDecodeLimits.Phase02));
            byte[] negativeZero = (byte[])bytes.Clone(); negativeZero[24] = 0; negativeZero[25] = 0; negativeZero[26] = 0; negativeZero[27] = 128;
            Assert.Throws<InvalidDataException>(() => ZcgGeometryCodec.Read(negativeZero, ZcgDecodeLimits.Phase02));
        }

        [Test]
        public void DeclaredCountsAreBoundedBeforeAllocation()
        {
            byte[] bytes = new byte[24];
            bytes[0] = (byte)'Z'; bytes[1] = (byte)'C'; bytes[2] = (byte)'G'; bytes[3] = (byte)'1'; bytes[4] = 1;
            bytes[8] = 8;
            bytes[16] = 201; bytes[20] = 1;
            var limits = new ZcgDecodeLimits(1024, 200, 200, 1, 255);
            Assert.Throws<InvalidDataException>(() => ZcgGeometryCodec.Read(bytes, limits));
        }

        [Test]
        public void PythonConvexGoldenDecodesAndReserializesExactly()
        {
            byte[] bytes = FromHex(ConvexGoldenHex);
            ZcgDocument document = ZcgGeometryCodec.Read(bytes, ZcgDecodeLimits.Phase02);
            Assert.That(document.Kind, Is.EqualTo(ZcgGeometryKind.ConvexSet));
            Assert.That(document.Hulls.Length, Is.EqualTo(1));
            Assert.That(document.Hulls[0].Positions.Length, Is.EqualTo(4));
            Assert.That(ZcgGeometryCodec.WriteCanonical(document), Is.EqualTo(bytes));
            Assert.That(ZcgGeometryCodec.ComputeSha256(bytes),
                Is.EqualTo("8fd87b43e332c67430d5ae1692d18a29b42e7c7bcdda514f1008c7b32c81396f"));
        }

        [Test]
        public void Binary32OneUlpAreaBoundaryMatchesPython()
        {
            Assert.Throws<InvalidDataException>(() =>
                ZcgGeometryCodec.Read(FromHex(AreaBelowHex), ZcgDecodeLimits.Phase02));
            Assert.DoesNotThrow(() =>
                ZcgGeometryCodec.Read(FromHex(AreaAboveHex), ZcgDecodeLimits.Phase02));
        }

        private static byte[] FromHex(string value)
        {
            byte[] result = new byte[value.Length / 2];
            for (int i = 0; i < result.Length; i++) result[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
            return result;
        }
    }
}
