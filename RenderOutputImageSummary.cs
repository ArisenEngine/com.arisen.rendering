using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Arisen.Native.RHI;

namespace ArisenEngine.Rendering;

internal sealed class RenderOutputImageSummaryArtifact
{
    public int SchemaVersion { get; init; } = 2;
    public string CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow.ToString("O");
    public required string Profile { get; init; }
    public required string OutputKind { get; init; }
    public required string SurfaceId { get; init; }
    public uint FrameIndex { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
    public required string Format { get; init; }
    public required string ChannelOrder { get; init; }
    public required string ColorSpace { get; init; }
    public int BytesPerPixel { get; init; }
    public long ByteCount { get; init; }
    public long PixelCount { get; init; }
    public long FinitePixelCount { get; init; }
    public long NonBlankPixelCount { get; init; }
    public long OpaquePixelCount { get; init; }
    public required double[] MinimumRgb { get; init; }
    public required double[] MaximumRgb { get; init; }
    public required double[] AverageRgb { get; init; }
    public double MinimumLuminance { get; init; }
    public double MaximumLuminance { get; init; }
    public double AverageLuminance { get; init; }
    public required long[] LuminanceHistogram { get; init; }
    public int SpatialGridWidth { get; init; }
    public int SpatialGridHeight { get; init; }
    public required double[] SpatialLuminanceGrid { get; init; }
    public required RenderDepthImageSummaryArtifact Depth { get; init; }
    public required RenderOutputImageSummaryChecks Checks { get; init; }
    public bool Passed => Checks.Passed && Depth.Checks.Passed;
}

internal sealed class RenderOutputImageSummaryChecks
{
    public bool AllPixelsFinite { get; init; }
    public bool HasNonBlankCoverage { get; init; }
    public bool HasLuminanceVariation { get; init; }
    public long RequiredNonBlankPixelCount { get; init; }
    public double RequiredLuminanceRange { get; init; }
    public bool Passed => AllPixelsFinite && HasNonBlankCoverage && HasLuminanceVariation;
}

internal sealed class RenderDepthImageSummaryArtifact
{
    public uint Width { get; init; }
    public uint Height { get; init; }
    public required string Format { get; init; }
    public int BytesPerPixel { get; init; }
    public long ByteCount { get; init; }
    public long PixelCount { get; init; }
    public long FiniteDepthPixelCount { get; init; }
    public long NormalizedDepthPixelCount { get; init; }
    public long ClearDepthPixelCount { get; init; }
    public long WrittenDepthPixelCount { get; init; }
    public double MinimumDepth { get; init; }
    public double MaximumDepth { get; init; }
    public double AverageDepth { get; init; }
    public required long[] DepthHistogram { get; init; }
    public int SpatialGridWidth { get; init; }
    public int SpatialGridHeight { get; init; }
    public required double[] SpatialDepthGrid { get; init; }
    public required RenderDepthImageSummaryChecks Checks { get; init; }
    public bool Passed => Checks.Passed;
}

internal sealed class RenderDepthImageSummaryChecks
{
    public bool AllDepthValuesFinite { get; init; }
    public bool AllDepthValuesNormalized { get; init; }
    public bool HasWrittenDepthCoverage { get; init; }
    public bool HasDepthVariation { get; init; }
    public long RequiredWrittenDepthPixelCount { get; init; }
    public double RequiredDepthRange { get; init; }
    public bool Passed =>
        AllDepthValuesFinite &&
        AllDepthValuesNormalized &&
        HasWrittenDepthCoverage &&
        HasDepthVariation;
}

internal static class RenderDepthImageSummaryBuilder
{
    private const int BytesPerPixel = sizeof(float);
    private const int HistogramBinCount = 16;
    private const int SpatialGridWidth = 4;
    private const int SpatialGridHeight = 4;
    private const double ClearDepthEpsilon = 0.000001;
    private const double WrittenDepthEpsilon = 0.00001;
    private const double RequiredDepthRange = 0.0001;

    public static long GetRequiredByteCount(uint width, uint height, EFormat format)
    {
        if (format != EFormat.FORMAT_D32_SFLOAT)
        {
            throw new NotSupportedException(
                $"Render-depth summary does not support format '{format}'.");
        }

        if (width == 0 || height == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "Render-depth summary dimensions must be nonzero.");
        }

        return checked((long)width * height * BytesPerPixel);
    }

    public static RenderDepthImageSummaryArtifact Build(
        ReadOnlySpan<byte> pixels,
        uint width,
        uint height,
        EFormat format)
    {
        var requiredByteCount = GetRequiredByteCount(width, height, format);
        if (requiredByteCount > int.MaxValue || pixels.Length != requiredByteCount)
        {
            throw new ArgumentException(
                $"Render-depth summary expected {requiredByteCount} bytes but received {pixels.Length}.",
                nameof(pixels));
        }

        var pixelCount = checked((long)width * height);
        var histogram = new long[HistogramBinCount];
        var spatialDepthSums = new double[SpatialGridWidth * SpatialGridHeight];
        var spatialPixelCounts = new long[SpatialGridWidth * SpatialGridHeight];
        var minimumDepth = double.PositiveInfinity;
        var maximumDepth = double.NegativeInfinity;
        var depthSum = 0.0;
        var finiteDepthPixelCount = 0L;
        var normalizedDepthPixelCount = 0L;
        var clearDepthPixelCount = 0L;
        var writtenDepthPixelCount = 0L;
        var integerWidth = checked((int)width);
        var integerHeight = checked((int)height);

        for (int y = 0; y < integerHeight; y++)
        {
            for (int x = 0; x < integerWidth; x++)
            {
                var offset = checked((y * integerWidth + x) * BytesPerPixel);
                var depth = (double)BinaryPrimitives.ReadSingleLittleEndian(
                    pixels.Slice(offset, BytesPerPixel));
                if (!double.IsFinite(depth))
                {
                    continue;
                }

                finiteDepthPixelCount++;
                minimumDepth = Math.Min(minimumDepth, depth);
                maximumDepth = Math.Max(maximumDepth, depth);
                depthSum += depth;

                var gridX = Math.Min(SpatialGridWidth - 1, x * SpatialGridWidth / integerWidth);
                var gridY = Math.Min(SpatialGridHeight - 1, y * SpatialGridHeight / integerHeight);
                var gridIndex = gridY * SpatialGridWidth + gridX;
                spatialDepthSums[gridIndex] += depth;
                spatialPixelCounts[gridIndex]++;

                if (depth < 0.0 || depth > 1.0)
                {
                    continue;
                }

                normalizedDepthPixelCount++;
                if (Math.Abs(depth - 1.0) <= ClearDepthEpsilon)
                {
                    clearDepthPixelCount++;
                }

                if (depth < 1.0 - WrittenDepthEpsilon)
                {
                    writtenDepthPixelCount++;
                }

                var histogramBin = Math.Min(
                    HistogramBinCount - 1,
                    (int)(depth * HistogramBinCount));
                histogram[histogramBin]++;
            }
        }

        var spatialDepthGrid = new double[spatialDepthSums.Length];
        for (int gridIndex = 0; gridIndex < spatialDepthGrid.Length; gridIndex++)
        {
            spatialDepthGrid[gridIndex] = spatialPixelCounts[gridIndex] == 0
                ? 0.0
                : spatialDepthSums[gridIndex] / spatialPixelCounts[gridIndex];
        }

        var hasFiniteDepth = finiteDepthPixelCount > 0;
        var resolvedMinimumDepth = hasFiniteDepth ? minimumDepth : 0.0;
        var resolvedMaximumDepth = hasFiniteDepth ? maximumDepth : 0.0;
        var requiredWrittenDepthPixelCount = Math.Max(1, pixelCount / 1000);
        var checks = new RenderDepthImageSummaryChecks
        {
            AllDepthValuesFinite = finiteDepthPixelCount == pixelCount,
            AllDepthValuesNormalized = normalizedDepthPixelCount == pixelCount,
            HasWrittenDepthCoverage = writtenDepthPixelCount >= requiredWrittenDepthPixelCount,
            HasDepthVariation = resolvedMaximumDepth - resolvedMinimumDepth >= RequiredDepthRange,
            RequiredWrittenDepthPixelCount = requiredWrittenDepthPixelCount,
            RequiredDepthRange = RequiredDepthRange
        };

        return new RenderDepthImageSummaryArtifact
        {
            Width = width,
            Height = height,
            Format = format.ToString(),
            BytesPerPixel = BytesPerPixel,
            ByteCount = requiredByteCount,
            PixelCount = pixelCount,
            FiniteDepthPixelCount = finiteDepthPixelCount,
            NormalizedDepthPixelCount = normalizedDepthPixelCount,
            ClearDepthPixelCount = clearDepthPixelCount,
            WrittenDepthPixelCount = writtenDepthPixelCount,
            MinimumDepth = resolvedMinimumDepth,
            MaximumDepth = resolvedMaximumDepth,
            AverageDepth = hasFiniteDepth ? depthSum / finiteDepthPixelCount : 0.0,
            DepthHistogram = histogram,
            SpatialGridWidth = SpatialGridWidth,
            SpatialGridHeight = SpatialGridHeight,
            SpatialDepthGrid = spatialDepthGrid,
            Checks = checks
        };
    }
}

internal static class RenderOutputImageSummaryBuilder
{
    private const int BytesPerPixel = 4;
    private const int HistogramBinCount = 16;
    private const int SpatialGridWidth = 4;
    private const int SpatialGridHeight = 4;
    private const byte NonBlankChannelThreshold = 2;
    private const byte OpaqueAlphaThreshold = 250;
    private const double RequiredLuminanceRange = 0.02;

    public static long GetRequiredByteCount(uint width, uint height, EFormat format)
    {
        _ = GetFormatInfo(format);
        if (width == 0 || height == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "Render-output summary dimensions must be nonzero.");
        }

        return checked((long)width * height * BytesPerPixel);
    }

    public static RenderOutputImageSummaryArtifact Build(
        ReadOnlySpan<byte> pixels,
        uint width,
        uint height,
        EFormat format,
        ReadOnlySpan<byte> depthPixels,
        EFormat depthFormat,
        string profile,
        RenderOutputKind outputKind,
        uint surfaceId,
        uint frameIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        var formatInfo = GetFormatInfo(format);
        var requiredByteCount = GetRequiredByteCount(width, height, format);
        var depth = RenderDepthImageSummaryBuilder.Build(
            depthPixels,
            width,
            height,
            depthFormat);
        if (requiredByteCount > int.MaxValue || pixels.Length != requiredByteCount)
        {
            throw new ArgumentException(
                $"Render-output summary expected {requiredByteCount} bytes but received {pixels.Length}.",
                nameof(pixels));
        }

        var pixelCount = checked((long)width * height);
        var minimumRgb = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var maximumRgb = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        var rgbSums = new double[3];
        var luminanceHistogram = new long[HistogramBinCount];
        var spatialLuminanceSums = new double[SpatialGridWidth * SpatialGridHeight];
        var spatialPixelCounts = new long[SpatialGridWidth * SpatialGridHeight];
        var minimumLuminance = double.PositiveInfinity;
        var maximumLuminance = double.NegativeInfinity;
        var luminanceSum = 0.0;
        var finitePixelCount = 0L;
        var nonBlankPixelCount = 0L;
        var opaquePixelCount = 0L;
        var integerWidth = checked((int)width);
        var integerHeight = checked((int)height);

        for (int y = 0; y < integerHeight; y++)
        {
            for (int x = 0; x < integerWidth; x++)
            {
                var offset = checked((y * integerWidth + x) * BytesPerPixel);
                var redByte = formatInfo.BlueFirst ? pixels[offset + 2] : pixels[offset];
                var greenByte = pixels[offset + 1];
                var blueByte = formatInfo.BlueFirst ? pixels[offset] : pixels[offset + 2];
                var alphaByte = pixels[offset + 3];
                var red = DecodeColor(redByte, formatInfo.IsSrgb);
                var green = DecodeColor(greenByte, formatInfo.IsSrgb);
                var blue = DecodeColor(blueByte, formatInfo.IsSrgb);
                var luminance = red * 0.2126 + green * 0.7152 + blue * 0.0722;

                if (double.IsFinite(red) &&
                    double.IsFinite(green) &&
                    double.IsFinite(blue) &&
                    double.IsFinite(luminance))
                {
                    finitePixelCount++;
                }

                if (redByte > NonBlankChannelThreshold ||
                    greenByte > NonBlankChannelThreshold ||
                    blueByte > NonBlankChannelThreshold)
                {
                    nonBlankPixelCount++;
                }

                if (alphaByte >= OpaqueAlphaThreshold)
                {
                    opaquePixelCount++;
                }

                AccumulateChannel(red, 0, minimumRgb, maximumRgb, rgbSums);
                AccumulateChannel(green, 1, minimumRgb, maximumRgb, rgbSums);
                AccumulateChannel(blue, 2, minimumRgb, maximumRgb, rgbSums);
                minimumLuminance = Math.Min(minimumLuminance, luminance);
                maximumLuminance = Math.Max(maximumLuminance, luminance);
                luminanceSum += luminance;

                var histogramBin = Math.Min(
                    HistogramBinCount - 1,
                    (int)(Math.Clamp(luminance, 0.0, 1.0) * HistogramBinCount));
                luminanceHistogram[histogramBin]++;

                var gridX = Math.Min(SpatialGridWidth - 1, x * SpatialGridWidth / integerWidth);
                var gridY = Math.Min(SpatialGridHeight - 1, y * SpatialGridHeight / integerHeight);
                var gridIndex = gridY * SpatialGridWidth + gridX;
                spatialLuminanceSums[gridIndex] += luminance;
                spatialPixelCounts[gridIndex]++;
            }
        }

        var averageRgb = new double[3];
        for (int channel = 0; channel < averageRgb.Length; channel++)
        {
            averageRgb[channel] = rgbSums[channel] / pixelCount;
        }

        var spatialLuminanceGrid = new double[spatialLuminanceSums.Length];
        for (int gridIndex = 0; gridIndex < spatialLuminanceGrid.Length; gridIndex++)
        {
            spatialLuminanceGrid[gridIndex] = spatialPixelCounts[gridIndex] == 0
                ? 0.0
                : spatialLuminanceSums[gridIndex] / spatialPixelCounts[gridIndex];
        }

        var requiredNonBlankPixelCount = Math.Max(1, pixelCount / 100);
        var checks = new RenderOutputImageSummaryChecks
        {
            AllPixelsFinite = finitePixelCount == pixelCount,
            HasNonBlankCoverage = nonBlankPixelCount >= requiredNonBlankPixelCount,
            HasLuminanceVariation = maximumLuminance - minimumLuminance >= RequiredLuminanceRange,
            RequiredNonBlankPixelCount = requiredNonBlankPixelCount,
            RequiredLuminanceRange = RequiredLuminanceRange
        };

        return new RenderOutputImageSummaryArtifact
        {
            Profile = profile,
            OutputKind = outputKind.ToString(),
            SurfaceId = $"0x{surfaceId:X}",
            FrameIndex = frameIndex,
            Width = width,
            Height = height,
            Format = format.ToString(),
            ChannelOrder = formatInfo.BlueFirst ? "BGRA" : "RGBA",
            ColorSpace = formatInfo.IsSrgb ? "linearized-sRGB" : "linear",
            BytesPerPixel = BytesPerPixel,
            ByteCount = requiredByteCount,
            PixelCount = pixelCount,
            FinitePixelCount = finitePixelCount,
            NonBlankPixelCount = nonBlankPixelCount,
            OpaquePixelCount = opaquePixelCount,
            MinimumRgb = minimumRgb,
            MaximumRgb = maximumRgb,
            AverageRgb = averageRgb,
            MinimumLuminance = minimumLuminance,
            MaximumLuminance = maximumLuminance,
            AverageLuminance = luminanceSum / pixelCount,
            LuminanceHistogram = luminanceHistogram,
            SpatialGridWidth = SpatialGridWidth,
            SpatialGridHeight = SpatialGridHeight,
            SpatialLuminanceGrid = spatialLuminanceGrid,
            Depth = depth,
            Checks = checks
        };
    }

    private static void AccumulateChannel(
        double value,
        int channel,
        double[] minimumRgb,
        double[] maximumRgb,
        double[] sums)
    {
        minimumRgb[channel] = Math.Min(minimumRgb[channel], value);
        maximumRgb[channel] = Math.Max(maximumRgb[channel], value);
        sums[channel] += value;
    }

    private static double DecodeColor(byte value, bool isSrgb)
    {
        var normalized = value / 255.0;
        if (!isSrgb)
        {
            return normalized;
        }

        return normalized <= 0.04045
            ? normalized / 12.92
            : Math.Pow((normalized + 0.055) / 1.055, 2.4);
    }

    private static RenderOutputFormatInfo GetFormatInfo(EFormat format)
    {
        return format switch
        {
            EFormat.FORMAT_R8G8B8A8_UNORM => new RenderOutputFormatInfo(false, false),
            EFormat.FORMAT_R8G8B8A8_SRGB => new RenderOutputFormatInfo(false, true),
            EFormat.FORMAT_B8G8R8A8_UNORM => new RenderOutputFormatInfo(true, false),
            EFormat.FORMAT_B8G8R8A8_SRGB => new RenderOutputFormatInfo(true, true),
            _ => throw new NotSupportedException(
                $"Render-output summary does not support format '{format}'.")
        };
    }

    private readonly record struct RenderOutputFormatInfo(bool BlueFirst, bool IsSrgb);
}

internal static class RenderOutputImageSummaryWriter
{
    private static readonly JsonSerializerOptions s_JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void WriteAtomic(string outputPath, RenderOutputImageSummaryArtifact artifact)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(artifact);

        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException(
                $"Visual-summary path '{fullPath}' has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = fullPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            var json = JsonSerializer.Serialize(artifact, s_JsonOptions);
            File.WriteAllText(temporaryPath, json + Environment.NewLine, new UTF8Encoding(false));
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
