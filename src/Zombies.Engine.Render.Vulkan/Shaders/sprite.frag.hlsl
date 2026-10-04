// Sprite fragment shader: the atlas supplies coverage in its red channel, the vertex supplies the color.
// A flat rectangle samples the atlas's solid cell, so one pipeline draws both text and boxes.

[[vk::combinedImageSampler]][[vk::binding(0, 0)]] Texture2D atlas;
[[vk::combinedImageSampler]][[vk::binding(0, 0)]] SamplerState atlasSampler;

struct FragmentInput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
    float4 color : COLOR0;
};

float4 main(FragmentInput input) : SV_Target
{
    float coverage = atlas.Sample(atlasSampler, input.uv).r;
    return float4(input.color.rgb, input.color.a * coverage);
}
