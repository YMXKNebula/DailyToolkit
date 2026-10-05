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

// Separable Catmull-Rom weights are calculated once per axis, not for every tap.
float4 Weights(float f)
{
    float f2=f*f, f3=f2*f;
    return float4(-0.5*f+f2-0.5*f3,1-2.5*f2+1.5*f3,0.5*f+2*f2-1.5*f3,-0.5*f2+0.5*f3);
}
float3 Pixel(int2 p) { return Source.Load(int3(clamp(p, int2(0,0), int2(Dimensions.xy)-1), 0)).rgb; }
float3 Reconstruct(float2 p,out float3 low,out float3 high,out float3 bilinearValue)
{
    int2 origin = int2(floor(p));
    float2 f = frac(p);
    float4 wx=Weights(f.x), wy=Weights(f.y);
    float3 sum = 0;
    [unroll] for (int y = -1; y <= 2; y++)
    [unroll] for (int x = -1; x <= 2; x++)
        sum += Pixel(origin + int2(x,y)) * wx[x+1] * wy[y+1];
    // Bound cubic overshoot at sharp edges to avoid bright/dark ringing.
    float3 a = Pixel(origin), b = Pixel(origin+int2(1,0));
    float3 c = Pixel(origin+int2(0,1)), d = Pixel(origin+int2(1,1));
    low=min(min(a,b),min(c,d)); high=max(max(a,b),max(c,d));
    bilinearValue=lerp(lerp(a,b,f.x),lerp(c,d,f.x),f.y);
    return clamp(sum,low,high);
}
float4 PS(VertexOutput input) : SV_TARGET
{
    float2 p = SourceRect.xy + input.uv * SourceRect.zw - 0.5;
    float3 low,high,bilinearValue;
    float3 center = Reconstruct(p,low,high,bilinearValue);
    // Enhance cubic detail over bilinear at the source scale, reusing those same taps.
    // No extra reconstruction or texture fetches; bounded colors avoid edge halos.
    float contrast=max(high.r-low.r,max(high.g-low.g,high.b-low.b));
    float strength=Options.x*2.5/(1+contrast);
    float3 color=clamp(center+strength*(center-bilinearValue),low,high);
    float2 edge = min(input.position.xy, Dimensions.zw-input.position.xy);
    if (min(edge.x,edge.y) < Options.y) color = float3(0.20,0.70,0.54);
    return float4(saturate(color),1);
}
