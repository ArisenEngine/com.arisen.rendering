using System;
using System.IO;
using System.Text;

namespace ArisenEngine.Rendering;

/// <summary>
/// Writes the frame a visual summary captured beside the summary itself, as a binary PPM (P6).
/// The summarized artifact is statistics - hashes, histograms, coverage - which cannot be looked
/// at; a dump is what turns "the frame changed" into "this is what the frame shows", for a reviewer
/// and for an investigation that would otherwise guess from numbers.
/// </summary>
internal static class RenderOutputFrameDump
{
    public static void Write(
        string summaryPath,
        ReadOnlySpan<byte> colorPixels,
        uint width,
        uint height,
        string channelOrder)
    {
        if (width == 0 || height == 0)
        {
            return;
        }

        int required = checked((int)(width * height * 4));
        if (colorPixels.Length < required)
        {
            return;
        }

        bool blueFirst = channelOrder.StartsWith("B", StringComparison.OrdinalIgnoreCase);
        string path = Path.ChangeExtension(summaryPath, ".ppm");
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(path);
        stream.Write(Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
        var row = new byte[width * 3];
        for (uint y = 0; y < height; y++)
        {
            for (uint x = 0; x < width; x++)
            {
                int source = checked((int)((y * width + x) * 4));
                row[x * 3] = blueFirst ? colorPixels[source + 2] : colorPixels[source];
                row[x * 3 + 1] = colorPixels[source + 1];
                row[x * 3 + 2] = blueFirst ? colorPixels[source] : colorPixels[source + 2];
            }

            stream.Write(row);
        }
    }
}
