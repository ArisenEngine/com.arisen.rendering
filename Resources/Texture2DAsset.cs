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

public readonly record struct Texture2DVariantKey(
    Texture2DCookedFormat Format,
    Texture2DColorSpace ColorSpace,
    bool GenerateMipMaps)
{
    public static Texture2DVariantKey DefaultSRgb { get; } = new(
        Texture2DCookedFormat.R8G8B8A8UNorm,
        Texture2DColorSpace.SRgb,
        GenerateMipMaps: false);

    public string GetCookedVariant()
    {
        var mipSuffix = GenerateMipMaps ? "mips" : "nomips";
        return $"{Format.ToString().ToLowerInvariant()}.{ColorSpace.ToString().ToLowerInvariant()}.{mipSuffix}";
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
    int MipCount,
    int PixelDataOffset,
    int PixelDataSize,
    CookedAssetHandle Handle)
{
    public bool IsValid => Handle.IsValid;
}

public static class Texture2DAssetCooker
{
    public const int CookedFormatVersion = 1;

    private const string TextureAssetType = "Texture2D";
    private const int HeaderSize = 36;
    private static readonly byte[] s_Magic = Encoding.ASCII.GetBytes("ARISTX2D");

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

        var outputPath = assetDatabase.GetCookedArtifactPath(texture.Guid, variant, ".texture2d");
        var sourceWriteTimeUtc = File.GetLastWriteTimeUtc(sourceAsset.SourcePath);

        if (!assetDatabase.TryGetCookedArtifact(texture.Guid, variant, out _) ||
            !File.Exists(outputPath) ||
            File.GetLastWriteTimeUtc(outputPath) < sourceWriteTimeUtc)
        {
            CookTexture(sourceAsset, texture, variant, outputPath);
        }

        var outputInfo = new FileInfo(outputPath);
        if (!outputInfo.Exists || outputInfo.Length <= HeaderSize)
        {
            throw new InvalidOperationException(
                $"[Texture2DAssetCooker] Texture asset '{texture.Guid}' produced no cooked payload.");
        }

        assetDatabase.RegisterCookedArtifact(new CookedAssetRecord(
            texture.Guid,
            sourceAsset.AssetType,
            variant,
            outputInfo.FullName,
            outputInfo.Length,
            outputInfo.LastWriteTimeUtc));

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

        using var stream = File.Create(outputPath);
        stream.Write(s_Magic);
        WriteInt32(stream, CookedFormatVersion);
        WriteInt32(stream, checked((int)source.Width));
        WriteInt32(stream, checked((int)source.Height));
        WriteInt32(stream, 1);
        WriteInt32(stream, (int)texture.Variant.Format);
        WriteInt32(stream, (int)texture.Variant.ColorSpace);
        WriteInt32(stream, source.RgbaPixels.Length);
        stream.Write(source.RgbaPixels);

        Logger.Log(
            $"[Texture2DAssetCooker] Cooked texture asset {texture.Guid} | Size: {source.Width}x{source.Height} | Variant: {variant} | Output: {outputPath}");
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

        if (width <= 0 || height <= 0 || mipCount <= 0 || pixelDataSize <= 0)
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
        int PixelDataSize);

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
