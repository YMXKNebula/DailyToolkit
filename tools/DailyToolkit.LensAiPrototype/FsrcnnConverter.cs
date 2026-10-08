using System.IO;

namespace DailyToolkit.LensAiPrototype;

internal sealed record FrozenTensor(string Name,int[] Shape,float[] Values);
internal static class FsrcnnConverter
{
    public static FrozenTensor[] Constants(byte[] frozenGraph)
    {
        var tensors=new List<FrozenTensor>();
        foreach(var node in Proto.Read(frozenGraph).Where(f=>f.Number==1))
        {
            var fields=Proto.Read(node.Data);
            if(fields.First(f=>f.Number==2).Text!="Const") continue;
            var name=fields.First(f=>f.Number==1).Text;
            var attribute=fields.Where(f=>f.Number==5).Select(f=>Proto.Read(f.Data)).FirstOrDefault(a=>a.First(f=>f.Number==1).Text=="value");
            if(attribute is null) continue;
            var value=Proto.Read(attribute.First(f=>f.Number==2).Data);
            var tensor=Proto.Read(value.First(f=>f.Number==8).Data);
            if(tensor.First(f=>f.Number==1).Integer!=1) continue; // DT_FLOAT
            var shape=Proto.Read(tensor.First(f=>f.Number==2).Data).Where(f=>f.Number==2)
                .Select(f=>checked((int)Proto.Read(f.Data).First(v=>v.Number==1).Integer)).ToArray();
            var count=shape.Aggregate(1,(a,b)=>a*b);
            var raw=tensor.FirstOrDefault(f=>f.Number==4)?.Data;
            var floats=tensor.Where(f=>f.Number==5).SelectMany(f=>Enumerable.Range(0,f.Data.Length/4).Select(i=>BitConverter.ToSingle(f.Data,i*4))).ToArray();
            var values=raw is {Length:>0} ? Enumerable.Range(0,raw.Length/4).Select(i=>BitConverter.ToSingle(raw,i*4)).ToArray() :
                floats.Length==1 ? Enumerable.Repeat(floats[0],count).ToArray() : floats;
            if(values.Length!=count) throw new InvalidDataException("Frozen tensor length mismatch: "+name);
            tensors.Add(new(name,shape,values));
        }
        return tensors.ToArray();
    }

    public static (byte[] Model,int Parameters) Convert(byte[] frozenGraph)
    {
        var tensors=Constants(frozenGraph).ToDictionary(x=>x.Name);
        var layers=tensors.Values.Where(x=>x.Name.StartsWith('f') && int.TryParse(x.Name[1..],out _)).OrderBy(x=>int.Parse(x.Name[1..])).ToArray();
        if(layers.Length<3 || layers[^1].Shape[3]!=4) throw new InvalidDataException("Expected pretrained 2x luminance FSRCNN.");
        var nodes=new List<byte[]>(); var initializers=new List<byte[]>(); var previous="input"; var parameters=0;
        byte[] Tensor(string name,int[] shape,float[] values)
        {var raw=new byte[values.Length*4];Buffer.BlockCopy(values,0,raw,0,raw.Length);return Proto.Message(Proto.Packed(1,shape.Select(x=>(long)x)),Proto.Number(2,1),Proto.Text(8,name),Proto.Bytes(9,raw));}
        byte[] AttributeInt(string name,long value) => Proto.Message(Proto.Text(1,name),Proto.Number(3,value),Proto.Number(20,2));
        byte[] AttributeInts(string name,long[] values) => Proto.Message(Proto.Text(1,name),Proto.Packed(8,values),Proto.Number(20,7));
        byte[] Node(string op,string[] input,string output,params byte[][] attrs) => Proto.Message(input.Select(x=>Proto.Text(1,x))
            .Concat([Proto.Text(2,output),Proto.Text(3,output),Proto.Text(4,op)]).Concat(attrs.Select(x=>Proto.Bytes(5,x))).ToArray());
        for(var index=0;index<layers.Length;index++)
        {
            var layer=index+1; var frozen=layers[index]; var shape=frozen.Shape;
            var kh=shape[0];var kw=shape[1];var inputChannels=shape[2];var outputChannels=shape[3];
            var values=new float[frozen.Values.Length];
            for(var y=0;y<kh;y++) for(var x=0;x<kw;x++) for(var c=0;c<inputChannels;c++) for(var o=0;o<outputChannels;o++)
                values[((o*inputChannels+c)*kh+y)*kw+x]=frozen.Values[((y*kw+x)*inputChannels+c)*outputChannels+o];
            initializers.Add(Tensor("w"+layer,[outputChannels,inputChannels,kh,kw],values)); parameters+=values.Length;
            var final=index==layers.Length-1;
            var inputs=new List<string>{previous,"w"+layer};
            var bias=tensors["b"+layer]; parameters+=bias.Values.Length;
            if(!final) {initializers.Add(Tensor("b"+layer,[outputChannels],bias.Values));inputs.Add("b"+layer);}
            var next="conv"+layer;
            nodes.Add(Node("Conv",inputs.ToArray(),next,AttributeInts("pads",[(kh-1)/2,(kw-1)/2,(kh-1)/2,(kw-1)/2])));
            if(!final)
            {
                var alpha=tensors["alpha"+layer]; parameters+=alpha.Values.Length;
                initializers.Add(Tensor("a"+layer,[outputChannels,1,1],alpha.Values));
                nodes.Add(Node("PRelu",[next,"a"+layer],"prelu"+layer)); previous="prelu"+layer;
            }
            else
            {
                nodes.Add(Node("DepthToSpace",[next],"scaled",AttributeInt("blocksize",2)));
                initializers.Add(Tensor("finalBias",[1,1,1,1],bias.Values));
                nodes.Add(Node("Add",["scaled","finalBias"],"output"));
            }
        }
        byte[] ValueInfo(string name,string h,string w)
        {
            var shape=Proto.Message(Proto.Bytes(1,Proto.Number(1,1)),Proto.Bytes(1,Proto.Number(1,1)),Proto.Bytes(1,Proto.Text(2,h)),Proto.Bytes(1,Proto.Text(2,w)));
            return Proto.Message(Proto.Text(1,name),Proto.Bytes(2,Proto.Bytes(1,Proto.Message(Proto.Number(1,1),Proto.Bytes(2,shape)))));
        }
        var graph=Proto.Message(nodes.Select(x=>Proto.Bytes(1,x)).Concat([Proto.Text(2,"FSRCNN-small-x2")])
            .Concat(initializers.Select(x=>Proto.Bytes(5,x))).Concat([Proto.Bytes(11,ValueInfo("input","height","width")),Proto.Bytes(12,ValueInfo("output","height2","width2"))]).ToArray());
        var model=Proto.Message(Proto.Number(1,7),Proto.Text(2,"DailyToolkit standalone FSRCNN evaluator"),Proto.Bytes(7,graph),Proto.Bytes(8,Proto.Number(2,13)));
        return (model,parameters);
    }
}
