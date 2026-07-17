using System.Buffers.Binary;
using System.Text;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.Serialization;
using StbImageSharp;

namespace ArisenEngine.Rendering.Resources;

public enum EnvironmentTextureLayout
{
    LatLong
}

public enum EnvironmentTextureCookedFormat
{
    R16G16B16A16SFloat
}

public readonly record struct EnvironmentTextureVariantKey(
    EnvironmentTextureLayout Layout,
    EnvironmentTextureCookedFormat Format,
    bool GenerateMipMaps)
{
    public static EnvironmentTextureVariantKey DefaultLatLong { get; } = new(
        EnvironmentTextureLayout.LatLong,
        EnvironmentTextureCookedFormat.R16G16B16A16SFloat,
        GenerateMipMaps: false);

    public string GetCookedVariant()
    {
        var mipSuffix = GenerateMipMaps ? "mips" : "nomips";
        return $"{Layout.ToString().ToLowerInvariant()}.{Format.ToString().ToLowerInvariant()}.{mipSuffix}";
    }
}

public sealed record EnvironmentTextureAsset(
    Guid Guid,
    string Name,
    AssetRef<Texture2DSourceAsset> SourceTexture,
    EnvironmentTextureVariantKey Variant,
    Texture2DColorSpace SourceColorSpace,
    float RotationDegrees,
    float Intensity);

public readonly record struct CookedEnvironmentTexture(
    EnvironmentTextureAsset Asset,
    string Variant,
    uint Width,
    uint Height,
    int MipCount,
    EnvironmentTextureLayout Layout,
    EnvironmentTextureCookedFormat Format,
    float RotationDegrees,
    float Intensity,
    int PixelDataOffset,
    int PixelDataSize,
    CookedAssetHandle Handle)
{
    public bool IsValid => Handle.IsValid;
}

public static class EnvironmentTextureAssetLoader
{
    public const string EnvironmentTextureAssetType = "EnvironmentTexture";
    public const string Texture2DAssetType = "Texture2D";

    public static EnvironmentTextureAsset LoadSource(
        IAssetDatabase assetDatabase,
        Guid environmentTextureGuid)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (!assetDatabase.TryGetAsset(environmentTextureGuid, out var sourceAsset))
        {
            throw new InvalidOperationException(
                $"[EnvironmentTextureAssetLoader] Environment texture asset '{environmentTextureGuid}' was not found.");
        }

        return LoadSource(assetDatabase, sourceAsset);
    }

    public static EnvironmentTextureAsset LoadSource(
        IAssetDatabase assetDatabase,
        AssetRecord sourceAsset)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (sourceAsset == null)
        {
            throw new ArgumentNullException(nameof(sourceAsset));
        }

        if (!string.Equals(
                sourceAsset.AssetType,
                EnvironmentTextureAssetType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[EnvironmentTextureAssetLoader] Asset '{sourceAsset.Guid}' has asset type '{sourceAsset.AssetType}', expected '{EnvironmentTextureAssetType}'.");
        }

        var source = SerializationUtil.Deserialize<SerializedEnvironmentTextureSource>(
            sourceAsset.SourcePath,
            serializeIfNotExist: false);
        source.Validate(sourceAsset.SourcePath);

        var sourceTextureRef = new AssetRef<Texture2DSourceAsset>(
            source.SourceTexture.Guid,
            Texture2DAssetType,
            source.SourceTexture.PackageId ?? string.Empty);
        if (!assetDatabase.TryGetAsset(sourceTextureRef, out var sourceTexture))
        {
            throw new InvalidOperationException(
                $"[EnvironmentTextureAssetLoader] Environment texture '{sourceAsset.SourcePath}' references missing Texture2D asset '{source.SourceTexture.Guid:D}'.");
        }

        if (!string.Equals(sourceTexture.AssetType, Texture2DAssetType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[EnvironmentTextureAssetLoader] Environment texture '{sourceAsset.SourcePath}' source asset '{source.SourceTexture.Guid:D}' has type '{sourceTexture.AssetType}', expected '{Texture2DAssetType}'.");
        }

        return new EnvironmentTextureAsset(
            sourceAsset.Guid,
            string.IsNullOrWhiteSpace(source.Name)
                ? Path.GetFileNameWithoutExtension(sourceAsset.SourcePath)
                : source.Name.Trim(),
            sourceTextureRef,
            new EnvironmentTextureVariantKey(
                ParseEnum<EnvironmentTextureLayout>(source.Layout, nameof(source.Layout), sourceAsset.SourcePath),
                ParseEnum<EnvironmentTextureCookedFormat>(source.RuntimeFormat, nameof(source.RuntimeFormat), sourceAsset.SourcePath),
                GenerateMipMaps: false),
            ParseEnum<Texture2DColorSpace>(source.SourceColorSpace, nameof(source.SourceColorSpace), sourceAsset.SourcePath),
            source.RotationDegrees,
            source.Intensity);
    }

    private static TEnum ParseEnum<TEnum>(string value, string fieldName, string sourcePath)
        where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"[EnvironmentTextureAssetLoader] Environment texture '{sourcePath}' has unsupported {fieldName} '{value}'.");
    }

    private sealed class SerializedEnvironmentTextureSource
    {
        public int Version { get; set; } = 1;
        public string Name { get; set; } = string.Empty;
        public SerializedAssetReference SourceTexture { get; set; } = new();
        public string Layout { get; set; } = nameof(EnvironmentTextureLayout.LatLong);
        public string SourceColorSpace { get; set; } = nameof(Texture2DColorSpace.SRgb);
        public string RuntimeFormat { get; set; } = nameof(EnvironmentTextureCookedFormat.R16G16B16A16SFloat);
        public float RotationDegrees { get; set; }
        public float Intensity { get; set; } = 1.0f;

        public void Validate(string sourcePath)
        {
            if (Version != 1)
            {
                throw new InvalidOperationException(
                    $"[EnvironmentTextureAssetLoader] Environment texture '{sourcePath}' version '{Version}' is not supported.");
            }

            if (SourceTexture == null || SourceTexture.Guid == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"[EnvironmentTextureAssetLoader] Environment texture '{sourcePath}' is missing SourceTexture.Guid.");
            }

            if (!float.IsFinite(RotationDegrees))
            {
                throw new InvalidOperationException(
                    $"[EnvironmentTextureAssetLoader] Environment texture '{sourcePath}' RotationDegrees must be finite.");
            }

            if (!float.IsFinite(Intensity) || Intensity <= 0.0f)
            {
                throw new InvalidOperationException(
                    $"[EnvironmentTextureAssetLoader] Environment texture '{sourcePath}' Intensity must be finite and greater than zero.");
            }
        }
    }

    private sealed class SerializedAssetReference
    {
        public Guid Guid { get; set; }
        public string PackageId { get; set; } = string.Empty;
    }
}

public static class EnvironmentTextureAssetCooker
{
    private const int HeaderSize = 64;
    private const int BytesPerPixel = 8;
    private static readonly byte[] s_Magic = Encoding.ASCII.GetBytes("ARIENVTX");

    public static CookedEnvironmentTexture LoadOrCook(
        IAssetDatabase assetDatabase,
        Guid environmentTextureGuid)
    {
        var asset = EnvironmentTextureAssetLoader.LoadSource(assetDatabase, environmentTextureGuid);
        return LoadOrCook(assetDatabase, asset);
    }

    public static CookedEnvironmentTexture LoadOrCook(
        IAssetDatabase assetDatabase,
        EnvironmentTextureAsset asset)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (asset == null)
        {
            throw new ArgumentNullException(nameof(asset));
        }

        if (!assetDatabase.TryGetAsset(asset.Guid, out var sourceAsset))
        {
            throw new InvalidOperationException(
                $"[EnvironmentTextureAssetCooker] Environment texture asset '{asset.Guid}' was not found.");
        }

        var variant = asset.Variant.GetCookedVariant();
        var outputPath = assetDatabase.GetCookedArtifactPath(asset.Guid, variant, ".environment");
        var dependencyWriteTimeUtc = AssetDependencyTracker.GetEnvironmentTextureDependencyWriteTimeUtc(
            assetDatabase,
            asset);

        if (!assetDatabase.TryGetCookedArtifact(asset.Guid, variant, out _) ||
            !File.Exists(outputPath) ||
            File.GetLastWriteTimeUtc(outputPath) < dependencyWriteTimeUtc)
        {
            CookTexture(assetDatabase, asset, variant, outputPath);
        }

        var outputInfo = new FileInfo(outputPath);
        if (!outputInfo.Exists || outputInfo.Length <= HeaderSize)
        {
            throw new InvalidOperationException(
                $"[EnvironmentTextureAssetCooker] Environment texture asset '{asset.Guid}' produced no cooked payload.");
        }

        assetDatabase.RegisterCookedArtifact(new CookedAssetRecord(
            asset.Guid,
            sourceAsset.AssetType,
            variant,
            outputInfo.FullName,
            outputInfo.Length,
            outputInfo.LastWriteTimeUtc));

        if (!assetDatabase.TryLoadCookedAsset(
                asset.Guid,
                variant,
                EnvironmentTextureAssetLoader.EnvironmentTextureAssetType,
                out var handle))
        {
            throw new InvalidOperationException(
                $"[EnvironmentTextureAssetCooker] Failed to load cooked environment texture asset '{asset.Guid}'.");
        }

        try
        {
            var bytes = assetDatabase.GetCookedAssetBytes(handle);
            var header = ReadHeader(bytes.Span);
            if (header.SourceTextureGuid != asset.SourceTexture.Guid)
            {
                throw new InvalidOperationException(
                    $"[EnvironmentTextureAssetCooker] Cooked environment texture '{asset.Guid}' references stale source texture '{header.SourceTextureGuid}', expected '{asset.SourceTexture.Guid}'.");
            }

            return new CookedEnvironmentTexture(
                asset,
                variant,
                header.Width,
                header.Height,
                header.MipCount,
                header.Layout,
                header.Format,
                header.RotationDegrees,
                header.Intensity,
                HeaderSize,
                header.PixelDataSize,
                handle);
        }
        catch
        {
            assetDatabase.Release(handle);
            throw;
        }
    }

    public static ReadOnlySpan<byte> GetPixelData(ReadOnlyMemory<byte> bytes)
    {
        var header = ReadHeader(bytes.Span);
        return bytes.Span.Slice(HeaderSize, header.PixelDataSize);
    }

    private static void CookTexture(
        IAssetDatabase assetDatabase,
        EnvironmentTextureAsset asset,
        string variant,
        string outputPath)
    {
        if (asset.Variant.Layout != EnvironmentTextureLayout.LatLong)
        {
            throw new NotSupportedException(
                $"Environment texture layout '{asset.Variant.Layout}' is not supported by the first cooker.");
        }

        if (asset.Variant.Format != EnvironmentTextureCookedFormat.R16G16B16A16SFloat ||
            asset.Variant.GenerateMipMaps)
        {
            throw new NotSupportedException(
                $"Environment texture variant '{variant}' is not supported by the first cooker.");
        }

        if (!assetDatabase.TryGetAsset(asset.SourceTexture, out var sourceTexture))
        {
            throw new InvalidOperationException(
                $"[EnvironmentTextureAssetCooker] Source Texture2D asset '{asset.SourceTexture.Guid}' was not found.");
        }

        var source = DecodeLinearSource(sourceTexture.SourcePath, asset.SourceColorSpace);
        if (source.Width != source.Height * 2u)
        {
            throw new InvalidOperationException(
                $"[EnvironmentTextureAssetCooker] Lat-long source '{sourceTexture.SourcePath}' must use a 2:1 aspect ratio, got {source.Width}x{source.Height}.");
        }

        var pixelBytes = EncodeRgba16Float(source);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var stream = File.Create(outputPath);
        stream.Write(s_Magic);
        WriteInt32(stream, 1);
        WriteInt32(stream, checked((int)source.Width));
        WriteInt32(stream, checked((int)source.Height));
        WriteInt32(stream, 1);
        WriteInt32(stream, (int)asset.Variant.Layout);
        WriteInt32(stream, (int)asset.Variant.Format);
        WriteInt32(stream, pixelBytes.Length);
        WriteInt32(stream, 0);
        WriteSingle(stream, asset.RotationDegrees);
        WriteSingle(stream, asset.Intensity);
        Span<byte> sourceGuidBytes = stackalloc byte[16];
        asset.SourceTexture.Guid.TryWriteBytes(sourceGuidBytes);
        stream.Write(sourceGuidBytes);
        stream.Write(pixelBytes);

        Logger.Log(
            $"[EnvironmentTextureAssetCooker] Cooked lat-long environment | Guid: {asset.Guid} | Source: {asset.SourceTexture.Guid} | Size: {source.Width}x{source.Height} | Variant: {variant} | Output: {outputPath}");
    }

    private static LinearSourceTexture DecodeLinearSource(
        string sourcePath,
        Texture2DColorSpace sourceColorSpace)
    {
        if (string.Equals(Path.GetExtension(sourcePath), ".hdr", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(sourcePath);
            var image = ImageResultFloat.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
            if (image.Width <= 0 || image.Height <= 0)
            {
                throw new InvalidOperationException(
                    $"[EnvironmentTextureAssetCooker] '{sourcePath}' has invalid HDR dimensions.");
            }

            var expectedLength = checked(image.Width * image.Height * 4);
            if (image.Data.Length != expectedLength)
            {
                throw new InvalidOperationException(
                    $"[EnvironmentTextureAssetCooker] '{sourcePath}' decoded to {image.Data.Length} HDR values, expected {expectedLength}.");
            }

            var pixels = image.Data;
            ConvertRgbToLinearInPlace(pixels, sourceColorSpace);
            return new LinearSourceTexture(
                checked((uint)image.Width),
                checked((uint)image.Height),
                pixels);
        }

        var sourceFormat = string.Equals(
            Path.GetExtension(sourcePath),
            ".ppm",
            StringComparison.OrdinalIgnoreCase)
            ? Texture2DSourceFormat.PpmP3
            : Texture2DSourceFormat.ImageFile;
        var decoded = Texture2DAssetCooker.DecodeSource(sourcePath, sourceFormat);
        var linearPixels = new float[decoded.RgbaPixels.Length];
        for (int i = 0; i < decoded.RgbaPixels.Length; i += 4)
        {
            linearPixels[i + 0] = DecodeColorChannel(decoded.RgbaPixels[i + 0] / 255.0f, sourceColorSpace);
            linearPixels[i + 1] = DecodeColorChannel(decoded.RgbaPixels[i + 1] / 255.0f, sourceColorSpace);
            linearPixels[i + 2] = DecodeColorChannel(decoded.RgbaPixels[i + 2] / 255.0f, sourceColorSpace);
            linearPixels[i + 3] = decoded.RgbaPixels[i + 3] / 255.0f;
        }

        return new LinearSourceTexture(decoded.Width, decoded.Height, linearPixels);
    }

    private static void ConvertRgbToLinearInPlace(
        float[] pixels,
        Texture2DColorSpace sourceColorSpace)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = DecodeColorChannel(pixels[i + 0], sourceColorSpace);
            pixels[i + 1] = DecodeColorChannel(pixels[i + 1], sourceColorSpace);
            pixels[i + 2] = DecodeColorChannel(pixels[i + 2], sourceColorSpace);
            pixels[i + 3] = Math.Clamp(pixels[i + 3], 0.0f, 1.0f);
        }
    }

    private static float DecodeColorChannel(float value, Texture2DColorSpace sourceColorSpace)
    {
        value = MathF.Max(0.0f, value);
        if (sourceColorSpace == Texture2DColorSpace.Linear)
        {
            return value;
        }

        return value <= 0.04045f
            ? value / 12.92f
            : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

    private static byte[] EncodeRgba16Float(LinearSourceTexture source)
    {
        var pixelBytes = new byte[checked(source.RgbaPixels.Length * 2)];
        for (int i = 0; i < source.RgbaPixels.Length; i++)
        {
            var value = Math.Clamp(source.RgbaPixels[i], 0.0f, (float)Half.MaxValue);
            var bits = BitConverter.HalfToUInt16Bits((Half)value);
            BinaryPrimitives.WriteUInt16LittleEndian(pixelBytes.AsSpan(i * 2, 2), bits);
        }

        return pixelBytes;
    }

    private static CookedEnvironmentTextureHeader ReadHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || !bytes.Slice(0, s_Magic.Length).SequenceEqual(s_Magic))
        {
            throw new InvalidOperationException(
                "[EnvironmentTextureAssetCooker] Cooked environment texture header magic is invalid.");
        }

        var version = ReadInt32(bytes, 8);
        if (version != 1)
        {
            throw new InvalidOperationException(
                $"[EnvironmentTextureAssetCooker] Cooked environment texture version '{version}' is not supported.");
        }

        var width = ReadInt32(bytes, 12);
        var height = ReadInt32(bytes, 16);
        var mipCount = ReadInt32(bytes, 20);
        var layout = (EnvironmentTextureLayout)ReadInt32(bytes, 24);
        var format = (EnvironmentTextureCookedFormat)ReadInt32(bytes, 28);
        var pixelDataSize = ReadInt32(bytes, 32);
        var rotationDegrees = ReadSingle(bytes, 40);
        var intensity = ReadSingle(bytes, 44);
        var sourceTextureGuid = new Guid(bytes.Slice(48, 16));

        if (width <= 0 || height <= 0 || width != height * 2 || mipCount != 1)
        {
            throw new InvalidOperationException(
                "[EnvironmentTextureAssetCooker] Cooked lat-long header contains invalid dimensions or mip count.");
        }

        if (layout != EnvironmentTextureLayout.LatLong ||
            format != EnvironmentTextureCookedFormat.R16G16B16A16SFloat)
        {
            throw new InvalidOperationException(
                "[EnvironmentTextureAssetCooker] Cooked environment texture header contains an unsupported layout or format.");
        }

        var expectedPixelDataSize = checked(width * height * BytesPerPixel);
        if (pixelDataSize != expectedPixelDataSize || HeaderSize + pixelDataSize > bytes.Length)
        {
            throw new InvalidOperationException(
                "[EnvironmentTextureAssetCooker] Cooked environment texture payload is truncated or has an invalid size.");
        }

        if (!float.IsFinite(rotationDegrees) || !float.IsFinite(intensity) || intensity <= 0.0f ||
            sourceTextureGuid == Guid.Empty)
        {
            throw new InvalidOperationException(
                "[EnvironmentTextureAssetCooker] Cooked environment texture metadata is invalid.");
        }

        return new CookedEnvironmentTextureHeader(
            checked((uint)width),
            checked((uint)height),
            mipCount,
            layout,
            format,
            rotationDegrees,
            intensity,
            sourceTextureGuid,
            pixelDataSize);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteSingle(Stream stream, float value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4));
    }

    private static float ReadSingle(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset, 4));
    }

    private readonly record struct LinearSourceTexture(
        uint Width,
        uint Height,
        float[] RgbaPixels);

    private readonly record struct CookedEnvironmentTextureHeader(
        uint Width,
        uint Height,
        int MipCount,
        EnvironmentTextureLayout Layout,
        EnvironmentTextureCookedFormat Format,
        float RotationDegrees,
        float Intensity,
        Guid SourceTextureGuid,
        int PixelDataSize);
}
