// Terrain vertex shader. Chunk vertices are packed bytes relative to the chunk corner; the chunk origin comes in a push constant.

[[vk::binding(0, 0)]] cbuffer Frame
{
    row_major float4x4 viewProjection;
    row_major float4x4 cascadeMatrix0;
    row_major float4x4 cascadeMatrix1;
    row_major float4x4 cascadeMatrix2;
    float4 cascadeSplits;   // xyz: far edge of each cascade in view depth, w: cascade count
    float4 sunDirection;    // xyz: toward the sun, w: 1 when shadows are on
    float4 sunColor;        // rgb already scaled by the sun's intensity
    float4 ambientColor;
    float4 fogColor;        // rgb, w: distance where fog starts
    float4 cameraPosition;  // xyz, w: distance where fog is complete
    float4 shadowParams;    // x: one shadow texel in uv, y: depth bias
};

struct Push
{
    float4 chunkOrigin;
};

[[vk::push_constant]] Push push;

struct VertexInput
{
    [[vk::location(0)]] uint4 positionAndFace : POSITION0;
    [[vk::location(1)]] uint tile : TEXCOORD0;
    [[vk::location(2)]] uint2 uv : TEXCOORD1;
    [[vk::location(3)]] uint4 light : TEXCOORD2;   // x: ambient occlusion 0-3, y: sky light 0-15, z: block light 0-15
};

struct VertexOutput
{
    float4 position : SV_Position;
    float3 world : TEXCOORD0;
    float2 uv : TEXCOORD1;
    nointerpolation uint tile : TEXCOORD2;
    nointerpolation uint face : TEXCOORD3;
    float3 light : TEXCOORD4;
    float viewDepth : TEXCOORD5;
};

VertexOutput main(VertexInput input)
{
    VertexOutput output;
    float3 world = float3(input.positionAndFace.xyz) + push.chunkOrigin.xyz;
    output.position = mul(float4(world, 1.0), viewProjection);
    output.world = world;
    output.uv = float2(input.uv);
    output.tile = input.tile;
    output.face = input.positionAndFace.w;
    output.light = float3(input.light.xyz);
    output.viewDepth = output.position.w;
    return output;
}
