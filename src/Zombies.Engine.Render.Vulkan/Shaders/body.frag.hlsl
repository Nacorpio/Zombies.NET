// Player body fragment shader. Same lighting model as the terrain: baked sky light, ambient, direct sun with cascaded
// shadows, and fog, so a body sits in the world instead of on top of it.

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

[[vk::combinedImageSampler]][[vk::binding(2, 0)]] Texture2DArray shadowMap;
[[vk::combinedImageSampler]][[vk::binding(2, 0)]] SamplerComparisonState shadowSampler;

struct FragmentInput
{
    float4 position : SV_Position;
    float3 world : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float4 color : TEXCOORD2;
    float viewDepth : TEXCOORD3;
};

float ShadowFactor(float3 world, float3 normal, float viewDepth)
{
    if (sunDirection.w < 0.5)
    {
        return 1.0;
    }

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
    float3 albedo = input.color.rgb;
    float3 normal = normalize(input.normal);

    // A body outdoors is lit by the sky; the terrain's baked light is not available here, so use a fixed sky exposure.
    float sky = 1.0;
    float sunAmount = saturate(dot(normal, sunDirection.xyz)) * sky;
    float3 direct = sunColor.rgb * sunAmount * ShadowFactor(input.world, normal, input.viewDepth);
    float3 ambient = ambientColor.rgb;

    float3 color = albedo * (ambient + direct);

    float distanceToCamera = length(input.world - cameraPosition.xyz);
    float fog = smoothstep(0.0, 1.0, saturate((distanceToCamera - fogColor.w) / max(cameraPosition.w - fogColor.w, 1.0)));
    color = lerp(color, fogColor.rgb, fog);

    return float4(pow(max(color, 0.0), 1.0 / 2.2), 1.0);
}
