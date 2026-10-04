// Sprite vertex shader: turns pixel coordinates (origin top-left) into clip space.

struct VertexInput
{
    float2 position : POSITION;
    float2 uv : TEXCOORD0;
    float4 color : COLOR0;
};

struct VertexOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    float4 color : COLOR0;
};

struct Screen
{
    float2 size;
};

[[vk::push_constant]] Screen screen;

VertexOutput main(VertexInput input)
{
    VertexOutput output;
    // Vulkan clip space has y pointing down, so top-left pixel coordinates map straight across.
    output.position = float4((input.position / screen.size) * 2.0 - 1.0, 0.0, 1.0);
    output.uv = input.uv;
    output.color = input.color;
    return output;
}
