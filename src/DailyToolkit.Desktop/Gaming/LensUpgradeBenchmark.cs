using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DailyToolkit.Core.Gaming;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DailyToolkit.Desktop.Gaming;

internal static class LensUpgradeBenchmark
{
    public static async Task RunAsync(string directory,string referenceShaderPath)
    {
        Directory.CreateDirectory(directory);
        var scenes=LensTestScenes.Create();
        var reference=await File.ReadAllTextAsync(referenceShaderPath);
        using var stream=typeof(LensGpuRenderer).Assembly.GetManifestResourceStream("DailyToolkit.Desktop.Gaming.Lens.hlsl")!;
        using var reader=new StreamReader(stream); var shader=await reader.ReadToEndAsync();
        var variants=new (string Name,LensImageMode Mode,string? Shader)[] {
            ("Performance",LensImageMode.Performance,null),("Clear",LensImageMode.Clear,null),
            ("HighQuality",LensImageMode.HighQuality,null),("Pixel",LensImageMode.Pixel,null),
            ("Bilinear",LensImageMode.Performance,"#define LENS_BILINEAR_ONLY 1\n"+shader),
            ("BilinearCAS",LensImageMode.Clear,"#define LENS_BILINEAR_CLEAR 1\n"+shader),
            ("Baseline069",LensImageMode.Performance,reference) };
        var metrics=await Task.Run(() => Measure(variants));
        var images=await Task.Run(() => Quality(directory,scenes,variants));
        await File.WriteAllTextAsync(Path.Combine(directory,"gpu-and-quality.json"),JsonSerializer.Serialize(new {
            Method="GPU timestamps; no capture, Present, image readback, inference or copy in timed draw loop",
            ActualCaptureFpsMeasured=false, ActualOutputFpsMeasured=false, GpuUtilizationMeasured=false,
            SourceTexture="2048x2048 BGRA8", PassCount=1, AddedIntermediateTextures=0, HudDrawPasses=0,
            TextureLoads=new {Performance="16 cubic + 4 repeated center loads (compiler may merge)",Clear=16,HighQuality=16,Pixel=1,Bilinear=4,BilinearCAS=12},
            Metrics=metrics, Quality=images },new JsonSerializerOptions {WriteIndented=true}));
    }

    private static unsafe object Measure((string Name,LensImageMode Mode,string? Shader)[] variants)
    {
        var results=new List<object>();
        var input=new byte[2048*2048*4];
        for(var y=0;y<2048;y++) for(var x=0;x<2048;x++)
        { var at=(y*2048+x)*4; input[at]=(byte)(x%251); input[at+1]=(byte)(y%253); input[at+2]=(byte)((x/3+y/7)%256); input[at+3]=255; }
        string? adapterName=null;
        foreach(var (width,height) in new[] {(640,384),(1600,1200)})
        foreach(var variant in variants)
        {
            using var renderer=new LensGpuRenderer(IntPtr.Zero,width,height,shaderSource:variant.Shader);
            using(var device=renderer.Device.QueryInterface<IDXGIDevice>()) using(var adapter=device.GetAdapter()) adapterName=adapter.Description.Description;
            fixed(byte* pointer=input)
            {
                using var texture=renderer.Device.CreateTexture2D(new Texture2DDescription {Width=2048,Height=2048,MipLevels=1,ArraySize=1,
                    Format=Format.B8G8R8A8_UNorm,SampleDescription=new(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.ShaderResource},
                    new SubresourceData(new IntPtr(pointer),2048*4));
                foreach(var zoom in new[] {1d,2d,4d,8d,12d,16d})
                {
                    var area=new SourceArea(1024-width/zoom/2+0.125,1024-height/zoom/2+0.375,width/zoom,height/zoom);
                    renderer.Render(texture,area,0.35,present:false,imageMode:variant.Mode);
                    for(var warmup=0;warmup<8;warmup++) renderer.RenderLast(area,0.35,present:false,imageMode:variant.Mode);
                    var timings=Enumerable.Range(0,5).Select(_ => renderer.MeasureGpuRender(area,0.35,64,variant.Mode)).Order().ToArray();
                    var cpu=Stopwatch.StartNew();
                    for(var i=0;i<64;i++) renderer.RenderLast(area,0.35,present:false,imageMode:variant.Mode);
                    cpu.Stop();
                    var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(renderer.ReadOutput()));
                    results.Add(new {Width=width,Height=height,Zoom=zoom,Mode=variant.Name,GpuDrawMs=timings[2],
                        P95GroupMeanGpuMs=timings[4],CpuSubmitMs=cpu.Elapsed.TotalMilliseconds/64,PixelHash=hash,
                        Budgets=new[] {60,120,144,240}.Select(fps => new {TargetFps=fps,BudgetMs=1000d/fps,DrawBudgetPercent=timings[2]*fps/10}).ToArray()});
                }
            }
        }
        return new {Adapter=adapterName,Results=results};
    }

    private static unsafe object[] Quality(string directory,IReadOnlyDictionary<string,BitmapSource> scenes,
        (string Name,LensImageMode Mode,string? Shader)[] variants)
    {
        var metrics=new List<object>(); var selected=variants.Take(6).ToArray();
        foreach(var (name,source) in scenes)
        {
            LensTestScenes.Save(source,Path.Combine(directory,name+"-input.png"));
            var pixels=new byte[128*96*4]; source.CopyPixels(pixels,128*4,0);
            var sheet=new DrawingVisual();
            using(var dc=sheet.RenderOpen())
            {
                dc.DrawRectangle(Brushes.DimGray,null,new(0,0,1536,1100));
                for(var col=0;col<selected.Length;col++)
                {
                    var variant=selected[col];
                    dc.DrawText(new System.Windows.Media.FormattedText(variant.Name,System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,new Typeface("Segoe UI"),16,Brushes.White,1),new(col*256+6,2));
                    using var renderer=new LensGpuRenderer(IntPtr.Zero,256,192,shaderSource:variant.Shader);
                    fixed(byte* pointer=pixels)
                    {
                        using var texture=renderer.Device.CreateTexture2D(new Texture2DDescription {Width=128,Height=96,MipLevels=1,ArraySize=1,
                            Format=Format.B8G8R8A8_UNorm,SampleDescription=new(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.ShaderResource},
                            new SubresourceData(new IntPtr(pointer),128*4));
                        var row=0;
                        foreach(var zoom in new[] {2d,4d,8d,12d,16d})
                        {
                            var area=new SourceArea(64-256/zoom/2,48-192/zoom/2,256/zoom,192/zoom);
                            renderer.Render(texture,area,0.35,present:false,imageMode:variant.Mode);
                            var output=renderer.ReadOutput();
                            var bitmap=BitmapSource.Create(256,192,96,96,PixelFormats.Bgra32,null,output,256*4); bitmap.Freeze();
                            LensTestScenes.Save(bitmap,Path.Combine(directory,$"{name}-{variant.Name}-{zoom:0}x.png"));
                            dc.DrawImage(bitmap,new(col*256,28+row*214,256,192));
                            dc.DrawText(new FormattedText($"{zoom:0}.00×",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,
                                new Typeface("Segoe UI"),12,Brushes.White,1),new(col*256+4,220+row*214)); row++;
                            double drift=0;
                            for(var step=1;step<=4;step++)
                            {
                                renderer.RenderLast(area with {Left=area.Left+step*0.125},0.35,present:false,imageMode:variant.Mode);
                                var shifted=renderer.ReadOutput();
                                for(var i=0;i<output.Length;i+=4) for(var channel=0;channel<3;channel++)
                                    drift+=Math.Pow((shifted[i+channel]-output[i+channel])/255d,2);
                            }
                            metrics.Add(new {Scene=name,Mode=variant.Name,Zoom=zoom,SubpixelShiftRms=Math.Sqrt(drift/(4*256d*192*3)),
                                Output=Path.GetFileName($"{name}-{variant.Name}-{zoom:0}x.png")});
                        }
                    }
                }
            }
            var contact=new RenderTargetBitmap(1536,1100,96,96,PixelFormats.Pbgra32); contact.Render(sheet); contact.Freeze();
            LensTestScenes.Save(contact,Path.Combine(directory,name+"-comparison.png"));
        }
        // One explicit final-output HUD sample on the same generated scene.
        using(var renderer=new LensGpuRenderer(IntPtr.Zero,256,192))
        {
            var source=scenes["GameHud"]; var data=new byte[128*96*4]; source.CopyPixels(data,128*4,0);
            fixed(byte* pointer=data)
            {
                using var texture=renderer.Device.CreateTexture2D(new Texture2DDescription {Width=128,Height=96,MipLevels=1,ArraySize=1,
                    Format=Format.B8G8R8A8_UNorm,SampleDescription=new(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.ShaderResource},new SubresourceData(new IntPtr(pointer),128*4));
                renderer.Render(texture,new(24,24,80,48),0.35,present:false,hud:new(3.25,1),imageMode:LensImageMode.Clear);
                var bitmap=BitmapSource.Create(256,192,96,96,PixelFormats.Bgra32,null,renderer.ReadOutput(),256*4); bitmap.Freeze();
                LensTestScenes.Save(bitmap,Path.Combine(directory,"zoom-hud-3.25.png"));
            }
        }
        return metrics.ToArray();
    }
}
