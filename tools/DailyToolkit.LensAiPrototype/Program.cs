using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DailyToolkit.Desktop.Gaming;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Vortice.DXGI;
using Vortice.Direct3D11;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Tests;
using System.Runtime.InteropServices;

namespace DailyToolkit.LensAiPrototype;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if(args.Length is <2 or >3) {Console.WriteLine("Usage: DailyToolkit.LensAiPrototype <FSRCNN-small_x2.pb> <output directory> [DXGI adapter index]");return 2;}
        var adapterIndex=args.Length==3 ? int.Parse(args[2]) : 0;
        var output=Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
        try
        {
            var frozen=File.ReadAllBytes(args[0]); var constants=FsrcnnConverter.Constants(frozen);
            using var factory=DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            factory.EnumAdapters1((uint)adapterIndex,out var adapter).CheckError();
            using var selectedAdapter=adapter;var adapterName=selectedAdapter.Description1.Description;
            File.WriteAllText(Path.Combine(output,"frozen-constants.json"),JsonSerializer.Serialize(constants.Select(x=>new{x.Name,x.Shape,Count=x.Values.Length})));
            var (model,parameters)=FsrcnnConverter.Convert(frozen); File.WriteAllBytes(Path.Combine(output,"FSRCNN-small-x2.onnx"),model);
            OrtEnv.Instance().DisableTelemetryEvents();
            using var options=new SessionOptions {ExecutionMode=ExecutionMode.ORT_SEQUENTIAL,EnableMemoryPattern=false,EnableProfiling=true,
                ProfileOutputPathPrefix=Path.Combine(output,"directml-profile")};
            options.AppendExecutionProvider_DML(adapterIndex);
            var initialized=Stopwatch.StartNew(); using var session=new InferenceSession(model,options); initialized.Stop();
            var scenarios=new List<object>(); var fixtures=LensTestScenes.Create();
            float[] Run(float[] input,int width,int height)
            {
                var tensor=new DenseTensor<float>(input,[1,1,height,width]);
                using var result=session.Run([NamedOnnxValue.CreateFromTensor("input",tensor)]);
                var values=result.Single().AsTensor<float>().ToArray();
                if(values.Length!=width*height*4 || values.Any(x=>!float.IsFinite(x))) throw new InvalidDataException("AI output is invalid.");
                return values;
            }
            var comparisonInput=Enumerable.Range(0,40*24).Select(i=>(float)(0.5+0.3*Math.Sin(i*0.37))).ToArray();
            var firstEvaluation=Stopwatch.StartNew();var gpuReference=Run(comparisonInput,40,24);firstEvaluation.Stop();
            float referenceError;
            using(var cpuOptions=new SessionOptions {IntraOpNumThreads=1})
            using(var cpuSession=new InferenceSession(model,cpuOptions))
            using(var cpuResult=cpuSession.Run([NamedOnnxValue.CreateFromTensor("input",new DenseTensor<float>(comparisonInput,[1,1,24,40]))]))
                referenceError=gpuReference.Zip(cpuResult.Single().AsTensor<float>().ToArray(),(gpu,cpu)=>Math.Abs(gpu-cpu)).Max();
            if(referenceError>0.001) throw new InvalidDataException($"DirectML differs from CPU reference: {referenceError}");
            using var gpuProbe=new GpuUsageProbe();
            foreach(var (width,height) in new[] {(40,24),(80,48),(160,96),(320,192),(640,384)})
            {
                var data=new float[width*height]; for(var y=0;y<height;y++) for(var x=0;x<width;x++) data[y*width+x]=(float)(0.5+0.3*Math.Sin(x*0.3)*Math.Cos(y*0.7));
                var cold=Stopwatch.StartNew(); _=Run(data,width,height); cold.Stop();
                for(var i=0;i<5;i++) _=Run(data,width,height);
                gpuProbe.Begin();var cpuBefore=Process.GetCurrentProcess().TotalProcessorTime; var wall=Stopwatch.StartNew(); var times=new List<double>();
                for(var i=0;i<40;i++){var timer=Stopwatch.StartNew();_=Run(data,width,height);times.Add(timer.Elapsed.TotalMilliseconds);}
                wall.Stop(); var cpuAfter=Process.GetCurrentProcess().TotalProcessorTime; times.Sort();var gpuEngines=gpuProbe.Read();
                ulong? vram=null;try {using var adapter3=selectedAdapter.QueryInterface<IDXGIAdapter3>();vram=adapter3.QueryVideoMemoryInfo(0,MemorySegmentGroup.Local).CurrentUsage;}catch(Exception){ }
                scenarios.Add(new{Width=width,Height=height,Scale=2,AverageMs=times.Average(),P95Ms=times[(int)(times.Count*0.95)],
                    InferenceFps=1000/times.Average(),FirstEvaluationMs=cold.Elapsed.TotalMilliseconds,
                    CpuPercent=(cpuAfter-cpuBefore).TotalMilliseconds/wall.Elapsed.TotalMilliseconds/System.Environment.ProcessorCount*100,
                    ActualGpuUtilization=gpuEngines is null ? (double?)null : gpuEngines.Values.Max(),GpuEnginePercent=gpuEngines,ProcessVideoMemoryBytes=vram,
                    EndToEndIncludesCpuTensorUploadAndOutputReadback=true});
            }
            var observations=new List<object>();using var spatialRenderer=new LensGpuRenderer(IntPtr.Zero,256,192);
            foreach(var (name,fixture) in fixtures)
            {
                const int width=128,height=96; var bgra=new byte[width*height*4];fixture.CopyPixels(bgra,width*4,0);
                var input=new float[width*height];for(var i=0;i<input.Length;i++) input[i]=(bgra[i*4+2]*0.299f+bgra[i*4+1]*0.587f+bgra[i*4]*0.114f)/255;
                var predicted=Run(input,width,height); var pixels=new byte[width*height*16];
                float Sample(int channel,double x,double y)
                {
                    var ix=(int)Math.Floor(x);var iy=(int)Math.Floor(y);var fx=x-ix;var fy=y-iy;
                    float At(int px,int py)=>bgra[(Math.Clamp(py,0,height-1)*width+Math.Clamp(px,0,width-1))*4+channel]/255f;
                    return (float)((At(ix,iy)*(1-fx)+At(ix+1,iy)*fx)*(1-fy)+(At(ix,iy+1)*(1-fx)+At(ix+1,iy+1)*fx)*fy);
                }
                for(var y=0;y<height*2;y++) for(var x=0;x<width*2;x++)
                {
                    var sx=(x+0.5)/2-0.5;var sy=(y+0.5)/2-0.5;var r=Sample(2,sx,sy);var g=Sample(1,sx,sy);var b=Sample(0,sx,sy);
                    var baseY=r*0.299f+g*0.587f+b*0.114f;var adjustment=predicted[y*width*2+x]-baseY;var at=(y*width*2+x)*4;
                    pixels[at]=(byte)Math.Clamp(Math.Round((b+adjustment)*255),0,255);pixels[at+1]=(byte)Math.Clamp(Math.Round((g+adjustment)*255),0,255);
                    pixels[at+2]=(byte)Math.Clamp(Math.Round((r+adjustment)*255),0,255);pixels[at+3]=255;
                }
                var bitmap=BitmapSource.Create(width*2,height*2,96,96,PixelFormats.Bgra32,null,pixels,width*8);bitmap.Freeze();
                LensTestScenes.Save(bitmap,Path.Combine(output,name+"-AI-2x.png"));
                var pin=GCHandle.Alloc(pixels,GCHandleType.Pinned);
                try
                {
                    using var texture=spatialRenderer.Device.CreateTexture2D(new Texture2DDescription {Width=256,Height=192,MipLevels=1,ArraySize=1,
                        Format=Format.B8G8R8A8_UNorm,SampleDescription=new(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.ShaderResource},new SubresourceData(pin.AddrOfPinnedObject(),256*4));
                    foreach(var zoom in new[]{2,4,8,12,16})
                    {
                        var roi=new SourceArea((256-512d/zoom)/2,(192-384d/zoom)/2,512d/zoom,384d/zoom);
                        spatialRenderer.Render(texture,roi,0.35,present:false,imageMode:LensImageMode.Clear);
                        var result=BitmapSource.Create(256,192,96,96,PixelFormats.Bgra32,null,spatialRenderer.ReadOutput(),256*4);result.Freeze();
                        LensTestScenes.Save(result,Path.Combine(output,$"{name}-AI-plus-spatial-{zoom}x.png"));
                    }
                }
                finally {pin.Free();}
                observations.Add(new{Scene=name,OutsideLuminanceRange=predicted.Count(x=>x<0 || x>1),Image=Path.Combine(output,name+"-AI-2x.png")});
            }
            var profilePath=session.EndProfiling(); using var profile=JsonDocument.Parse(File.ReadAllText(profilePath));
            var providers=profile.RootElement.EnumerateArray().Where(e=>e.TryGetProperty("args",out var a)&&a.TryGetProperty("provider",out _))
                .Select(e=>e.GetProperty("args").GetProperty("provider").GetString()).GroupBy(x=>x).ToDictionary(g=>g.Key!,g=>g.Count());
            var report=new{Model="FSRCNN-small (pretrained luminance, author Saafke)",License="Apache-2.0",Parameters=parameters,Scale=2,
                FrozenBytes=frozen.Length,OnnxBytes=model.Length,ModelSHA256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(frozen)),
                Runtime=typeof(InferenceSession).Assembly.GetName().Version?.ToString(),ExecutionProvider="DirectML",AdapterIndex=adapterIndex,
                Adapter=adapterName,
                CpuReferenceMaxAbsError=referenceError,
                FirstModelEvaluationMs=firstEvaluation.Elapsed.TotalMilliseconds,
                ProviderProfile=providers,GpuParticipationConfirmed=providers.ContainsKey("DmlExecutionProvider"),InitMs=initialized.Elapsed.TotalMilliseconds,
                Input="CPU float tensor from generated image",Output="CPU float tensor readback; prototype RGB composition on CPU",
                RealTimeD3D11InteropImplemented=false,ProductRuntimeAdded=false,ProductModelBundled=false,
                WorkingSetBytes=Process.GetCurrentProcess().WorkingSet64,PrivateMemoryBytes=Process.GetCurrentProcess().PrivateMemorySize64,
                ActualGpuUtilization=(double?)null,ActualVramBytes=(long?)null,Scenarios=scenarios,Quality=observations};
            File.WriteAllText(Path.Combine(output,"ai-prototype.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine(JsonSerializer.Serialize(new{parameters,report.GpuParticipationConfirmed,report.InitMs,Output=output}));return 0;
        }
        catch(Exception exception)
        {
            File.WriteAllText(Path.Combine(output,"prototype-failure.txt"),exception.ToString());Console.WriteLine(exception.ToString());return 1;
        }
    }
}
