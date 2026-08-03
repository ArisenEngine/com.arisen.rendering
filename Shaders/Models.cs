namespace ArisenEngine.ShaderLab;

using System.Collections.Generic;

// ShaderLab相关模型
public class RenderStateValue
{
    public string stateName = string.Empty; // "Blend", "ZTest" ...
    public bool isReference;
    public string referenceName = string.Empty; // 如果是引用

    public enum ValueKind
    {
        None,
        String,
        Int,
        Float
    }

    public ValueKind kind;
    public string stringValue = string.Empty;
    public int intValue;
    public float floatValue;

    public override string ToString()
    {
        return isReference
            ? $"[{referenceName}]"
            : kind switch
            {
                ValueKind.String => stringValue,
                ValueKind.Int => intValue.ToString(),
                ValueKind.Float => floatValue.ToString("0.###"),
                _ => "(null)"
            };
    }
}

public class BlendState
{
    public RenderStateValue? SrcColor;
    public RenderStateValue? DstColor;
    public RenderStateValue? SrcAlpha;
    public RenderStateValue? DstAlpha;
}

// 基础渲染状态集合（最小化子集，覆盖常见 ShaderLab 风格语义）
public class RenderStates
{
    // 混合
    public BlendState? Blend;

    // 剔除（Back/Front/Off）
    public string Cull = string.Empty;

    // 深度写入（On/Off）
    public string ZWrite = string.Empty;

    // 深度测试函数（LEqual/Less/Greater/GEqual/Equal/NotEqual/Always/Never）
    public string ZTest = string.Empty;

    // 颜色写掩码（RGBA/None/单通道组合）
    public string ColorMask = string.Empty;

    // Stencil（先提供原始文本，后续可细分字段）
    public string StencilRaw = string.Empty;

    // 偏移：Offset Factor, Units
    public RenderStateValue? OffsetFactor;
    public RenderStateValue? OffsetUnits;

    // 混合运算符（Add、Sub、RevSub、Min、Max 等）
    public string BlendOp = string.Empty;

    // AlphaToMask（On/Off）
    public string AlphaToMask = string.Empty;
}

public class ShaderLabShader
{
    public string name = string.Empty;
    public List<Property> properties = new();
    public List<SubShader> subShaders = new();
    public List<IncludedHLSL> includedHLSLs = new();
    public ShaderLabMaterialContract materialContract = new();
}

public class ShaderLabMaterialContract
{
    public List<string> texture2DRefs = new();
    public List<string> scalarProperties = new();
    public List<string> vector4Properties = new();
}

public class Property
{
    public string name = string.Empty;
    public string displayName = string.Empty;
    public string type = string.Empty;
    public string defaultValue = string.Empty;
}

public class SubShader
{
    public List<Pass> passes = new();

    public List<string> tags = new();

    // HLSLINCLUDE blocks defined at SubShader scope; their code should be prepended to each pass within this SubShader
    public List<string> includeHlslCodes = new();
}

public class IncludedHLSL
{
    public string hlslCode = string.Empty;
    public int passIndex;
    public int subShaderIndex;
}

public class Pass
{
    public string name = string.Empty;

    public string tagsRaw = string.Empty;

    // 解析后的标签字典（例如 { "LightMode": "ForwardBase" }）
    public Dictionary<string, string> tags = new();

    // 渲染状态集合
    public RenderStates states = new();

    // 程序块中的HLSL源码（HLSLPROGRAM..ENDHLSL），用于工具链落地及编译
    public string hlslCode = string.Empty;

    // 从代码与指令解析出的包含文件清单（行内 #include / include_with_pragmas 提取的相对路径）
    public List<string> includedHLSLs = new();

    // Pragma 信息
    public string target = string.Empty; // ps_6_8 / vs_6_8 等
    public string vertexEntry = string.Empty;
    public string fragmentEntry = string.Empty;
    public string geometryEntry = string.Empty;
    public string hullEntry = string.Empty;
    public string domainEntry = string.Empty;
    public List<string> multiCompile = new();
    public List<string> shaderFeature = new();
}

