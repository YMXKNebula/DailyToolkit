using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using DailyToolkit.Core.Gaming;
namespace DailyToolkit.Desktop.Controls;
public partial class ColorPicker : UserControl
{
    public static readonly DependencyProperty ColorProperty=DependencyProperty.Register(nameof(Color),typeof(string),typeof(ColorPicker),
        new FrameworkPropertyMetadata("#267A5D",FrameworkPropertyMetadataOptions.BindsTwoWayByDefault),value => LensBorderColor.TryParse(value as string,out _));
    public string Color { get => (string)GetValue(ColorProperty); set => SetValue(ColorProperty,value); }
    public ColorPicker()
    {
        InitializeComponent();
        Palette.ItemsSource=new[] { "#267A5D","#4285F4","#F5C542","#E85C65","#A675D1","#FFFFFF","#202020" };
    }
    private void SelectColor(object sender,RoutedEventArgs e) => SetCurrentValue(ColorProperty,(string)((Button)sender).Tag);
    private void CommitColor(object sender,RoutedEventArgs e)
    {
        var input=HexInput.Text.Trim(); if (!input.StartsWith('#')) input="#"+input;
        if (LensBorderColor.TryParse(input,out var rgb)) SetCurrentValue(ColorProperty,$"#{rgb:X6}");
        HexInput.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
    }
    private void ColorKeyDown(object sender,KeyEventArgs e) { if (e.Key == Key.Enter) { CommitColor(sender,e); e.Handled=true; } }
    private void ChooseCustomColor(object sender,RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { } owner) return;
        var colors=Marshal.AllocHGlobal(16*sizeof(int));
        try
        {
            for (var i=0;i<16;i++) Marshal.WriteInt32(colors,i*sizeof(int),0xFFFFFF);
            LensBorderColor.TryParse(Color,out var rgb);
            var options=new Options { Size=(uint)Marshal.SizeOf<Options>(),Owner=new WindowInteropHelper(owner).Handle,
                Result=ColorRef(rgb),CustomColors=colors,Flags=3 };
            if (ChooseColor(ref options)) SetCurrentValue(ColorProperty,$"#{ColorRef(options.Result):X6}");
        }
        finally { Marshal.FreeHGlobal(colors); }
    }
    private static uint ColorRef(uint rgb) => (rgb & 255)<<16 | (rgb & 0xFF00) | (rgb>>16 & 255);
    [StructLayout(LayoutKind.Sequential)] private struct Options
    {
        public uint Size; public IntPtr Owner,Instance; public uint Result;
        public IntPtr CustomColors; public uint Flags; public IntPtr CustomData,Hook,Template;
    }
    [DllImport("comdlg32.dll",EntryPoint="ChooseColorW")]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ChooseColor(ref Options options);
}
