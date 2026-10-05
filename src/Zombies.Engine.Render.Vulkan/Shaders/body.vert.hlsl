// Player body vertex shader. Body vertices are in blocks relative to the body's feet; the body's world position and yaw
// come in a push constant, so one mesh draws every player.

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

struct Push
{
    float4 bodyOrigin;   // xyz: world position of the feet, w: yaw in radians
};

[[vk::push_constant]] Push push;

struct VertexInput
{
    [[vk::location(0)]] float3 position : POSITION0;
    [[vk::location(1)]] float3 normal : NORMAL0;
    [[vk::location(2)]] uint4 color : COLOR0;
};

struct VertexOutput
{
    float4 position : SV_Position;
    float3 world : TEXCOORD0;
    float3 normal : TEXCOORD1;
    float4 color : TEXCOORD2;
    float viewDepth : TEXCOORD3;
};

VertexOutput main(VertexInput input)
{
    VertexOutput output;
    float s = sin(push.bodyOrigin.w);
    float c = cos(push.bodyOrigin.w);

    // Yaw 0 faces north (-Z), matching the camera, so a positive yaw turns right.
    float3 rotated = float3(
        (input.position.x * c) + (input.position.z * s),
        input.position.y,
        (-input.position.x * s) + (input.position.z * c));
    float3 normal = float3(
        (input.normal.x * c) + (input.normal.z * s),
        input.normal.y,
        (-input.normal.x * s) + (input.normal.z * c));

    float3 world = rotated + push.bodyOrigin.xyz;
    output.position = mul(float4(world, 1.0), viewProjection);
    output.world = world;
    output.normal = normal;
    output.color = float4(input.color) / 255.0;
    output.viewDepth = output.position.w;
    return output;
}
