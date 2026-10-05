cbuffer LensParameters : register(b0)
{
    float4 SourceRect; // x/y/width/height in source pixels
    float4 Dimensions; // source width/height, output width/height
    float4 Options;    // sharpening, border width, reserved, reserved
};
Texture2D<float4> Source : register(t0);

struct VertexOutput { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
VertexOutput VS(uint id : SV_VertexID)
{
    VertexOutput output;
    output.uv = float2((id << 1) & 2, id & 2);
    output.position = float4(output.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return output;
}

// Catmull-Rom cubic reconstruction: 16 source samples instead of stretched screenshot pixels.
float Cubic(float x)
{
    x = abs(x);
    return x < 1 ? 1.5*x*x*x - 2.5*x*x + 1 : x < 2 ? -0.5*x*x*x + 2.5*x*x - 4*x + 2 : 0;
}
float3 Pixel(int2 p) { return Source.Load(int3(clamp(p, int2(0,0), int2(Dimensions.xy)-1), 0)).rgb; }
float3 Reconstruct(float2 p)
{
    int2 origin = int2(floor(p));
    float2 f = frac(p);
    float3 sum = 0;
    [unroll] for (int y = -1; y <= 2; y++)
    [unroll] for (int x = -1; x <= 2; x++)
        sum += Pixel(origin + int2(x,y)) * Cubic(x-f.x) * Cubic(y-f.y);
    // Bound cubic overshoot at sharp edges to avoid bright/dark ringing.
    float3 a = Pixel(origin), b = Pixel(origin+int2(1,0));
    float3 c = Pixel(origin+int2(0,1)), d = Pixel(origin+int2(1,1));
    return clamp(sum, min(min(a,b),min(c,d)), max(max(a,b),max(c,d)));
}
float4 PS(VertexOutput input) : SV_TARGET
{
    float2 p = SourceRect.xy + input.uv * SourceRect.zw - 0.5;
    float3 center = Reconstruct(p);
    float2 step = SourceRect.zw / Dimensions.zw;
    float3 up = Reconstruct(p-float2(0,step.y)), down = Reconstruct(p+float2(0,step.y));
    float3 left = Reconstruct(p-float2(step.x,0)), right = Reconstruct(p+float2(step.x,0));
    float3 low = min(center,min(min(up,down),min(left,right)));
    float3 high = max(center,max(max(up,down),max(left,right)));
    // Limit sharpening on already high-contrast edges. No new scene detail is claimed.
    float contrast = max(high.r-low.r, max(high.g-low.g,high.b-low.b));
    float strength = Options.x * (1-saturate(contrast));
    float3 color = clamp(center + strength * (center - (up+down+left+right)*0.25), low, high);
    float2 edge = min(input.position.xy, Dimensions.zw-input.position.xy);
    if (min(edge.x,edge.y) < Options.y) color = float3(0.20,0.70,0.54);
    return float4(saturate(color),1);
}
