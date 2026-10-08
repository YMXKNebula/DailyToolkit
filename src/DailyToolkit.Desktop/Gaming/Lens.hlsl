cbuffer LensParameters : register(b0)
{
    float4 SourceRect; // x/y/width/height in source pixels
    float4 Dimensions; // source width/height, output width/height
    float4 Options;    // sharpness 0..1, border width, image mode, effective scale
    float4 BorderColor;
    float4 Hud; // exact quarter zoom, output overlay opacity, reserved
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
float Luma(float3 rgb) { return dot(rgb,float3(0.2126,0.7152,0.0722)); }
float CubicKernel(float t)
{
    t=abs(t);
    return t < 1 ? (1.5*t-2.5)*t*t+1 : t < 2 ? ((-0.5*t+2.5)*t-4)*t+2 : 0;
}
float3 AdaptivePicture(float2 p, bool edgeDirected)
{
    int2 origin=(int2)floor(p);
    float2 f=frac(p);
    float3 taps[16];
    [unroll] for(int row=0;row<4;row++)
    [unroll] for(int col=0;col<4;col++) taps[row*4+col]=Pixel(origin+int2(col-1,row-1));
    float3 a=taps[5], b=taps[6], c=taps[9], d=taps[10];
    float3 low=min(min(a,b),min(c,d)), high=max(max(a,b),max(c,d));
    float3 bilinearColor=lerp(lerp(a,b,f.x),lerp(c,d,f.x),f.y);
    float4 wx=Weights(f.x), wy=Weights(f.y);
    float3 center=0;
    if (edgeDirected)
    {
        float la=Luma(a),lb=Luma(b),lc=Luma(c),ld=Luma(d);
        float2 gradient=float2(lb-la+ld-lc,lc-la+ld-lb)*0.5;
        float magnitude=length(gradient);
        float coherence=saturate(magnitude/(abs(lb-la)+abs(ld-lc)+abs(lc-la)+abs(ld-lb)+0.0001)*2);
        float2 normal=magnitude > 0.0001 ? gradient/magnitude : float2(1,0);
        float2 tangent=float2(-normal.y,normal.x);
        float sum=0;
        [unroll] for(int row=0;row<4;row++)
        [unroll] for(int col=0;col<4;col++)
        {
            float2 delta=float2(col-1,row-1)-f;
            float weight=CubicKernel(dot(delta,normal)*lerp(1,1.3,coherence))*
                CubicKernel(dot(delta,tangent)*lerp(1,0.85,coherence));
            center+=taps[row*4+col]*weight; sum+=weight;
        }
        center=abs(sum)>0.0001 ? center/sum : bilinearColor;
    }
    else
    {
        [unroll] for(int row=0;row<4;row++)
        [unroll] for(int col=0;col<4;col++) center+=taps[row*4+col]*wx[col]*wy[row];
    }
    center=clamp(center,low,high);
#ifdef LENS_BILINEAR_CLEAR
    center=bilinearColor;
#endif
    // Reuse the 4x4 footprint to estimate neighboring reconstruction and contrast.
    float3 west=lerp(lerp(taps[4],a,f.x),lerp(taps[8],c,f.x),f.y);
    float3 east=lerp(lerp(b,taps[7],f.x),lerp(d,taps[11],f.x),f.y);
    float3 north=lerp(lerp(taps[1],taps[2],f.x),lerp(a,b,f.x),f.y);
    float3 south=lerp(lerp(c,d,f.x),lerp(taps[13],taps[14],f.x),f.y);
    float3 minimum=min(center,min(min(west,east),min(north,south)));
    float3 maximum=max(center,max(max(west,east),max(north,south)));
    float3 amplitude=sqrt(saturate(min(minimum,1-maximum)/max(maximum,0.0001)));
    float contrast=Luma(maximum)-Luma(minimum);
    float noiseGate=smoothstep(0.015,0.08,contrast);
    float scaleGate=lerp(1,0.65,saturate((Options.w-6)/10));
    float3 weight=-amplitude*(0.1+0.045*Options.x)*Options.x*noiseGate*scaleGate;
    float3 sharpened=(center+(west+east+north+south)*weight)/(1+4*weight);
    return clamp(sharpened,low,high);
}

// Glyph masks and output-space composition do not sample the capture texture.
static const uint2 Digits[10] = { uint2(0xae62e,0x3a33),uint2(0x210c4,0x3884),uint2(0x4422e,0x7c44),uint2(0x7420f,0x3e10),uint2(0x4a988,0x211f),uint2(0x7843f,0x3e10),uint2(0x7842e,0x3a31),uint2(0x2221f,0x842),uint2(0x7462e,0x3a31),uint2(0xf462e,0x3a10) };
float3 OverlayHud(float2 pixel, float3 color)
{
    if (Hud.y <= 0) return color;
    int quarters=(int)round(Hud.x*4);
    int whole=quarters/4, fraction=(quarters%4)*25;
    int count=whole >= 10 ? 6 : 5;
    float2 size=float2(count*12+20,30);
    float2 origin=float2(floor((Dimensions.z-size.x)*0.5),8);
    float2 local=pixel-origin;
    float2 distance=abs(local-size*0.5)-size*0.5+6;
    float rounded=length(max(distance,0))+min(max(distance.x,distance.y),0)-6;
    float coverage=1-smoothstep(-0.5,0.5,rounded);
    if (coverage <= 0) return color;
    color=lerp(color,float3(0.055,0.075,0.09),coverage*Hud.y*0.78);
    int2 letter=(int2)floor((local-float2(10,8))/2);
    int index=letter.x/6;
    int x=letter.x-index*6, y=letter.y;
    if (letter.x < 0 || y < 0 || y >= 7 || x >= 5 || index >= count) return color;
    int dot=whole >= 10 ? 2 : 1;
    bool ink=false;
    if (index == dot) ink=(x == 2 && y == 6);
    else if (index == count-1) ink=(y >= 1 && y <= 5 && (x == y-1 || x == 5-y));
    else
    {
        int digit=index < dot ? (whole >= 10 && index == 0 ? whole/10 : whole%10) :
            (index == dot+1 ? fraction/10 : fraction%10);
        uint2 mask=Digits[digit];
        uint bit=y < 4 ? (mask.x >> (x+y*5)) : (mask.y >> (x+(y-4)*5));
        ink=(bit&1) != 0;
    }
    return ink ? lerp(color,float3(0.97,0.98,1),Hud.y) : color;
}
float4 PS(VertexOutput input) : SV_TARGET
{
    float2 p = SourceRect.xy + input.uv * SourceRect.zw - 0.5;
    int mode=(int)Options.z;
#ifdef LENS_FORCE_MODE
    mode=LENS_FORCE_MODE;
#endif
    float3 color;
#ifdef LENS_BILINEAR_ONLY
    int2 origin=(int2)floor(p); float2 f=frac(p);
    color=lerp(lerp(Pixel(origin),Pixel(origin+int2(1,0)),f.x),lerp(Pixel(origin+int2(0,1)),Pixel(origin+int2(1,1)),f.x),f.y);
#else
    [branch] if (mode == 3) color=Pixel((int2)floor(p+0.5));
    else if (mode == 0)
    {
        float3 low,high,bilinearValue;
        float3 center=Reconstruct(p,low,high,bilinearValue);
        // The established 0.6.9 performance path is preserved exactly.
        float contrast=max(high.r-low.r,max(high.g-low.g,high.b-low.b));
        float strength=Options.x*2.5/(1+contrast);
        color=clamp(center+strength*(center-bilinearValue),low,high);
    }
    else color=AdaptivePicture(p,mode == 2);
#endif
    float2 edge = min(input.position.xy, Dimensions.zw-input.position.xy);
    if (min(edge.x,edge.y) < Options.y) color = BorderColor.rgb;
    return float4(saturate(OverlayHud(input.position.xy,color)),1);
}
