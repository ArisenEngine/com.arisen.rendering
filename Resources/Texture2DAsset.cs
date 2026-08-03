using System.Buffers.Binary;
using System.Text;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using StbImageSharp;

namespace ArisenEngine.Rendering.Resources;

public enum Texture2DSourceFormat
{
    PpmP3,
    ImageFile
}

public enum Texture2DCookedFormat
{
    R8G8B8A8UNorm
}

public enum Texture2DColorSpace
{
    Linear,
    SRgb
}

public enum Texture2DMipFilter
{
    Color,
    NormalMap
}

public readonly record struct Texture2DVariantKey(
    Texture2DCookedFormat Format,
    Texture2DColorSpace ColorSpace,
    bool GenerateMipMaps,
    Texture2DMipFilter MipFilter = Texture2DMipFilter.Color)
{
    public static Texture2DVariantKey DefaultSRgb { get; } = new(
        Texture2DCookedFormat.R8G8B8A8UNorm,
        Texture2DColorSpace.SRgb,
        GenerateMipMaps: false);

    public static Texture2DVariantKey MipmappedSRgb { get; } = new(
        Texture2DCookedFormat.R8G8B8A8UNorm,
        Texture2DColorSpace.SRgb,
        GenerateMipMaps: true);

    public static Texture2DVariantKey MipmappedLinear { get; } = new(
        Texture2DCookedFormat.R8G8B8A8UNorm,
        Texture2DColorSpace.Linear,
        GenerateMipMaps: true);

    public static Texture2DVariantKey MipmappedNormal { get; } = new(
        Texture2DCookedFormat.R8G8B8A8UNorm,
        Texture2DColorSpace.Linear,
        GenerateMipMaps: true,
        Texture2DMipFilter.NormalMap);

    public string GetCookedVariant()
    {
        var mipSuffix = GenerateMipMaps ? "mips" : "nomips";
        string filterSuffix = MipFilter == Texture2DMipFilter.NormalMap
            ? ".normalmap"
            : string.Empty;
        return $"{Format.ToString().ToLowerInvariant()}.{ColorSpace.ToString().ToLowerInvariant()}.{mipSuffix}{filterSuffix}";
    }
}

public sealed record Texture2DAsset(
    Guid Guid,
    string Name,
    Texture2DVariantKey Variant,
    Texture2DSourceFormat SourceFormat = Texture2DSourceFormat.PpmP3);

public readonly record struct DecodedTexture2DSource(
    uint Width,
    uint Height,
    byte[] RgbaPixels);

public readonly record struct CookedTexture2D(
    Texture2DAsset Asset,
    string Variant,
    uint Width,
    uint Height,
    Texture2DCookedFormat Format,
    Texture2DColorSpace ColorSpace,
    Texture2DMipFilter MipFilter,
    int MipCount,
    int PixelDataOffset,
    int PixelDataSize,
    CookedAssetHandle Handle)
{
    public bool IsValid => Handle.IsValid;
}

public static class Texture2DAssetCooker
{
    public const int CookedFormatVersion = 2;

    private const string TextureAssetType = "Texture2D";
    private const int HeaderSize = 40;
    private const int LinearToSrgbTableResolution = 4096;
    private static readonly byte[] s_Magic = Encoding.ASCII.GetBytes("ARISTX2D");
    private static readonly float[] s_SrgbToLinear = CreateSrgbToLinearTable();
    private static readonly byte[] s_LinearToSrgb = CreateLinearToSrgbTable();

    public static CookedTexture2D LoadOrCook(
        IAssetDatabase assetDatabase,
        Texture2DAsset texture)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (texture == null)
        {
            throw new ArgumentNullException(nameof(texture));
        }

        var variant = texture.Variant.GetCookedVariant();
        if (!assetDatabase.CanReadSourceAssets)
        {
            return LoadCooked(assetDatabase, texture, variant);
        }

        if (!assetDatabase.TryGetAsset(texture.Guid, out var sourceAsset))
        {
            throw new InvalidOperationException($"[Texture2DAssetCooker] Texture asset '{texture.Guid}' was not found.");
        }

        if (!string.Equals(sourceAsset.AssetType, TextureAssetType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[Texture2DAssetCooker] Texture asset '{texture.Guid}' has asset type '{sourceAsset.AssetType}', expected '{TextureAssetType}'.");
        }

        var sourceWriteTimeUtc = File.GetLastWriteTimeUtc(sourceAsset.SourcePath);

        if (!assetDatabase.TryGetCookedArtifact(texture.Guid, variant, out CookedAssetRecord current) ||
            !File.Exists(current.Path) ||
            File.GetLastWriteTimeUtc(current.Path) < sourceWriteTimeUtc ||
            !HasCompatibleCookedArtifact(current.Path, texture.Variant))
        {
            using CookedArtifactWrite write = assetDatabase.BeginCookedArtifactWrite(
                texture.Guid,
                variant,
                ".texture2d");
            CookTexture(sourceAsset, texture, variant, write.OutputPath);

            var outputInfo = new FileInfo(write.OutputPath);
            if (!outputInfo.Exists || outputInfo.Length <= HeaderSize)
            {
                throw new InvalidOperationException(
                    $"[Texture2DAssetCooker] Texture asset '{texture.Guid}' produced no cooked payload.");
            }

            write.Commit(sourceAsset.AssetType);
        }

        return LoadCooked(assetDatabase, texture, variant);
    }

    private static CookedTexture2D LoadCooked(
        IAssetDatabase assetDatabase,
        Texture2DAsset texture,
        string variant)
    {
        if (!assetDatabase.TryLoadCookedAsset(texture.Guid, variant, TextureAssetType, out var handle))
        {
            throw new InvalidOperationException(
                $"[Texture2DAssetCooker] Cooked texture asset '{texture.Guid}' variant '{variant}' is unavailable.");
        }

        try
        {
            var bytes = assetDatabase.GetCookedAssetBytes(handle);
            var header = ReadHeader(bytes.Span);
            return new CookedTexture2D(
                texture,
                variant,
                header.Width,
                header.Height,
                header.Format,
                header.ColorSpace,
                header.MipFilter,
                header.MipCount,
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

    public static DecodedTexture2DSource DecodeSource(
        string sourcePath,
        Texture2DSourceFormat sourceFormat)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("Texture source path cannot be empty.", nameof(sourcePath));
        }

        return sourceFormat switch
        {
            Texture2DSourceFormat.PpmP3 => ReadPpmP3(sourcePath),
            Texture2DSourceFormat.ImageFile => ReadImageFile(sourcePath),
            _ => throw new NotSupportedException($"Texture source format '{sourceFormat}' is not implemented yet.")
        };
    }

    private static void CookTexture(
        AssetRecord sourceAsset,
        Texture2DAsset texture,
        string variant,
        string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var source = DecodeSource(sourceAsset.SourcePath, texture.SourceFormat);
        ValidateVariant(texture.Variant);
        CookedMipChain mipChain = texture.Variant.GenerateMipMaps
            ? GenerateMipChain(
                source,
                texture.Variant.ColorSpace,
                texture.Variant.MipFilter)
            : new CookedMipChain(1, source.RgbaPixels);

        using var stream = File.Create(outputPath);
        stream.Write(s_Magic);
        WriteInt32(stream, CookedFormatVersion);
        WriteInt32(stream, checked((int)source.Width));
        WriteInt32(stream, checked((int)source.Height));
        WriteInt32(stream, mipChain.MipCount);
        WriteInt32(stream, (int)texture.Variant.Format);
        WriteInt32(stream, (int)texture.Variant.ColorSpace);
        WriteInt32(stream, mipChain.Pixels.Length);
        WriteInt32(stream, (int)texture.Variant.MipFilter);
        stream.Write(mipChain.Pixels);

        Logger.Log(
            $"[Texture2DAssetCooker] Cooked texture asset {texture.Guid} | Size: {source.Width}x{source.Height} | Mips: {mipChain.MipCount} | Variant: {variant} | Output: {outputPath}");
    }

    private static CookedMipChain GenerateMipChain(
        DecodedTexture2DSource source,
        Texture2DColorSpace colorSpace,
        Texture2DMipFilter mipFilter)
    {
        int mipCount = GetMipCount(source.Width, source.Height);
        int packedByteCount = GetPackedMipByteCount(source.Width, source.Height, mipCount);
        var packedPixels = new byte[packedByteCount];
        source.RgbaPixels.CopyTo(packedPixels, 0);

        uint sourceWidth = source.Width;
        uint sourceHeight = source.Height;
        int sourceOffset = 0;
        int destinationOffset = source.RgbaPixels.Length;
        while (sourceWidth > 1 || sourceHeight > 1)
        {
            uint destinationWidth = Math.Max(1u, sourceWidth >> 1);
            uint destinationHeight = Math.Max(1u, sourceHeight >> 1);
            int sourceByteCount = checked((int)(sourceWidth * sourceHeight * 4u));
            int destinationByteCount = checked((int)(destinationWidth * destinationHeight * 4u));
            DownsampleMip(
                packedPixels.AsSpan(sourceOffset, sourceByteCount),
                sourceWidth,
                sourceHeight,
                packedPixels.AsSpan(destinationOffset, destinationByteCount),
                destinationWidth,
                destinationHeight,
                colorSpace,
                mipFilter);

            sourceOffset = destinationOffset;
            destinationOffset = checked(destinationOffset + destinationByteCount);
            sourceWidth = destinationWidth;
            sourceHeight = destinationHeight;
        }

        return new CookedMipChain(mipCount, packedPixels);
    }

    private static bool HasCompatibleCookedArtifact(
        string path,
        Texture2DVariantKey variant)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < HeaderSize)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[HeaderSize];
            stream.ReadExactly(header);
            if (!header.Slice(0, s_Magic.Length).SequenceEqual(s_Magic) ||
                ReadInt32(header, 8) != CookedFormatVersion)
            {
                return false;
            }

            int width = ReadInt32(header, 12);
            int height = ReadInt32(header, 16);
            int mipCount = ReadInt32(header, 20);
            int pixelDataSize = ReadInt32(header, 32);
            if (width <= 0 ||
                height <= 0 ||
                (Texture2DCookedFormat)ReadInt32(header, 24) != variant.Format ||
                (Texture2DColorSpace)ReadInt32(header, 28) != variant.ColorSpace)
            {
                return false;
            }

            if ((Texture2DMipFilter)ReadInt32(header, 36) != variant.MipFilter)
            {
                return false;
            }

            int expectedMipCount = variant.GenerateMipMaps
                ? GetMipCount(checked((uint)width), checked((uint)height))
                : 1;
            int expectedPixelDataSize = GetPackedMipByteCount(
                checked((uint)width),
                checked((uint)height),
                expectedMipCount);
            return mipCount == expectedMipCount &&
                   pixelDataSize == expectedPixelDataSize &&
                   stream.Length == HeaderSize + (long)pixelDataSize;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            OverflowException)
        {
            return false;
        }
    }

    private static void DownsampleMip(
        ReadOnlySpan<byte> source,
        uint sourceWidth,
        uint sourceHeight,
        Span<byte> destination,
        uint destinationWidth,
        uint destinationHeight,
        Texture2DColorSpace colorSpace,
        Texture2DMipFilter mipFilter)
    {
        for (uint destinationY = 0; destinationY < destinationHeight; destinationY++)
        {
            uint sourceYBegin = destinationY * sourceHeight / destinationHeight;
            uint sourceYEnd = (destinationY + 1u) * sourceHeight / destinationHeight;
            for (uint destinationX = 0; destinationX < destinationWidth; destinationX++)
            {
                uint sourceXBegin = destinationX * sourceWidth / destinationWidth;
                uint sourceXEnd = (destinationX + 1u) * sourceWidth / destinationWidth;
                int sampleCount = checked((int)((sourceXEnd - sourceXBegin) * (sourceYEnd - sourceYBegin)));
                int destinationIndex = checked((int)((destinationY * destinationWidth + destinationX) * 4u));

                if (mipFilter == Texture2DMipFilter.NormalMap)
                {
                    float normalX = 0.0f;
                    float normalY = 0.0f;
                    float normalZ = 0.0f;
                    int alpha = 0;
                    for (uint sourceY = sourceYBegin; sourceY < sourceYEnd; sourceY++)
                    {
                        for (uint sourceX = sourceXBegin; sourceX < sourceXEnd; sourceX++)
                        {
                            int sourceIndex = checked((int)((sourceY * sourceWidth + sourceX) * 4u));
                            normalX += DecodeNormalChannel(source[sourceIndex]);
                            normalY += DecodeNormalChannel(source[sourceIndex + 1]);
                            normalZ += DecodeNormalChannel(source[sourceIndex + 2]);
                            alpha += source[sourceIndex + 3];
                        }
                    }

                    float lengthSquared =
                        (normalX * normalX) +
                        (normalY * normalY) +
                        (normalZ * normalZ);
                    if (!float.IsFinite(lengthSquared) || lengthSquared <= 1.0e-12f)
                    {
                        normalX = 0.0f;
                        normalY = 0.0f;
                        normalZ = 1.0f;
                    }
                    else
                    {
                        float inverseLength = 1.0f / MathF.Sqrt(lengthSquared);
                        normalX *= inverseLength;
                        normalY *= inverseLength;
                        normalZ *= inverseLength;
                    }

                    destination[destinationIndex] = EncodeNormalChannel(normalX);
                    destination[destinationIndex + 1] = EncodeNormalChannel(normalY);
                    destination[destinationIndex + 2] = EncodeNormalChannel(normalZ);
                    destination[destinationIndex + 3] =
                        checked((byte)((alpha + sampleCount / 2) / sampleCount));
                    continue;
                }

                if (colorSpace == Texture2DColorSpace.SRgb)
                {
                    float red = 0.0f;
                    float green = 0.0f;
                    float blue = 0.0f;
                    int alpha = 0;
                    for (uint sourceY = sourceYBegin; sourceY < sourceYEnd; sourceY++)
                    {
                        for (uint sourceX = sourceXBegin; sourceX < sourceXEnd; sourceX++)
                        {
                            int sourceIndex = checked((int)((sourceY * sourceWidth + sourceX) * 4u));
                            red += s_SrgbToLinear[source[sourceIndex]];
                            green += s_SrgbToLinear[source[sourceIndex + 1]];
                            blue += s_SrgbToLinear[source[sourceIndex + 2]];
                            alpha += source[sourceIndex + 3];
                        }
                    }

                    float inverseSampleCount = 1.0f / sampleCount;
                    destination[destinationIndex] = EncodeSrgb(red * inverseSampleCount);
                    destination[destinationIndex + 1] = EncodeSrgb(green * inverseSampleCount);
                    destination[destinationIndex + 2] = EncodeSrgb(blue * inverseSampleCount);
                    destination[destinationIndex + 3] = checked((byte)((alpha + sampleCount / 2) / sampleCount));
                    continue;
                }

                for (int channel = 0; channel < 4; channel++)
                {
                    int sum = 0;
                    for (uint sourceY = sourceYBegin; sourceY < sourceYEnd; sourceY++)
                    {
                        for (uint sourceX = sourceXBegin; sourceX < sourceXEnd; sourceX++)
                        {
                            int sourceIndex = checked((int)((sourceY * sourceWidth + sourceX) * 4u));
                            sum += source[sourceIndex + channel];
                        }
                    }

                    destination[destinationIndex + channel] =
                        checked((byte)((sum + sampleCount / 2) / sampleCount));
                }
            }
        }
    }

    private static int GetMipCount(uint width, uint height)
    {
        int mipCount = 1;
        while (width > 1 || height > 1)
        {
            width = Math.Max(1u, width >> 1);
            height = Math.Max(1u, height >> 1);
            mipCount++;
        }

        return mipCount;
    }

    private static int GetPackedMipByteCount(uint width, uint height, int mipCount)
    {
        ulong byteCount = 0;
        for (int mipLevel = 0; mipLevel < mipCount; mipLevel++)
        {
            byteCount = checked(byteCount + (ulong)width * height * 4u);
            width = Math.Max(1u, width >> 1);
            height = Math.Max(1u, height >> 1);
        }

        return checked((int)byteCount);
    }

    private static float[] CreateSrgbToLinearTable()
    {
        var table = new float[256];
        for (int value = 0; value < table.Length; value++)
        {
            float encoded = value / 255.0f;
            table[value] = encoded <= 0.04045f
                ? encoded / 12.92f
                : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
        }

        return table;
    }

    private static byte[] CreateLinearToSrgbTable()
    {
        var table = new byte[LinearToSrgbTableResolution + 1];
        for (int value = 0; value < table.Length; value++)
        {
            float linear = value / (float)LinearToSrgbTableResolution;
            float encoded = linear <= 0.0031308f
                ? linear * 12.92f
                : 1.055f * MathF.Pow(linear, 1.0f / 2.4f) - 0.055f;
            table[value] = checked((byte)Math.Clamp(
                (int)MathF.Round(encoded * 255.0f),
                0,
                255));
        }

        return table;
    }

    private static byte EncodeSrgb(float linear)
    {
        int index = Math.Clamp(
            (int)MathF.Round(linear * LinearToSrgbTableResolution),
            0,
            LinearToSrgbTableResolution);
        return s_LinearToSrgb[index];
    }

    private static float DecodeNormalChannel(byte encoded) =>
        (encoded * (2.0f / 255.0f)) - 1.0f;

    private static byte EncodeNormalChannel(float value) =>
        checked((byte)Math.Clamp(
            (int)MathF.Round((Math.Clamp(value, -1.0f, 1.0f) * 0.5f + 0.5f) * 255.0f),
            0,
            255));

    private static void ValidateVariant(Texture2DVariantKey variant)
    {
        if (!Enum.IsDefined(variant.Format) ||
            !Enum.IsDefined(variant.ColorSpace) ||
            !Enum.IsDefined(variant.MipFilter) ||
            (variant.MipFilter == Texture2DMipFilter.NormalMap &&
             variant.ColorSpace != Texture2DColorSpace.Linear))
        {
            throw new InvalidOperationException(
                $"[Texture2DAssetCooker] Texture variant '{variant.GetCookedVariant()}' is invalid.");
        }
    }

    private static CookedTexture2DHeader ReadHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || !bytes.Slice(0, s_Magic.Length).SequenceEqual(s_Magic))
        {
            throw new InvalidOperationException("[Texture2DAssetCooker] Cooked texture header magic is invalid.");
        }

        var version = ReadInt32(bytes, 8);
        if (version != CookedFormatVersion)
        {
            throw new InvalidOperationException($"[Texture2DAssetCooker] Cooked texture version '{version}' is not supported.");
        }

        var width = ReadInt32(bytes, 12);
        var height = ReadInt32(bytes, 16);
        var mipCount = ReadInt32(bytes, 20);
        var format = (Texture2DCookedFormat)ReadInt32(bytes, 24);
        var colorSpace = (Texture2DColorSpace)ReadInt32(bytes, 28);
        var pixelDataSize = ReadInt32(bytes, 32);
        var mipFilter = (Texture2DMipFilter)ReadInt32(bytes, 36);

        if (width <= 0 ||
            height <= 0 ||
            mipCount <= 0 ||
            pixelDataSize <= 0 ||
            !Enum.IsDefined(format) ||
            !Enum.IsDefined(colorSpace) ||
            !Enum.IsDefined(mipFilter) ||
            (mipFilter == Texture2DMipFilter.NormalMap &&
             colorSpace != Texture2DColorSpace.Linear))
        {
            throw new InvalidOperationException("[Texture2DAssetCooker] Cooked texture header contains invalid dimensions or payload size.");
        }

        if (HeaderSize + pixelDataSize > bytes.Length)
        {
            throw new InvalidOperationException("[Texture2DAssetCooker] Cooked texture payload is truncated.");
        }

        return new CookedTexture2DHeader(
            checked((uint)width),
            checked((uint)height),
            mipCount,
            format,
            colorSpace,
            mipFilter,
            pixelDataSize);
    }

    private static DecodedTexture2DSource ReadPpmP3(string sourcePath)
    {
        var tokenizer = new PpmTokenizer(File.ReadAllText(sourcePath));
        var magic = tokenizer.NextToken();
        if (!string.Equals(magic, "P3", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"[Texture2DAssetCooker] '{sourcePath}' is not an ASCII PPM P3 texture.");
        }

        var width = tokenizer.NextUInt32();
        var height = tokenizer.NextUInt32();
        var maxValue = tokenizer.NextUInt32();
        if (width == 0 || height == 0 || maxValue == 0 || maxValue > 255)
        {
            throw new InvalidOperationException($"[Texture2DAssetCooker] '{sourcePath}' has invalid PPM dimensions or range.");
        }

        var pixelCount = checked((int)(width * height));
        var pixels = new byte[checked(pixelCount * 4)];
        for (int i = 0; i < pixelCount; i++)
        {
            pixels[(i * 4) + 0] = ScaleToByte(tokenizer.NextUInt32(), maxValue);
            pixels[(i * 4) + 1] = ScaleToByte(tokenizer.NextUInt32(), maxValue);
            pixels[(i * 4) + 2] = ScaleToByte(tokenizer.NextUInt32(), maxValue);
            pixels[(i * 4) + 3] = 255;
        }

        return new DecodedTexture2DSource(width, height, pixels);
    }

    private static DecodedTexture2DSource ReadImageFile(string sourcePath)
    {
        using var stream = File.OpenRead(sourcePath);
        var image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        if (image.Width <= 0 || image.Height <= 0)
        {
            throw new InvalidOperationException($"[Texture2DAssetCooker] '{sourcePath}' has invalid image dimensions.");
        }

        var expectedLength = checked(image.Width * image.Height * 4);
        if (image.Data.Length != expectedLength)
        {
            throw new InvalidOperationException(
                $"[Texture2DAssetCooker] '{sourcePath}' decoded to {image.Data.Length} bytes, expected {expectedLength}.");
        }

        return new DecodedTexture2DSource(checked((uint)image.Width), checked((uint)image.Height), image.Data);
    }

    private static byte ScaleToByte(uint value, uint maxValue)
    {
        if (value > maxValue)
        {
            throw new InvalidOperationException("[Texture2DAssetCooker] PPM color value exceeds declared range.");
        }

        return maxValue == 255 ? (byte)value : checked((byte)((value * 255u) / maxValue));
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4));
    }

    private readonly record struct CookedTexture2DHeader(
        uint Width,
        uint Height,
        int MipCount,
        Texture2DCookedFormat Format,
        Texture2DColorSpace ColorSpace,
        Texture2DMipFilter MipFilter,
        int PixelDataSize);

    private readonly record struct CookedMipChain(int MipCount, byte[] Pixels);

    private ref struct PpmTokenizer
    {
        private readonly ReadOnlySpan<char> m_Source;
        private int m_Index;

        public PpmTokenizer(string source)
        {
            m_Source = source.AsSpan();
            m_Index = 0;
        }

        public uint NextUInt32()
        {
            var token = NextToken();
            if (!uint.TryParse(token, out var value))
            {
                throw new InvalidOperationException($"[Texture2DAssetCooker] Invalid PPM integer token '{token}'.");
            }

            return value;
        }

        public string NextToken()
        {
            SkipWhitespaceAndComments();
            if (m_Index >= m_Source.Length)
            {
                throw new InvalidOperationException("[Texture2DAssetCooker] Unexpected end of PPM source.");
            }

            var start = m_Index;
            while (m_Index < m_Source.Length && !char.IsWhiteSpace(m_Source[m_Index]) && m_Source[m_Index] != '#')
            {
                m_Index++;
            }

            return m_Source.Slice(start, m_Index - start).ToString();
        }

        private void SkipWhitespaceAndComments()
        {
            while (m_Index < m_Source.Length)
            {
                if (char.IsWhiteSpace(m_Source[m_Index]))
                {
                    m_Index++;
                    continue;
                }

                if (m_Source[m_Index] == '#')
                {
                    while (m_Index < m_Source.Length && m_Source[m_Index] != '\n')
                    {
                        m_Index++;
                    }
                    continue;
                }

                break;
            }
        }
    }
}
