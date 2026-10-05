using System.IO;
using System.Text.Json;
using DailyToolkit.Core.Gaming;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DailyToolkit.Desktop.Gaming;

// A deterministic, generated test texture; no desktop capture or user input.
internal static class LensRenderBenchmark
{
    public static async Task RunAsync(string outputPath,string referenceShaderPath)
    {
        var reference=await File.ReadAllTextAsync(referenceShaderPath);
        var results=await Task.Run(() => Measure(reference));
        await File.WriteAllTextAsync(outputPath,JsonSerializer.Serialize(new { GpuTimestamps=true,ImageReadbackExcluded=true,Results=results }));
    }

    private static unsafe object[] Measure(string reference)
    {
        var results=new List<object>();
        foreach (var (width,height,zoom) in new[] { (640,384,2d),(640,384,10d),(1600,1200,10d) })
        {
            var times=new double[2];
            var hashes=new string[2];
            for (var variant=0;variant<2;variant++)
            {
                using var renderer=new LensGpuRenderer(IntPtr.Zero,width,height,shaderSource:variant == 0 ? reference : null);
                var pixels=new byte[512*512*4];
                for (var y=0;y<512;y++) for (var x=0;x<512;x++)
                {
                    var at=(y*512+x)*4;
                    pixels[at]=(byte)(x%256); pixels[at+1]=(byte)(y%256);
                    pixels[at+2]=(byte)(((x/4+y/4)%2)*160+40); pixels[at+3]=255;
                }
                fixed (byte* pointer=pixels)
                {
                    using var texture=renderer.Device.CreateTexture2D(new Texture2DDescription
                    {
                        Width=512,Height=512,MipLevels=1,ArraySize=1,Format=Format.B8G8R8A8_UNorm,
                        SampleDescription=new(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.ShaderResource
                    },new SubresourceData(new IntPtr(pointer),512*4));
                    var area=new SourceArea(256-width/zoom/2,256-height/zoom/2,width/zoom,height/zoom);
                    renderer.Render(texture,area,0.35,present:false);
                    hashes[variant]=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(renderer.ReadOutput()));
                    for (var warmup=0;warmup<4;warmup++) { renderer.RenderLast(area,0.35,present:false); renderer.ReadOutput(); }
                    times[variant]=renderer.MeasureGpuRender(area,0.35,64);
                }
            }
            results.Add(new { Width=width,Height=height,Zoom=zoom,BeforeMilliseconds=times[0],AfterMilliseconds=times[1],Speedup=times[0]/times[1],
                BeforePixelHash=hashes[0],AfterPixelHash=hashes[1],PixelOutputIdentical=hashes[0] == hashes[1] });
        }
        return results.ToArray();
    }
}
