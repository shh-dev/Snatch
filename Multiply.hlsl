// Multiply blend: highlight coverage (implicit input s0) × base image (s1)
sampler2D input : register(s0);   // highlighter layer (opaque yellow strokes on transparent)
sampler2D bg    : register(s1);   // base image behind

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float4 h = tex2D(input, uv);          // premultiplied; h.a = stroke coverage
    float4 b = tex2D(bg, uv);             // opaque base
    float3 hc = float3(1.0, 0.949, 0.0);  // highlighter colour (#FFF200)
    float3 o = b.rgb * hc;                // multiply
    return float4(o * h.a, h.a);          // premultiplied output, coverage = h.a
}
