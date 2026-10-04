// Depth-only vertex shader for the shadow cascades. There is no fragment shader: only depth is written.

struct Push
{
    row_major float4x4 lightViewProjection;
    float4 chunkOrigin;
};

[[vk::push_constant]] Push push;

float4 main([[vk::location(0)]] uint4 positionAndFace : POSITION0) : SV_Position
{
    float3 world = float3(positionAndFace.xyz) + push.chunkOrigin.xyz;
    return mul(float4(world, 1.0), push.lightViewProjection);
}
