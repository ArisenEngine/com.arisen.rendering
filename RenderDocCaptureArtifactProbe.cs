using System;
using System.IO;
using System.Threading;

namespace ArisenEngine.Rendering;

internal enum RenderDocCaptureArtifactProbeStatus
{
    Ready = 0,
    Failed = 1
}

internal readonly record struct RenderDocCaptureArtifactProbeResult(
    RenderDocCaptureArtifactProbeStatus Status,
    uint CaptureIndex,
    string CandidatePath,
    long Length,
    string Diagnostic);

internal delegate uint RenderDocCaptureCountReader();

internal delegate bool RenderDocCapturePathReader(
    uint captureIndex,
    out string capturePath,
    out string diagnostic);

internal static class RenderDocCaptureArtifactProbe
{
    public static RenderDocCaptureArtifactProbeResult WaitForPublication(
        uint captureCountBeforeStart,
        string pathTemplate,
        RenderDocCaptureCountReader readCaptureCount,
        RenderDocCapturePathReader tryReadCapturePath,
        AutoResetEvent observationSignal,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathTemplate);
        ArgumentNullException.ThrowIfNull(readCaptureCount);
        ArgumentNullException.ThrowIfNull(tryReadCapturePath);
        ArgumentNullException.ThrowIfNull(observationSignal);

        WaitHandle[] observationHandles =
        [
            observationSignal,
            cancellationToken.WaitHandle
        ];

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint captureCount = readCaptureCount();
                if (captureCount > captureCountBeforeStart)
                {
                    for (uint captureIndex = captureCountBeforeStart;
                         captureIndex < captureCount;
                         captureIndex++)
                    {
                        if (!tryReadCapturePath(
                                captureIndex,
                                out string capturePath,
                                out string pathDiagnostic))
                        {
                            return Failed(pathDiagnostic);
                        }
                        if (!IsExpectedCapturePath(capturePath, pathTemplate))
                        {
                            continue;
                        }

                        var captureFile = new FileInfo(capturePath);
                        if (!captureFile.Exists)
                        {
                            return Failed(
                                $"RenderDoc published capture index {captureIndex} at " +
                                $"'{capturePath}', but the artifact does not exist.");
                        }
                        if (captureFile.Length == 0)
                        {
                            return Failed(
                                $"RenderDoc published capture index {captureIndex} at " +
                                $"'{capturePath}', but the artifact is empty.");
                        }

                        return new RenderDocCaptureArtifactProbeResult(
                            RenderDocCaptureArtifactProbeStatus.Ready,
                            captureIndex,
                            captureFile.FullName,
                            captureFile.Length,
                            $"RenderDoc published capture index {captureIndex} at " +
                            $"'{captureFile.FullName}'.");
                    }
                }

                if (WaitHandle.WaitAny(observationHandles) == 1)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failed(
                $"RenderDoc capture artifact publication probe failed with " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static bool IsExpectedCapturePath(
        string capturePath,
        string pathTemplate)
    {
        string normalizedCapturePath = Path.GetFullPath(capturePath);
        string normalizedTemplate = Path.GetFullPath(pathTemplate);
        return string.Equals(
                   Path.GetExtension(normalizedCapturePath),
                   ".rdc",
                   StringComparison.OrdinalIgnoreCase) &&
               normalizedCapturePath.StartsWith(
                   normalizedTemplate,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static RenderDocCaptureArtifactProbeResult Failed(string diagnostic)
    {
        return new RenderDocCaptureArtifactProbeResult(
            RenderDocCaptureArtifactProbeStatus.Failed,
            0,
            string.Empty,
            0,
            diagnostic);
    }
}
