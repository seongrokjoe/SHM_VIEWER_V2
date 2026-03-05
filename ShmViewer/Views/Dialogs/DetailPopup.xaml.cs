using ShmViewer.Core.Mapper;
using ShmViewer.Core.Model;
using ShmViewer.ViewModels;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ShmViewer.Views.Dialogs;

public class EnumListItem
{
    public string Display { get; set; } = string.Empty;
    public bool IsCurrent { get; set; }
}

public class BoolToFontWeightConverter : IValueConverter
{
    public static readonly BoolToFontWeightConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true ? FontWeights.Bold : FontWeights.Normal;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

public class BoolToForegroundConverter : IValueConverter
{
    public static readonly BoolToForegroundConverter Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true
            ? new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0))
            : new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}

public partial class DetailPopup : Window
{
    public DetailPopup(TreeNodeViewModel node, byte[] data)
    {
        InitializeComponent();
        Populate(node, data);
    }

    private void Populate(TreeNodeViewModel node, byte[] data)
    {
        var member = node.MemberInfo;
        HeaderText.Text = $"{node.Name}  =  {node.Value}";
        OffsetText.Text = $"+{node.Offset} bytes";
        SizeText.Text = $"{node.Size} bytes";
        TypeText.Text = node.TypeName;

        if (member == null || member.ResolvedType != null)
        {
            DecText.Text = HexText.Text = BinText.Text = OctText.Text = "(복합 타입)";
            return;
        }

        // Read raw integer value for display
        long? rawValue = TryReadLong(data, node.Offset, node.Size, member);
        if (rawValue == null)
        {
            DecText.Text = HexText.Text = BinText.Text = OctText.Text = "N/A";
            return;
        }

        long v = rawValue.Value;
        DecText.Text = v.ToString();
        HexText.Text = FormatHex(v, node.Size);
        BinText.Text = FormatBin(v, node.Size);
        OctText.Text = $"0o{System.Convert.ToString(v, 8)}";

        // Enum list
        if (member.ResolvedEnum != null)
        {
            EnumSeparator.Visibility = Visibility.Visible;
            EnumHeader.Visibility = Visibility.Visible;
            EnumList.Visibility = Visibility.Visible;

            var items = member.ResolvedEnum.Members
                .Select(kv => new EnumListItem
                {
                    Display = kv.Value == v ? $"  ✅ {kv.Key}  = {kv.Value}  ← 현재값" : $"     {kv.Key}  = {kv.Value}",
                    IsCurrent = kv.Value == v
                }).ToList();
            EnumList.ItemsSource = items;
        }
    }

    private static long? TryReadLong(byte[] data, int offset, int size, MemberInfo member)
    {
        if (offset < 0 || offset + Math.Min(size, 8) > data.Length) return null;

        if (member.BitFieldWidth > 0)
        {
            var storageSize = Math.Min(size > 0 ? size : 4, 8);
            var bytes = new byte[storageSize];
            Array.Copy(data, offset, bytes, 0, Math.Min(storageSize, data.Length - offset));
            ulong raw = storageSize switch
            {
                1 => bytes[0],
                2 => BitConverter.ToUInt16(bytes),
                8 => BitConverter.ToUInt64(bytes),
                _ => BitConverter.ToUInt32(bytes)
            };
            var mask = (1UL << member.BitFieldWidth) - 1;
            return (long)((raw >> member.BitFieldOffset) & mask);
        }

        var buf = new byte[Math.Min(size, 8)];
        Array.Copy(data, offset, buf, 0, buf.Length);

        return size switch
        {
            1 => buf[0],
            2 => BitConverter.ToInt16(buf),
            4 => BitConverter.ToInt32(buf),
            8 => BitConverter.ToInt64(buf),
            _ => BitConverter.ToInt32(buf)
        };
    }

    private static string FormatHex(long value, int size)
    {
        int nibbles = size * 2;
        return $"0x{(ulong)value:X}".PadLeft(nibbles + 2, '0');
    }

    private static string FormatBin(long value, int size)
    {
        var bits = System.Convert.ToString(value, 2).PadLeft(size * 8, '0');
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < bits.Length; i++)
        {
            if (i > 0 && i % 8 == 0) sb.AppendLine();
            else if (i > 0 && i % 4 == 0) sb.Append(' ');
            sb.Append(bits[i]);
        }
        return sb.ToString();
    }
}
