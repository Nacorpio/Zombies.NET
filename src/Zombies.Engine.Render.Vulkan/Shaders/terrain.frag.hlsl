// Terrain fragment shader. Lighting is the sum of baked voxel light (sky and block), ambient occlusion,
// and direct sun with cascaded shadow maps filtered over 3 by 3 texels. Colour work happens in linear light; the result is gamma encoded at the end.

[[vk::binding(0, 0)]] cbuffer Frame
{
    row_major float4x4 viewProjection;
    row_major float4x4 cascadeMatrix0;
    row_major float4x4 cascadeMatrix1;
    row_major float4x4 cascadeMatrix2;
    float4 cascadeSplits;
    float4 sunDirection;
    float4 sunColor;
    float4 ambientColor;
    float4 fogColor;
    float4 cameraPosition;
    float4 shadowParams;
};

[[vk::combinedImageSampler]][[vk::binding(1, 0)]] Texture2DArray terrainTiles;
[[vk::combinedImageSampler]][[vk::binding(1, 0)]] SamplerState terrainSampler;

[[vk::combinedImageSampler]][[vk::binding(2, 0)]] Texture2DArray shadowMap;
[[vk::combinedImageSampler]][[vk::binding(2, 0)]] SamplerComparisonState shadowSampler;

struct FragmentInput
{
    float4 position : SV_Position;
    float3 world : TEXCOORD0;
    float2 uv : TEXCOORD1;
    nointerpolation uint tile : TEXCOORD2;
    nointerpolation uint face : TEXCOORD3;
    float3 light : TEXCOORD4;
    float viewDepth : TEXCOORD5;
};

// Faces are numbered +X, -X, +Y, -Y, +Z, -Z: the axis is face / 2 and the sign alternates.
// This is arithmetic on purpose. Indexing a constant array with a runtime value makes the compiler copy it into per-thread
// local memory, and on NVIDIA driver 582 that memory was seen to fault the GPU (an invalid write) when frames overlapped.
float3 FaceNormal(uint face)
{
    uint axis = face >> 1;
    float sign = (face & 1) == 0 ? 1.0 : -1.0;
    return float3(axis == 0, axis == 1, axis == 2) * sign;
}

// Ambient occlusion 0 to 3 maps to 0.50, 0.70, 0.85, 1.00.
float OcclusionFactor(float ao)
{
    return ao < 0.5 ? 0.50 : (ao < 1.5 ? 0.70 : (ao < 2.5 ? 0.85 : 1.00));
}

float ShadowFactor(float3 world, float3 normal, float viewDepth)
{
    if (sunDirection.w < 0.5)
    {
        return 1.0;
    }

    // Push the lookup slightly along the surface normal so a surface does not shadow itself.
    float3 p = world + normal * 0.06;
    float4 clip;
    uint cascade;
    if (viewDepth < cascadeSplits.x)
    {
        clip = mul(float4(p, 1.0), cascadeMatrix0);
        cascade = 0;
    }
    else if (viewDepth < cascadeSplits.y)
    {
        clip = mul(float4(p, 1.0), cascadeMatrix1);
        cascade = 1;
    }
    else if (viewDepth < cascadeSplits.z)
    {
        clip = mul(float4(p, 1.0), cascadeMatrix2);
        cascade = 2;
    }
    else
    {
        return 1.0;
    }

    float3 ndc = clip.xyz / clip.w;
    float2 uv = ndc.xy * 0.5 + 0.5;
    if (any(uv < 0.0) || any(uv > 1.0) || ndc.z > 1.0)
    {
        return 1.0;
    }

    float lit = 0.0;
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            float2 offset = float2(x, y) * shadowParams.x;
            lit += shadowMap.SampleCmpLevelZero(shadowSampler, float3(uv + offset, cascade), ndc.z - shadowParams.y);
        }
    }

    return lit / 9.0;
}

float4 main(FragmentInput input) : SV_Target
{
    float3 albedo = terrainTiles.Sample(terrainSampler, float3(input.uv, input.tile)).rgb;
    float3 normal = FaceNormal(input.face);

    float ao = OcclusionFactor(input.light.x);
    float sky = input.light.y / 15.0;
    float block = input.light.z / 15.0;

    // Direct sun only reaches places that can see the sky.
    float outdoors = smoothstep(0.55, 0.95, sky);
    float sunAmount = saturate(dot(normal, sunDirection.xyz)) * outdoors;
    float3 direct = sunColor.rgb * sunAmount * ShadowFactor(input.world, normal, input.viewDepth);

    float3 ambient = ambientColor.rgb * lerp(0.20, 1.0, sky);
    float3 blockLight = float3(1.0, 0.78, 0.45) * (block * block) * 1.3;

    float3 color = albedo * ((ambient + direct) * ao + blockLight);

    float distanceToCamera = length(input.world - cameraPosition.xyz);
    float fog = smoothstep(0.0, 1.0, saturate((distanceToCamera - fogColor.w) / max(cameraPosition.w - fogColor.w, 1.0)));
    color = lerp(color, fogColor.rgb, fog);

    return float4(pow(max(color, 0.0), 1.0 / 2.2), 1.0);
}
