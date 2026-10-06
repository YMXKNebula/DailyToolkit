using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Controls;

public partial class LensColorPicker : UserControl
{
    private GamingViewModel? _model;
    private readonly Swatch[] _swatches=[new(LensBorderColor.Default,"绿色"),new("#4285F4","蓝色"),
        new("#F5C542","黄色"),new("#F28C45","橙色"),new("#E85C65","红色"),
        new("#A675D1","紫色"),new("#FFFFFF","白色"),new("#202020","黑色")];

    public LensColorPicker()
    {
        InitializeComponent();
        Palette.ItemsSource=_swatches;
        Loaded += (_,_) => Attach();
        DataContextChanged += (_,_) => { if (IsLoaded) Attach(); };
        Unloaded += (_,_) => { if (_model is not null) _model.PropertyChanged -= SettingsChanged; _model=null; };
    }

    private void Attach()
    {
        if (_model is not null) _model.PropertyChanged -= SettingsChanged;
        _model=DataContext as GamingViewModel;
        if (_model is not null) _model.PropertyChanged += SettingsChanged;
        UpdateSelection();
    }
    private void SettingsChanged(object? sender,PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(GamingViewModel.BorderColor)) UpdateSelection(); }
    private void UpdateSelection()
    { foreach (var swatch in _swatches) swatch.IsSelected=swatch.Color == _model?.BorderColor; }
    private void SelectSwatch(object sender,RoutedEventArgs e)
    { if (_model is not null && ((Button)sender).Tag is string color) _model.BorderColor=color; }

    private void ChooseCustomColor(object sender,RoutedEventArgs e)
    {
        if (_model is null || Window.GetWindow(this) is not { } owner) return;
        var colors=Marshal.AllocHGlobal(16*sizeof(int));
        try
        {
            for (var i=0;i<16;i++) Marshal.WriteInt32(colors,i*sizeof(int),0xFFFFFF);
            var rgb=_model.BorderColorRgb;
            var dialog=new ChooseColorOptions
            {
                Size=(uint)Marshal.SizeOf<ChooseColorOptions>(),Owner=new WindowInteropHelper(owner).Handle,
                Result=ToColorRef(rgb),CustomColors=colors,Flags=3 // RGBINIT | FULLOPEN
            };
            if (ChooseColor(ref dialog)) _model.BorderColor=$"#{ToColorRef(dialog.Result):X6}";
        }
        finally { Marshal.FreeHGlobal(colors); }
    }
    // COLORREF stores red in the low byte; the app stores #RRGGBB.
    private static uint ToColorRef(uint color) => (color & 255) << 16 | (color & 0xFF00) | (color >> 16 & 255);
    [StructLayout(LayoutKind.Sequential)] private struct ChooseColorOptions
    {
        public uint Size;
        public IntPtr Owner,Instance;
        public uint Result;
        public IntPtr CustomColors;
        public uint Flags;
        public IntPtr CustomData,Hook,Template;
    }
    [DllImport("comdlg32.dll",EntryPoint="ChooseColorW")]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ChooseColor(ref ChooseColorOptions options);

    private sealed class Swatch(string color,string name) : ObservableObject
    {
        private bool _selected;
        public string Color { get; } = color;
        public string Name { get; } = name;
        public bool IsSelected { get => _selected; set => Set(ref _selected,value); }
    }
}
