using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;

namespace ArisenEngine.Rendering.Resources;

public readonly record struct CookedEnvironmentLighting(
    EnvironmentTextureAsset Asset,
    string Variant,
    uint IrradianceWidth,
    uint IrradianceHeight,
    int IrradianceMipCount,
    int IrradianceDataOffset,
    int IrradianceDataSize,
    uint SpecularWidth,
    uint SpecularHeight,
    int SpecularMipCount,
    int SpecularDataOffset,
    int SpecularDataSize,
    uint BrdfWidth,
    uint BrdfHeight,
    int BrdfMipCount,
    int BrdfDataOffset,
    int BrdfDataSize,
    CookedAssetHandle Handle)
{
    public bool IsValid => Handle.IsValid;
}

public static class EnvironmentLightingAssetCooker
{
    public const uint IrradianceWidth = 32;
    public const uint IrradianceHeight = 16;
    public const uint SpecularWidth = 128;
    public const uint SpecularHeight = 64;
    public const uint BrdfWidth = 128;
    public const uint BrdfHeight = 128;
    public const int IrradianceSampleCount = 128;
    public const int SpecularSampleCount = 64;
    public const int BrdfSampleCount = 128;
    public const string CookedVariant = "ibl.latlong.rgba16f.v1";
    public const int CookedFormatVersion = 1;

    private const int HeaderSize = 96;
    private const int BytesPerPixel = 8;
    private const float TwoPi = MathF.PI * 2.0f;
    private static readonly byte[] s_Magic = Encoding.ASCII.GetBytes("ARIENVIB");

    public static CookedEnvironmentLighting LoadOrCook(
        IAssetDatabase assetDatabase,
        Guid environmentTextureGuid)
    {
        var asset = EnvironmentTextureAssetLoader.Load(assetDatabase, environmentTextureGuid);
        return LoadOrCook(assetDatabase, asset);
    }

    public static CookedEnvironmentLighting LoadOrCook(
        IAssetDatabase assetDatabase,
        EnvironmentTextureAsset asset)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        ArgumentNullException.ThrowIfNull(asset);

        if (!assetDatabase.CanReadSourceAssets)
        {
            return LoadCooked(assetDatabase, asset);
        }

        if (!assetDatabase.TryGetAsset(asset.Guid, out var sourceAsset))
        {
            throw new InvalidOperationException(
                $"[EnvironmentLightingAssetCooker] Environment texture asset '{asset.Guid}' was not found.");
        }

        var dependencyWriteTimeUtc = AssetDependencyTracker.GetEnvironmentTextureDependencyWriteTimeUtc(
            assetDatabase,
            asset);

        if (!assetDatabase.TryGetCookedArtifact(
                asset.Guid,
                CookedVariant,
                out CookedAssetRecord current) ||
            !File.Exists(current.Path) ||
            File.GetLastWriteTimeUtc(current.Path) < dependencyWriteTimeUtc)
        {
            using CookedArtifactWrite write = assetDatabase.BeginCookedArtifactWrite(
                asset.Guid,
                CookedVariant,
                ".environmentlighting");
            CookLighting(assetDatabase, asset, write.OutputPath);

            var outputInfo = new FileInfo(write.OutputPath);
            if (!outputInfo.Exists || outputInfo.Length <= HeaderSize)
            {
                throw new InvalidOperationException(
                    $"[EnvironmentLightingAssetCooker] Environment texture asset '{asset.Guid}' produced no IBL payload.");
            }

            write.Commit(sourceAsset.AssetType);
        }

        return LoadCooked(assetDatabase, asset);
    }

    private static CookedEnvironmentLighting LoadCooked(
        IAssetDatabase assetDatabase,
        EnvironmentTextureAsset asset)
    {
        if (!assetDatabase.TryLoadCookedAsset(
                asset.Guid,
                CookedVariant,
                EnvironmentTextureAssetLoader.EnvironmentTextureAssetType,
                out var handle))
        {
            throw new InvalidOperationException(
                $"[EnvironmentLightingAssetCooker] Cooked IBL resources for environment " +
                $"'{asset.Guid}' variant '{CookedVariant}' are unavailable.");
        }

        try
        {
            var header = ReadHeader(assetDatabase.GetCookedAssetBytes(handle).Span);
            if (header.EnvironmentGuid != asset.Guid)
            {
                throw new InvalidOperationException(
                    $"[EnvironmentLightingAssetCooker] Cooked IBL payload belongs to environment '{header.EnvironmentGuid}', expected '{asset.Guid}'.");
            }

            var irradianceOffset = HeaderSize;
            var specularOffset = checked(irradianceOffset + header.IrradianceDataSize);
            var brdfOffset = checked(specularOffset + header.SpecularDataSize);
            return new CookedEnvironmentLighting(
                asset,
                CookedVariant,
                header.IrradianceWidth,
                header.IrradianceHeight,
                header.IrradianceMipCount,
                irradianceOffset,
                header.IrradianceDataSize,
                header.SpecularWidth,
                header.SpecularHeight,
                header.SpecularMipCount,
                specularOffset,
                header.SpecularDataSize,
                header.BrdfWidth,
                header.BrdfHeight,
                header.BrdfMipCount,
                brdfOffset,
                header.BrdfDataSize,
                handle);
        }
        catch
        {
            assetDatabase.Release(handle);
            throw;
        }
    }

    public static ReadOnlySpan<byte> GetIrradiancePixelData(ReadOnlyMemory<byte> bytes)
    {
        var header = ReadHeader(bytes.Span);
        return bytes.Span.Slice(HeaderSize, header.IrradianceDataSize);
    }

    public static ReadOnlySpan<byte> GetSpecularPixelData(ReadOnlyMemory<byte> bytes)
    {
        var header = ReadHeader(bytes.Span);
        var offset = checked(HeaderSize + header.IrradianceDataSize);
        return bytes.Span.Slice(offset, header.SpecularDataSize);
    }

    public static ReadOnlySpan<byte> GetBrdfPixelData(ReadOnlyMemory<byte> bytes)
    {
        var header = ReadHeader(bytes.Span);
        var offset = checked(HeaderSize + header.IrradianceDataSize + header.SpecularDataSize);
        return bytes.Span.Slice(offset, header.BrdfDataSize);
    }

    public static int GetPackedMipDataSize(uint width, uint height, int mipCount)
    {
        if (width == 0 || height == 0 || mipCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mipCount));
        }

        var size = 0;
        for (int mipLevel = 0; mipLevel < mipCount; mipLevel++)
        {
            var mipWidth = Math.Max(1u, width >> mipLevel);
            var mipHeight = Math.Max(1u, height >> mipLevel);
            size = checked(size + checked((int)(mipWidth * mipHeight * BytesPerPixel)));
        }

        return size;
    }

    private static void CookLighting(
        IAssetDatabase assetDatabase,
        EnvironmentTextureAsset asset,
        string outputPath)
    {
        if (asset.Variant.Layout != EnvironmentTextureLayout.LatLong ||
            asset.Variant.Format != EnvironmentTextureCookedFormat.R16G16B16A16SFloat)
        {
            throw new NotSupportedException(
                $"[EnvironmentLightingAssetCooker] Environment variant '{asset.Variant.GetCookedVariant()}' is not supported.");
        }

        var startedAt = Stopwatch.GetTimestamp();
        var source = LoadLinearSource(assetDatabase, asset);
        var specularMipCount = GetFullMipCount(SpecularWidth, SpecularHeight);
        var irradiancePixels = GenerateDiffuseIrradiance(source);
        var specularPixels = GeneratePrefilteredSpecular(source, specularMipCount);
        var brdfPixels = GenerateBrdfIntegrationLut();
        var irradianceBytes = EncodeRgba16Float(irradiancePixels);
        var specularBytes = EncodeRgba16Float(specularPixels);
        var brdfBytes = EncodeRgba16Float(brdfPixels);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var stream = File.Create(outputPath);
        stream.Write(s_Magic);
        WriteInt32(stream, CookedFormatVersion);
        WriteInt32(stream, checked((int)IrradianceWidth));
        WriteInt32(stream, checked((int)IrradianceHeight));
        WriteInt32(stream, 1);
        WriteInt32(stream, irradianceBytes.Length);
        WriteInt32(stream, checked((int)SpecularWidth));
        WriteInt32(stream, checked((int)SpecularHeight));
        WriteInt32(stream, specularMipCount);
        WriteInt32(stream, specularBytes.Length);
        WriteInt32(stream, checked((int)BrdfWidth));
        WriteInt32(stream, checked((int)BrdfHeight));
        WriteInt32(stream, 1);
        WriteInt32(stream, brdfBytes.Length);
        WriteInt32(stream, checked(irradianceBytes.Length + specularBytes.Length + brdfBytes.Length));
        Span<byte> environmentGuidBytes = stackalloc byte[16];
        asset.Guid.TryWriteBytes(environmentGuidBytes);
        stream.Write(environmentGuidBytes);
        Span<byte> reserved = stackalloc byte[16];
        reserved.Clear();
        stream.Write(reserved);
        stream.Write(irradianceBytes);
        stream.Write(specularBytes);
        stream.Write(brdfBytes);

        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        Logger.Log(
            $"[EnvironmentLightingAssetCooker] Cooked IBL resources | Guid: {asset.Guid} | Irradiance: {IrradianceWidth}x{IrradianceHeight} | Specular: {SpecularWidth}x{SpecularHeight} mips={specularMipCount} | BRDF: {BrdfWidth}x{BrdfHeight} | Elapsed: {elapsed.TotalMilliseconds:0.###} ms | Output: {outputPath}");
    }

    private static LinearEnvironmentSource LoadLinearSource(
        IAssetDatabase assetDatabase,
        EnvironmentTextureAsset asset)
    {
        var cookedSource = EnvironmentTextureAssetCooker.LoadOrCook(assetDatabase, asset);
        try
        {
            var sourceBytes = EnvironmentTextureAssetCooker.GetPixelData(
                assetDatabase.GetCookedAssetBytes(cookedSource.Handle));
            var expectedValueCount = checked((int)(cookedSource.Width * cookedSource.Height * 4));
            if (sourceBytes.Length != checked(expectedValueCount * sizeof(ushort)))
            {
                throw new InvalidOperationException(
                    "[EnvironmentLightingAssetCooker] Cooked environment source has an invalid half-float payload size.");
            }

            var pixels = new float[expectedValueCount];
            for (int i = 0; i < pixels.Length; i++)
            {
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(sourceBytes.Slice(i * sizeof(ushort), sizeof(ushort)));
                pixels[i] = (float)BitConverter.UInt16BitsToHalf(bits);
            }

            return new LinearEnvironmentSource(cookedSource.Width, cookedSource.Height, pixels);
        }
        finally
        {
            assetDatabase.Release(cookedSource.Handle);
        }
    }

    private static float[] GenerateDiffuseIrradiance(LinearEnvironmentSource source)
    {
        var output = new float[checked((int)(IrradianceWidth * IrradianceHeight * 4))];
        for (uint y = 0; y < IrradianceHeight; y++)
        {
            for (uint x = 0; x < IrradianceWidth; x++)
            {
                var normal = LatLongTexelDirection(x, y, IrradianceWidth, IrradianceHeight);
                BuildTangentBasis(normal, out var tangent, out var bitangent);
                var accumulated = Vector3.Zero;
                for (uint sampleIndex = 0; sampleIndex < IrradianceSampleCount; sampleIndex++)
                {
                    var xi = Hammersley(sampleIndex, IrradianceSampleCount);
                    var radius = MathF.Sqrt(xi.X);
                    var phi = TwoPi * xi.Y;
                    var local = new Vector3(
                        radius * MathF.Cos(phi),
                        radius * MathF.Sin(phi),
                        MathF.Sqrt(MathF.Max(0.0f, 1.0f - xi.X)));
                    var direction = Vector3.Normalize(
                        tangent * local.X + bitangent * local.Y + normal * local.Z);
                    accumulated += SampleLatLong(source, direction);
                }

                WritePixel(output, x, y, IrradianceWidth, accumulated / IrradianceSampleCount);
            }
        }

        return output;
    }

    private static float[] GeneratePrefilteredSpecular(
        LinearEnvironmentSource source,
        int mipCount)
    {
        var valueCount = checked(GetPackedMipDataSize(SpecularWidth, SpecularHeight, mipCount) / sizeof(ushort));
        var output = new float[valueCount];
        var outputPixelOffset = 0;

        for (int mipLevel = 0; mipLevel < mipCount; mipLevel++)
        {
            var mipWidth = Math.Max(1u, SpecularWidth >> mipLevel);
            var mipHeight = Math.Max(1u, SpecularHeight >> mipLevel);
            var roughness = mipCount == 1 ? 0.0f : mipLevel / (float)(mipCount - 1);

            for (uint y = 0; y < mipHeight; y++)
            {
                for (uint x = 0; x < mipWidth; x++)
                {
                    var reflection = LatLongTexelDirection(x, y, mipWidth, mipHeight);
                    Vector3 filtered;
                    if (mipLevel == 0)
                    {
                        filtered = SampleLatLong(source, reflection);
                    }
                    else
                    {
                        var accumulated = Vector3.Zero;
                        var totalWeight = 0.0f;
                        for (uint sampleIndex = 0; sampleIndex < SpecularSampleCount; sampleIndex++)
                        {
                            var halfVector = ImportanceSampleGgx(
                                Hammersley(sampleIndex, SpecularSampleCount),
                                reflection,
                                roughness);
                            var light = Vector3.Normalize(
                                2.0f * Vector3.Dot(reflection, halfVector) * halfVector - reflection);
                            var normalDotLight = MathF.Max(Vector3.Dot(reflection, light), 0.0f);
                            if (normalDotLight <= 0.0f)
                            {
                                continue;
                            }

                            accumulated += SampleLatLong(source, light) * normalDotLight;
                            totalWeight += normalDotLight;
                        }

                        filtered = totalWeight > 0.0f
                            ? accumulated / totalWeight
                            : SampleLatLong(source, reflection);
                    }

                    WritePixel(output, outputPixelOffset++, filtered);
                }
            }
        }

        return output;
    }

    private static float[] GenerateBrdfIntegrationLut()
    {
        var output = new float[checked((int)(BrdfWidth * BrdfHeight * 4))];
        for (uint y = 0; y < BrdfHeight; y++)
        {
            var roughness = (y + 0.5f) / BrdfHeight;
            for (uint x = 0; x < BrdfWidth; x++)
            {
                var normalDotView = (x + 0.5f) / BrdfWidth;
                var view = new Vector3(
                    MathF.Sqrt(MathF.Max(0.0f, 1.0f - normalDotView * normalDotView)),
                    0.0f,
                    normalDotView);
                var integratedA = 0.0f;
                var integratedB = 0.0f;

                for (uint sampleIndex = 0; sampleIndex < BrdfSampleCount; sampleIndex++)
                {
                    var halfVector = ImportanceSampleGgx(
                        Hammersley(sampleIndex, BrdfSampleCount),
                        Vector3.UnitZ,
                        roughness);
                    var light = Vector3.Normalize(
                        2.0f * Vector3.Dot(view, halfVector) * halfVector - view);
                    var normalDotLight = MathF.Max(light.Z, 0.0f);
                    var normalDotHalf = MathF.Max(halfVector.Z, 0.0f);
                    var viewDotHalf = MathF.Max(Vector3.Dot(view, halfVector), 0.0f);
                    if (normalDotLight <= 0.0f || normalDotHalf <= 0.0f)
                    {
                        continue;
                    }

                    var geometry = GeometrySmith(normalDotView, normalDotLight, roughness);
                    var visibility = geometry * viewDotHalf /
                        MathF.Max(normalDotHalf * normalDotView, 1.0e-5f);
                    var fresnel = MathF.Pow(1.0f - viewDotHalf, 5.0f);
                    integratedA += (1.0f - fresnel) * visibility;
                    integratedB += fresnel * visibility;
                }

                var pixelIndex = checked((int)((y * BrdfWidth + x) * 4));
                output[pixelIndex + 0] = integratedA / BrdfSampleCount;
                output[pixelIndex + 1] = integratedB / BrdfSampleCount;
                output[pixelIndex + 2] = 0.0f;
                output[pixelIndex + 3] = 1.0f;
            }
        }

        return output;
    }

    private static Vector3 ImportanceSampleGgx(Vector2 xi, Vector3 normal, float roughness)
    {
        var alpha = MathF.Max(roughness * roughness, 1.0e-4f);
        var alphaSquared = alpha * alpha;
        var phi = TwoPi * xi.X;
        var cosTheta = MathF.Sqrt(
            MathF.Max(0.0f, (1.0f - xi.Y) / (1.0f + (alphaSquared - 1.0f) * xi.Y)));
        var sinTheta = MathF.Sqrt(MathF.Max(0.0f, 1.0f - cosTheta * cosTheta));
        var local = new Vector3(
            MathF.Cos(phi) * sinTheta,
            MathF.Sin(phi) * sinTheta,
            cosTheta);
        BuildTangentBasis(normal, out var tangent, out var bitangent);
        return Vector3.Normalize(tangent * local.X + bitangent * local.Y + normal * local.Z);
    }

    private static float GeometrySmith(float normalDotView, float normalDotLight, float roughness)
    {
        var k = roughness * roughness * 0.5f;
        var view = normalDotView /
            MathF.Max(normalDotView * (1.0f - k) + k, 1.0e-5f);
        var light = normalDotLight /
            MathF.Max(normalDotLight * (1.0f - k) + k, 1.0e-5f);
        return view * light;
    }

    private static Vector3 LatLongTexelDirection(
        uint x,
        uint y,
        uint width,
        uint height)
    {
        var u = (x + 0.5f) / width;
        var v = (y + 0.5f) / height;
        var longitude = (u - 0.5f) * TwoPi;
        var polar = v * MathF.PI;
        var sinPolar = MathF.Sin(polar);
        return new Vector3(
            sinPolar * MathF.Sin(longitude),
            MathF.Cos(polar),
            sinPolar * MathF.Cos(longitude));
    }

    private static Vector3 SampleLatLong(
        LinearEnvironmentSource source,
        Vector3 direction)
    {
        var longitude = MathF.Atan2(direction.X, direction.Z);
        var u = 0.5f + longitude / TwoPi;
        u -= MathF.Floor(u);
        var v = MathF.Acos(Math.Clamp(direction.Y, -1.0f, 1.0f)) / MathF.PI;

        var sampleX = u * source.Width - 0.5f;
        var sampleY = v * source.Height - 0.5f;
        var x0 = (int)MathF.Floor(sampleX);
        var y0 = (int)MathF.Floor(sampleY);
        var tx = sampleX - MathF.Floor(sampleX);
        var ty = sampleY - MathF.Floor(sampleY);
        var x1 = x0 + 1;
        var y1 = y0 + 1;
        x0 = Wrap(x0, checked((int)source.Width));
        x1 = Wrap(x1, checked((int)source.Width));
        y0 = Math.Clamp(y0, 0, checked((int)source.Height) - 1);
        y1 = Math.Clamp(y1, 0, checked((int)source.Height) - 1);

        var top = Vector3.Lerp(ReadPixel(source, x0, y0), ReadPixel(source, x1, y0), tx);
        var bottom = Vector3.Lerp(ReadPixel(source, x0, y1), ReadPixel(source, x1, y1), tx);
        return Vector3.Max(Vector3.Lerp(top, bottom, ty), Vector3.Zero);
    }

    private static Vector3 ReadPixel(LinearEnvironmentSource source, int x, int y)
    {
        var index = checked((y * (int)source.Width + x) * 4);
        return new Vector3(
            source.RgbaPixels[index + 0],
            source.RgbaPixels[index + 1],
            source.RgbaPixels[index + 2]);
    }

    private static void BuildTangentBasis(
        Vector3 normal,
        out Vector3 tangent,
        out Vector3 bitangent)
    {
        var up = MathF.Abs(normal.Y) < 0.999f ? Vector3.UnitY : Vector3.UnitX;
        tangent = Vector3.Normalize(Vector3.Cross(up, normal));
        bitangent = Vector3.Cross(normal, tangent);
    }

    private static Vector2 Hammersley(uint index, uint sampleCount)
    {
        return new Vector2(index / (float)sampleCount, RadicalInverseVdc(index));
    }

    private static float RadicalInverseVdc(uint bits)
    {
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
        return bits * 2.3283064365386963e-10f;
    }

    private static int Wrap(int value, int length)
    {
        var wrapped = value % length;
        return wrapped < 0 ? wrapped + length : wrapped;
    }

    private static int GetFullMipCount(uint width, uint height)
    {
        var largestDimension = Math.Max(width, height);
        var mipCount = 1;
        while (largestDimension > 1)
        {
            largestDimension >>= 1;
            mipCount++;
        }

        return mipCount;
    }

    private static void WritePixel(
        float[] output,
        uint x,
        uint y,
        uint width,
        Vector3 color)
    {
        WritePixel(output, checked((int)(y * width + x)), color);
    }

    private static void WritePixel(float[] output, int pixelIndex, Vector3 color)
    {
        var valueIndex = checked(pixelIndex * 4);
        output[valueIndex + 0] = color.X;
        output[valueIndex + 1] = color.Y;
        output[valueIndex + 2] = color.Z;
        output[valueIndex + 3] = 1.0f;
    }

    private static byte[] EncodeRgba16Float(float[] values)
    {
        var bytes = new byte[checked(values.Length * sizeof(ushort))];
        for (int i = 0; i < values.Length; i++)
        {
            var value = float.IsFinite(values[i])
                ? Math.Clamp(values[i], 0.0f, (float)Half.MaxValue)
                : 0.0f;
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(i * sizeof(ushort), sizeof(ushort)),
                BitConverter.HalfToUInt16Bits((Half)value));
        }

        return bytes;
    }

    private static CookedEnvironmentLightingHeader ReadHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || !bytes.Slice(0, s_Magic.Length).SequenceEqual(s_Magic))
        {
            throw new InvalidOperationException(
                "[EnvironmentLightingAssetCooker] Cooked IBL header magic is invalid.");
        }

        var version = ReadInt32(bytes, 8);
        if (version != CookedFormatVersion)
        {
            throw new InvalidOperationException(
                $"[EnvironmentLightingAssetCooker] Cooked IBL version '{version}' is not supported.");
        }

        var header = new CookedEnvironmentLightingHeader(
            checked((uint)ReadInt32(bytes, 12)),
            checked((uint)ReadInt32(bytes, 16)),
            ReadInt32(bytes, 20),
            ReadInt32(bytes, 24),
            checked((uint)ReadInt32(bytes, 28)),
            checked((uint)ReadInt32(bytes, 32)),
            ReadInt32(bytes, 36),
            ReadInt32(bytes, 40),
            checked((uint)ReadInt32(bytes, 44)),
            checked((uint)ReadInt32(bytes, 48)),
            ReadInt32(bytes, 52),
            ReadInt32(bytes, 56),
            ReadInt32(bytes, 60),
            new Guid(bytes.Slice(64, 16)));

        var expectedSpecularMipCount = GetFullMipCount(SpecularWidth, SpecularHeight);
        if (header.IrradianceWidth != IrradianceWidth ||
            header.IrradianceHeight != IrradianceHeight ||
            header.IrradianceMipCount != 1 ||
            header.SpecularWidth != SpecularWidth ||
            header.SpecularHeight != SpecularHeight ||
            header.SpecularMipCount != expectedSpecularMipCount ||
            header.BrdfWidth != BrdfWidth ||
            header.BrdfHeight != BrdfHeight ||
            header.BrdfMipCount != 1 ||
            header.EnvironmentGuid == Guid.Empty)
        {
            throw new InvalidOperationException(
                "[EnvironmentLightingAssetCooker] Cooked IBL header contains invalid dimensions or identity.");
        }

        var expectedIrradianceSize = GetPackedMipDataSize(IrradianceWidth, IrradianceHeight, 1);
        var expectedSpecularSize = GetPackedMipDataSize(
            SpecularWidth,
            SpecularHeight,
            expectedSpecularMipCount);
        var expectedBrdfSize = GetPackedMipDataSize(BrdfWidth, BrdfHeight, 1);
        var expectedPayloadSize = checked(expectedIrradianceSize + expectedSpecularSize + expectedBrdfSize);
        if (header.IrradianceDataSize != expectedIrradianceSize ||
            header.SpecularDataSize != expectedSpecularSize ||
            header.BrdfDataSize != expectedBrdfSize ||
            header.PayloadDataSize != expectedPayloadSize ||
            HeaderSize + expectedPayloadSize > bytes.Length)
        {
            throw new InvalidOperationException(
                "[EnvironmentLightingAssetCooker] Cooked IBL payload is truncated or has an invalid size.");
        }

        return header;
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, sizeof(int)));
    }

    private readonly record struct LinearEnvironmentSource(
        uint Width,
        uint Height,
        float[] RgbaPixels);

    private readonly record struct CookedEnvironmentLightingHeader(
        uint IrradianceWidth,
        uint IrradianceHeight,
        int IrradianceMipCount,
        int IrradianceDataSize,
        uint SpecularWidth,
        uint SpecularHeight,
        int SpecularMipCount,
        int SpecularDataSize,
        uint BrdfWidth,
        uint BrdfHeight,
        int BrdfMipCount,
        int BrdfDataSize,
        int PayloadDataSize,
        Guid EnvironmentGuid);
}
