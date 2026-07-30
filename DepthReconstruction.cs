namespace ArisenEngine.Rendering;

public enum DeviceDepthConvention
{
    ForwardZeroToOne,
    ReversedZeroToOne
}

public static class DepthReconstruction
{
    public const float DefaultClearEpsilon = 1.0e-6f;

    public static float LinearizeZeroToOne(
        float deviceDepth,
        float nearClip,
        float farClip,
        CameraProjectionType projectionType,
        DeviceDepthConvention convention)
    {
        Validate(deviceDepth, nearClip, farClip, projectionType, convention);
        float forwardDepth = convention == DeviceDepthConvention.ReversedZeroToOne
            ? 1.0f - deviceDepth
            : deviceDepth;

        if (projectionType == CameraProjectionType.Orthographic)
        {
            return nearClip + forwardDepth * (farClip - nearClip);
        }

        return nearClip * farClip /
               (farClip - forwardDepth * (farClip - nearClip));
    }

    public static bool IsClearDepth(
        float deviceDepth,
        DeviceDepthConvention convention,
        float epsilon = DefaultClearEpsilon)
    {
        if (!float.IsFinite(deviceDepth) ||
            !float.IsFinite(epsilon) ||
            epsilon < 0.0f ||
            epsilon > 1.0f ||
            !Enum.IsDefined(convention))
        {
            return false;
        }

        return convention == DeviceDepthConvention.ReversedZeroToOne
            ? deviceDepth <= epsilon
            : deviceDepth >= 1.0f - epsilon;
    }

    private static void Validate(
        float deviceDepth,
        float nearClip,
        float farClip,
        CameraProjectionType projectionType,
        DeviceDepthConvention convention)
    {
        if (!float.IsFinite(deviceDepth) || deviceDepth < 0.0f || deviceDepth > 1.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviceDepth),
                deviceDepth,
                "Device depth must be finite and within [0, 1].");
        }

        if (!float.IsFinite(nearClip) || nearClip <= 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nearClip),
                nearClip,
                "Near clip must be finite and greater than zero.");
        }

        if (!float.IsFinite(farClip) || farClip <= nearClip)
        {
            throw new ArgumentOutOfRangeException(
                nameof(farClip),
                farClip,
                "Far clip must be finite and greater than near clip.");
        }

        if (!Enum.IsDefined(projectionType))
        {
            throw new ArgumentOutOfRangeException(nameof(projectionType));
        }

        if (!Enum.IsDefined(convention))
        {
            throw new ArgumentOutOfRangeException(nameof(convention));
        }
    }
}
