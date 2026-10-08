using System.Text;
using System.IO;

namespace DailyToolkit.LensAiPrototype;

// The prototype reads only frozen float constants and writes an ONNX graph.
// Unknown TensorFlow protobuf fields remain opaque; no TensorFlow runtime is used.
internal sealed record Field(int Number,int Wire,ulong Integer,byte[] Data)
{
    public string Text => Encoding.UTF8.GetString(Data);
}
internal static class Proto
{
    public static List<Field> Read(byte[] message)
    {
        using var stream=new MemoryStream(message); var fields=new List<Field>();
        ulong Varint() {ulong value=0; for(var shift=0;shift<64;shift+=7){var b=stream.ReadByte();if(b<0)throw new InvalidDataException();value|=(ulong)(b&127)<<shift;if((b&128)==0)return value;}throw new InvalidDataException();}
        while(stream.Position<stream.Length)
        {
            var tag=Varint(); var number=(int)(tag>>3); var wire=(int)(tag&7);
            if(wire==0) fields.Add(new(number,wire,Varint(),[]));
            else
            {
                var length=wire switch {1=>8,2=>checked((int)Varint()),5=>4,_=>throw new InvalidDataException("Unsupported protobuf wire type.")};
                var data=new byte[length]; stream.ReadExactly(data); fields.Add(new(number,wire,0,data));
            }
        }
        return fields;
    }
    public static byte[] Message(params byte[][] fields) => fields.SelectMany(x=>x).ToArray();
    private static byte[] Varint(ulong value) {var data=new List<byte>();do{var b=(byte)(value&127);value>>=7;data.Add((byte)(b|(value>0?128:0)));}while(value>0);return data.ToArray();}
    public static byte[] Number(int field,long value) => Message(Varint((ulong)(field<<3)),Varint((ulong)value));
    public static byte[] Bytes(int field,byte[] value) => Message(Varint((ulong)(field<<3|2)),Varint((ulong)value.Length),value);
    public static byte[] Text(int field,string value) => Bytes(field,Encoding.UTF8.GetBytes(value));
    public static byte[] Packed(int field,IEnumerable<long> values) => Bytes(field,values.SelectMany(x=>Varint((ulong)x)).ToArray());
}
