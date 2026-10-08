cbuffer LensParameters : register(b0)
{
    float4 SourceRect; // x/y/width/height in source pixels
    float4 Dimensions; // source width/height, output width/height
    float4 Options;    // sharpness 0..1, border width, image mode, effective scale
    float4 BorderColor;
    float4 Hud; // exact quarter zoom, output overlay opacity, reserved
};
Texture2D<float4> Source : register(t0);
Texture2D<float4> QualityImage : register(t1); // Only the quality resolve pass binds this texture.

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
// CAS weight/normalization adapted from AMD FidelityFX CAS (MIT), with lens
// output-space neighbors fused into the existing 4x4 reconstruction footprint.
// Copyright (c) 2017-2019 Advanced Micro Devices, Inc. All rights reserved.
float3 ClearPicture(float2 p)
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
    [unroll] for(int cubicRow=0;cubicRow<4;cubicRow++)
    [unroll] for(int cubicCol=0;cubicCol<4;cubicCol++) center+=taps[cubicRow*4+cubicCol]*wx[cubicCol]*wy[cubicRow];
    center=clamp(center,low,high);
#ifdef LENS_BILINEAR_CLEAR
    center=bilinearColor;
#endif
    if (Options.x <= 0) return center;
    // Recover the bounded cubic edge response before CAS. Weak source contrast
    // is left alone; this never pushes RGB outside the nearest source colors.
    float sourceGate=smoothstep(0.03,0.15,Luma(high)-Luma(low));
    center=clamp(center+(center-bilinearColor)*(1.4*Options.x*sourceGate),low,high);
    // One output pixel, not one source pixel: high-zoom text must not estimate
    // CAS headroom from a distant black/white pair that always collapses to zero.
    float2 step=min(float2(1,1),SourceRect.zw/Dimensions.zw);
    float westX=f.x-step.x,eastX=f.x+step.x,northY=f.y-step.y,southY=f.y+step.y;
    float3 west=lerp(westX < 0 ? lerp(taps[4],a,westX+1) : lerp(a,b,westX),
        westX < 0 ? lerp(taps[8],c,westX+1) : lerp(c,d,westX),f.y);
    float3 east=lerp(eastX > 1 ? lerp(b,taps[7],eastX-1) : lerp(a,b,eastX),
        eastX > 1 ? lerp(d,taps[11],eastX-1) : lerp(c,d,eastX),f.y);
    float3 north=lerp(northY < 0 ? lerp(taps[1],a,northY+1) : lerp(a,c,northY),
        northY < 0 ? lerp(taps[2],b,northY+1) : lerp(b,d,northY),f.x);
    float3 south=lerp(southY > 1 ? lerp(c,taps[13],southY-1) : lerp(a,c,southY),
        southY > 1 ? lerp(d,taps[14],southY-1) : lerp(b,d,southY),f.x);
    float3 minimum=min(center,min(min(west,east),min(north,south)));
    float3 maximum=max(center,max(max(west,east),max(north,south)));
    float contrast=Luma(maximum)-Luma(minimum);
    float noiseGate=smoothstep(0.008,0.04,contrast)*
        (1-0.5*saturate(abs(Luma(center-(west+east+north+south)*0.25))/max(contrast,0.0001)));
    // AMD's inexpensive gamma-2 approximation keeps CAS out of encoded sRGB;
    // exact sRGB conversion at every tap would burden this responsive mode.
    float3 lc=center*center,lw=west*west,le=east*east,ln=north*north,ls=south*south;
    float3 mn=min(lc,min(min(lw,le),min(ln,ls))),mx=max(lc,max(max(lw,le),max(ln,ls)));
    float3 amplitude=sqrt(saturate(min(mn,1-mx)/max(mx,0.0001)));
    float peak=-1/(8-3*Options.x);
    float3 weight=amplitude*peak*noiseGate*smoothstep(0,0.25,Options.x);
    float3 linearColor=(lc+(lw+le+ln+ls)*weight)/(1+4*weight);
    return clamp(sqrt(max(linearColor,0)),low,high);
}

// Quality scaling and resolve are adapted from AMD FidelityFX FSR 1 EASU/RCAS.
// Copyright (c) 2021 Advanced Micro Devices, Inc. All rights reserved.
// SPDX-License-Identifier: MIT. Full notice is in THIRD_PARTY_NOTICES.txt.
// Adaptations: direct clamped RGB loads, exact FP32 math, arbitrary lens crop,
// 0..1 strength, finite zero-contrast guards, and additional source-local deringing.
float EasuLuma(float3 c) { return c.g+0.5*(c.r+c.b); }
void EasuDirection(inout float2 direction,inout float extent,float weight,
    float north,float west,float center,float east,float south)
{
    float2 delta=float2(east-west,south-north);
    float2 span=max(abs(float2(east,south)-center),abs(center-float2(west,north)));
    float2 agreement=saturate(abs(delta)/max(span,0.0001));
    direction+=delta*weight;
    extent+=dot(agreement,agreement)*weight;
}
void EasuTap(inout float3 color,inout float weightSum,float2 offset,float2 direction,
    float2 extent,float lobe,float cutoff,float3 sampleColor)
{
    float2 v=float2(dot(offset,direction),dot(offset,float2(-direction.y,direction.x)))*extent;
    float radius=min(dot(v,v),cutoff);
    float base=0.4*radius-1, window=lobe*radius-1;
    float weight=(1.5625*base*base-0.5625)*window*window;
    color+=sampleColor*weight; weightSum+=weight;
}
float QualityLanczosWeight(float distance)
{
    distance=abs(distance);
    if (distance < 0.0001) return 1;
    if (distance >= 3) return 0;
    float phase=3.141592653589793*distance;
    return (sin(phase)/phase)*(sin(phase/3)/(phase/3));
}
float3 QualityScale(float2 p)
{
    // Preserve native pixels at integer positions without snapping fractional
    // lens movement to nearest-neighbor steps at 1x.
    if (Options.w <= 1.0001)
    {
        float3 low,high,bilinearValue;
        return Reconstruct(p,low,high,bilinearValue);
    }
    int2 origin=(int2)floor(p); float2 f=frac(p);
    // Quality favors detail retention over tap count. A normalized 6x6 Lanczos-3
    // reconstruction supplies the fine texture/text response missing from the
    // previous all-EASU path. The same footprint supplies its edge estimator.
    float wx[6],wy[6]; float sumX=0,sumY=0;
    [unroll] for(int index=0;index<6;index++)
    {
        wx[index]=QualityLanczosWeight(index-2-f.x); wy[index]=QualityLanczosWeight(index-2-f.y);
        sumX+=wx[index]; sumY+=wy[index];
    }
    float3 taps[36],detailColor=0;
    [unroll] for(int tapRow=0;tapRow<6;tapRow++)
    [unroll] for(int tapCol=0;tapCol<6;tapCol++)
    {
        float3 sampleColor=Pixel(origin+int2(tapCol-2,tapRow-2));
        taps[tapRow*6+tapCol]=sampleColor;
        detailColor+=sampleColor*wx[tapCol]*wy[tapRow];
    }
    detailColor/=max(sumX*sumY,0.0001);
    float3 b=taps[8],c=taps[9],e=taps[13],a=taps[14],g=taps[15],h=taps[16];
    float3 i=taps[19],j=taps[20],k=taps[21],l=taps[22],n=taps[26],o=taps[27];
    float2 direction=0; float extent=0;
    EasuDirection(direction,extent,(1-f.x)*(1-f.y),EasuLuma(b),EasuLuma(e),EasuLuma(a),EasuLuma(g),EasuLuma(j));
    EasuDirection(direction,extent,f.x*(1-f.y),EasuLuma(c),EasuLuma(a),EasuLuma(g),EasuLuma(h),EasuLuma(k));
    EasuDirection(direction,extent,(1-f.x)*f.y,EasuLuma(a),EasuLuma(i),EasuLuma(j),EasuLuma(k),EasuLuma(n));
    EasuDirection(direction,extent,f.x*f.y,EasuLuma(g),EasuLuma(j),EasuLuma(k),EasuLuma(l),EasuLuma(o));
    float squared=dot(direction,direction);
    direction=squared < (1.0/32768.0) ? float2(1,0) : direction*rsqrt(squared);
    extent=saturate(extent*0.5); extent*=extent;
    float stretch=1/max(abs(direction.x),abs(direction.y));
    float2 lengths=float2(lerp(1,stretch,extent),1-0.5*extent);
    float lobe=lerp(0.5,0.21,extent),cutoff=1/lobe;
    float3 color=0; float weight=0;
    EasuTap(color,weight,float2(0,-1)-f,direction,lengths,lobe,cutoff,b);
    EasuTap(color,weight,float2(1,-1)-f,direction,lengths,lobe,cutoff,c);
    EasuTap(color,weight,float2(-1,0)-f,direction,lengths,lobe,cutoff,e);
    EasuTap(color,weight,-f,direction,lengths,lobe,cutoff,a);
    EasuTap(color,weight,float2(1,0)-f,direction,lengths,lobe,cutoff,g);
    EasuTap(color,weight,float2(2,0)-f,direction,lengths,lobe,cutoff,h);
    EasuTap(color,weight,float2(-1,1)-f,direction,lengths,lobe,cutoff,i);
    EasuTap(color,weight,float2(0,1)-f,direction,lengths,lobe,cutoff,j);
    EasuTap(color,weight,float2(1,1)-f,direction,lengths,lobe,cutoff,k);
    EasuTap(color,weight,float2(2,1)-f,direction,lengths,lobe,cutoff,l);
    EasuTap(color,weight,float2(0,2)-f,direction,lengths,lobe,cutoff,n);
    EasuTap(color,weight,float2(1,2)-f,direction,lengths,lobe,cutoff,o);
    float3 low=min(min(a,g),min(j,k)), high=max(max(a,g),max(j,k));
    float3 bilinearColor=lerp(lerp(a,g,f.x),lerp(j,k,f.x),f.y);
    detailColor=clamp(detailColor,low,high);
    float3 edgeColor=clamp(color/max(weight,0.0001),low,high);
    // Smooth only coherent slanted edges. Axis-aligned strokes, intersections
    // and texture keep the sharper reconstruction instead of being averaged.
    float2 d0=float2(EasuLuma(g)-EasuLuma(e),EasuLuma(j)-EasuLuma(b));
    float2 d1=float2(EasuLuma(h)-EasuLuma(a),EasuLuma(k)-EasuLuma(c));
    float2 d2=float2(EasuLuma(k)-EasuLuma(i),EasuLuma(n)-EasuLuma(a));
    float2 d3=float2(EasuLuma(l)-EasuLuma(j),EasuLuma(o)-EasuLuma(g));
    float gradientEnergy=lerp(lerp(dot(d0,d0),dot(d1,d1),f.x),lerp(dot(d2,d2),dot(d3,d3),f.x),f.y);
    float coherence=saturate(squared/max(gradientEnergy,0.0001));
    float sourceContrast=Luma(high)-Luma(low);
    float sourceGate=smoothstep(0.08,0.22,sourceContrast);
    float slantedEdge=coherence*(2*abs(direction.x*direction.y));
    float blend=0.8*slantedEdge*sourceGate;
    float3 result=lerp(detailColor,edgeColor,blend);
    // Recover edge contrast in source space: post-scale RCAS alone loses its
    // leverage as a source edge spreads across many output pixels at 8x/16x.
    result+=(result-bilinearColor)*(lerp(2.6,0.65,slantedEdge)*Options.x*sourceGate);
    result=clamp(result,low,high);
    // A bounded contrast curve retains definition at large zoom ratios where
    // the output-space high-pass has become too small. Endpoints and flat or
    // weak-contrast regions stay unchanged; no new bright/dark halo is allowed.
    float3 range=high-low;
    float3 value=saturate((result-low)/max(range,0.0001));
    value+=value*(1-value)*(2*value-1)*(lerp(0.6,0.15,slantedEdge)*Options.x*sourceGate);
    return low+range*value;
}
float3 QualityPixel(int2 p)
{
    return QualityImage.Load(int3(clamp(p,int2(0,0),int2(Dimensions.zw)-1),0)).rgb;
}
float3 QualityResolve(int2 p)
{
    float3 center=QualityPixel(p);
    if (Options.x <= 0) return center;
    float3 n=QualityPixel(p+int2(0,-1)),w=QualityPixel(p+int2(-1,0));
    float3 e=QualityPixel(p+int2(1,0)),s=QualityPixel(p+int2(0,1));
    float3 low=min(min(n,w),min(e,s)),high=max(max(n,w),max(e,s));
    float3 hitMin=min(low,center)/max(4*high,0.0001);
    float3 hitMax=(1-max(high,center))/min(4*low-4,-0.0001);
    float3 lobes=max(-hitMin,hitMax);
    float lobe=max(-0.1875,min(max(lobes.r,max(lobes.g,lobes.b)),0))*Options.x;
    float nl=EasuLuma(n),wl=EasuLuma(w),el=EasuLuma(e),sl=EasuLuma(s),cl=EasuLuma(center);
    float contrast=max(cl,max(max(nl,wl),max(el,sl)))-min(cl,min(min(nl,wl),min(el,sl)));
    float noise=1-0.5*saturate(abs((nl+wl+el+sl)*0.25-cl)/max(contrast,0.0001));
    // Preserve smooth enlarged gradients instead of sharpening sub-byte variation.
    lobe*=noise*smoothstep(1.0/255.0,4.0/255.0,contrast);
    return (center+(n+w+e+s)*lobe)/(1+4*lobe);
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
float4 ComposeOutput(VertexOutput input,float3 color)
{
    float2 edge=min(input.position.xy,Dimensions.zw-input.position.xy);
    if (min(edge.x,edge.y) < Options.y) color=BorderColor.rgb;
    return float4(saturate(OverlayHud(input.position.xy,color)),1);
}
float4 PSQualityScale(VertexOutput input) : SV_TARGET
{
    return float4(QualityScale(SourceRect.xy+input.uv*SourceRect.zw-0.5),1);
}
float4 PSQualityResolve(VertexOutput input) : SV_TARGET
{
    float3 color=QualityResolve((int2)input.position.xy);
    int2 p=(int2)floor(SourceRect.xy+input.uv*SourceRect.zw-0.5);
    float3 a=Pixel(p),b=Pixel(p+int2(1,0)),c=Pixel(p+int2(0,1)),d=Pixel(p+int2(1,1));
    color=clamp(color,min(min(a,b),min(c,d)),max(max(a,b),max(c,d)));
    return ComposeOutput(input,color);
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
    [branch] if (mode == 0)
    {
        float3 low,high,bilinearValue;
        float3 center=Reconstruct(p,low,high,bilinearValue);
        // The established 0.6.9 performance path is preserved exactly.
        float contrast=max(high.r-low.r,max(high.g-low.g,high.b-low.b));
        float strength=Options.x*2.5/(1+contrast);
        color=clamp(center+strength*(center-bilinearValue),low,high);
    }
    else if (mode == 3) color=Pixel((int2)floor(p+0.5));
    else color=ClearPicture(p);
#endif
    return ComposeOutput(input,color);
}
