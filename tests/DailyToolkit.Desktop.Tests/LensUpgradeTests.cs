using System.IO;
using System.Runtime.InteropServices;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Presentation;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static void CheckLensUpgrade()
    {
        var directory=Directory.CreateTempSubdirectory("DailyToolkit-lens-upgrade-tests-");
        try
        {
            var path=Path.Combine(directory.FullName,"gaming.json"); var store=new GamingPreferencesStore(path);
            using(var model=new GamingViewModel(store))
            {
                Require(model.Zoom == 2 && model.ZoomText == "2.00×","Default zoom display is not exact");
                model.Zoom=1.26; Require(model.Zoom == 1.25,"UI input was not quantized");
                model.AdjustZoom(60); Require(model.Zoom == 1.25,"Partial wheel tick changed zoom");
                model.IsMovableMode=true; model.AdjustZoom(60); Require(model.Zoom == 1.5,"Wheel remainder was lost on mode change");
                model.AdjustZoom(-240); Require(model.Zoom == 1,"Lower boundary failed");
                model.Zoom=16; model.AdjustZoom(120); Require(model.Zoom == 16,"Upper boundary failed");
                model.Zoom=double.NaN; Require(model.Zoom == 2,"Invalid input did not recover safely");
                model.Zoom=3.24999;
            }
            using(var reopened=new GamingViewModel(store)) Require(reopened.Zoom == 3.25,"Quarter zoom was not restored");
            using(var model=new GamingViewModel(store))
            {
                Require(model.ImageMode == LensImageMode.Clear && model.Sharpness == 35,"New image defaults are unsafe");
                model.Zoom=16; model.ImageMode=LensImageMode.Pixel; Require(!model.CanAdjustSharpness,"Pixel mode permits sharpening");
                model.ImageMode=LensImageMode.Clear; model.Sharpness=55;
                model.ImageMode=LensImageMode.AIEnhanced; Require(model.ImageMode == LensImageMode.HighQuality && model.Status.Contains("不可用"),"Unavailable AI did not fall back");
                model.ImageMode=LensImageMode.Performance;
                Require(model.Sharpening == 0.35 && model.Sharpness == 55,"Performance lost its established strength or the user's clear-mode preference");
                model.IsEnabled=false; model.IsEnabled=true;
                Require(model.Zoom == 16 && model.ImageMode == LensImageMode.Performance,"Re-enabling overwrote image state");
                model.Sharpness=double.NaN; Require(model.Sharpness == 35,"Nonfinite sharpness was accepted");
                model.Sharpness=999; Require(model.Sharpness == 100,"Sharpness upper bound failed");
                model.ImageMode=LensImageMode.HighQuality; model.Sharpness=55;
            }
            using(var reopened=new GamingViewModel(store)) Require(reopened.ImageMode == LensImageMode.HighQuality && reopened.Sharpness == 55,"Image choices did not persist");
            File.WriteAllText(path,"{\"LensImageMode\":99,\"Sharpness\":\"Infinity\",\"Zoom\":3.24,\"ToggleShortcut\":null}");
            Require(store.Load() is { LensImageMode:LensImageMode.Clear,Sharpness:35,Zoom:3.25,ToggleShortcut:null },"Image migration discarded unrelated preferences");
            foreach(var (json,expected) in new[] {("{\"Zoom\":4}",4d),("{\"Zoom\":99,\"ToggleShortcut\":null}",16d),
                ("{\"Zoom\":0}",1d),("{\"Zoom\":\"NaN\"}",2d),("{\"Zoom\":\"Infinity\"}",2d)})
            { File.WriteAllText(path,json); Require(store.Load().Zoom == expected,"Legacy zoom migration failed"); }
            using var renderer=new LensGpuRenderer(IntPtr.Zero,320,192,software:true);
            var pixels=new byte[64*64*4];
            for(var i=0;i<pixels.Length;i+=4) { pixels[i]=160; pixels[i+1]=90; pixels[i+2]=50; pixels[i+3]=255; }
            var pinned=GCHandle.Alloc(pixels,GCHandleType.Pinned);
            try
            {
                using var texture=renderer.Device.CreateTexture2D(new Texture2DDescription { Width=64,Height=64,MipLevels=1,ArraySize=1,
                    Format=Format.B8G8R8A8_UNorm,SampleDescription=new(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.ShaderResource },
                    new SubresourceData(pinned.AddrOfPinnedObject(),64*4));
                var area=new SourceArea(0,0,64,64);
                renderer.Render(texture,area,0.35,present:false); var original=renderer.ReadOutput();
                renderer.RenderLast(area,0.35,present:false,hud:new(3.25,1)); var overlay=renderer.ReadOutput();
                Require(!original.SequenceEqual(overlay),"HUD was not composited into final GPU output");
                for(var y=0;y<192;y++) for(var x=0;x<320;x++) if(y < 8 || y >= 38 || x < 120 || x >= 200)
                { var at=(y*320+x)*4; Require(original.AsSpan(at,4).SequenceEqual(overlay.AsSpan(at,4)),"HUD altered pixels outside its output rectangle"); }
                renderer.RenderLast(area,0.35,present:false,hud:new(16,0));
                Require(original.SequenceEqual(renderer.ReadOutput()),"Hidden HUD changed image output");
            }
            finally { pinned.Free(); }
            for(var y=0;y<64;y++) for(var x=0;x<64;x++)
            {var at=(y*64+x)*4; pixels[at]=(byte)(x%3*100); pixels[at+1]=(byte)(y%5*50); pixels[at+2]=(byte)((x+y)%2*255);}
            pinned=GCHandle.Alloc(pixels,GCHandleType.Pinned);
            try
            {
                using var texture=renderer.Device.CreateTexture2D(new Texture2DDescription {Width=64,Height=64,MipLevels=1,ArraySize=1,
                    Format=Format.B8G8R8A8_UNorm,SampleDescription=new(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.ShaderResource},new SubresourceData(pinned.AddrOfPinnedObject(),64*4));
                renderer.Render(texture,new(0,0,64,64),1,present:false,imageMode:LensImageMode.Pixel);
                var point=renderer.ReadOutput();
                for(var y=2;y<190;y++) for(var x=2;x<318;x++)
                {
                    var sx=(int)Math.Floor((x+0.5)*64/320); var sy=(int)Math.Floor((y+0.5)*64/192);
                    Require(point.AsSpan((y*320+x)*4,3).SequenceEqual(pixels.AsSpan((sy*64+sx)*4,3)),"Pixel mode blended or sharpened source colors");
                }
                renderer.RenderLast(new(0,0,64,64),0.35,present:false,imageMode:LensImageMode.Clear);
                Require(!point.SequenceEqual(renderer.ReadOutput()),"Switching pixel to clear did not update the actual renderer");
                foreach(var mode in new[] {LensImageMode.Clear,LensImageMode.HighQuality,LensImageMode.Performance})
                {
                    renderer.RenderLast(new(0,0,64,64),1,present:false,imageMode:mode); var output=renderer.ReadOutput();
                    for(var y=2;y<190;y++) for(var x=2;x<318;x++)
                    {
                        var px=(x+0.5)*64/320-0.5; var py=(y+0.5)*64/192-0.5;
                        var sx=(int)Math.Floor(px); var sy=(int)Math.Floor(py);
                        for(var channel=0;channel<3;channel++)
                        {
                            var neighbors=new[]{pixels[(Math.Clamp(sy,0,63)*64+Math.Clamp(sx,0,63))*4+channel],
                                pixels[(Math.Clamp(sy,0,63)*64+Math.Clamp(sx+1,0,63))*4+channel],
                                pixels[(Math.Clamp(sy+1,0,63)*64+Math.Clamp(sx,0,63))*4+channel],
                                pixels[(Math.Clamp(sy+1,0,63)*64+Math.Clamp(sx+1,0,63))*4+channel]};
                            var value=output[(y*320+x)*4+channel];
                            Require(value >= neighbors.Min() && value <= neighbors.Max(),"Reconstruction introduced out-of-range edge ringing");
                        }
                    }
                }
            }
            finally { pinned.Free(); }
            Console.WriteLine("PASS Quarter zoom persistence/migration, signed wheel accumulation and final GPU zoom overlay");
        }
        finally { directory.Delete(recursive:true); }
    }
}
