using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace ShmViewer.Core.Dat;

public static class BinaryValueCodec
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
    public static Encoding EncodingFor(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(name == "CP949" ? 949 : name == "UTF-8" ? 65001 : 20127,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
    public static ReadOnlySpan<byte> Bytes(byte[] data, BinaryField field)
    {
        if (field.Offset < 0 || field.ByteLength < 0 || (long)field.Offset + field.ByteLength > data.Length)
            throw new InvalidDataException("DAT 범위를 벗어난 필드: " + field.Path);
        return data.AsSpan(field.Offset, field.ByteLength);
    }
    public static BigInteger ReadInteger(byte[] data, BinaryField field)
    {
        var raw = new BigInteger(Bytes(data, field), true, false);
        var bits = field.BitWidth > 0 ? field.BitWidth : field.Type.Size * 8;
        if (field.BitWidth > 0) raw = (raw >> field.BitOffset) & ((BigInteger.One << bits) - 1);
        if (IsSigned(field.Type) && (raw & (BigInteger.One << (bits - 1))) != 0) raw -= BigInteger.One << bits;
        return raw;
    }
    private static bool IsSigned(BinaryType type) => type.Kind == BinaryKind.Signed || type.Kind is BinaryKind.Char or BinaryKind.Enum && !type.IsUnsigned;
    public static bool Equal(byte[] left, BinaryField a, byte[] right, BinaryField b)
    {
        if (a.Signature != b.Signature) return false;
        return a.BitWidth > 0 ? ReadInteger(left, a) == ReadInteger(right, b) : Bytes(left, a).SequenceEqual(Bytes(right, b));
    }
    public static string Format(byte[] data, BinaryField field, string encoding = "ASCII", bool hex = false)
    {
        var bytes = Bytes(data, field);
        if (hex) return "hex:" + Convert.ToHexString(bytes);
        if (field.IsContainer) return $"({field.Type.Size} bytes)";
        if (field.Type.IsText)
        {
            try
            {
                var decoder = field.Type.Element!.Kind == BinaryKind.WChar ? new UnicodeEncoding(false, false, true) : EncodingFor(encoding);
                var length = bytes.Length;
                var step = field.Type.Element.Kind == BinaryKind.WChar ? 2 : 1;
                for (var i = 0; i + step <= bytes.Length; i += step)
                    if (bytes.Slice(i, step).IndexOfAnyExcept((byte)0) < 0) { length = i; break; }
                return JsonSerializer.Serialize(decoder.GetString(bytes[..length]), new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            }
            catch (DecoderFallbackException) { return "hex:" + Convert.ToHexString(bytes); }
        }
        if (field.Type.Kind == BinaryKind.Float) return BinaryPrimitives.ReadSingleLittleEndian(bytes).ToString("G9", Culture);
        if (field.Type.Kind == BinaryKind.Double) return BinaryPrimitives.ReadDoubleLittleEndian(bytes).ToString("G17", Culture);
        var integer = ReadInteger(data, field);
        if (field.Type.Kind == BinaryKind.Bool) return integer == 0 ? "false" : "true";
        if (field.Type.Kind == BinaryKind.Pointer) return "0x" + integer.ToString("X16", Culture);
        if (field.Type.Kind == BinaryKind.Enum)
        {
            var label = field.Type.EnumValues.FirstOrDefault(p => p.Value == integer).Key;
            if (label != null) return $"{label} ({integer})";
        }
        return integer.ToString(Culture);
    }
    public static void WriteText(byte[] data, BinaryField field, string text, string encoding = "ASCII")
    {
        Bytes(data, field);
        text = text.Trim();
        if (text.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = Convert.FromHexString(string.Concat(text[4..].Where(c => !char.IsWhiteSpace(c))));
            if (bytes.Length != field.ByteLength) throw new InvalidDataException($"HEX는 정확히 {field.ByteLength}바이트여야 합니다.");
            if (field.BitWidth > 0) throw new InvalidDataException("비트필드는 정수로 입력하세요.");
            bytes.CopyTo(data, field.Offset); return;
        }
        if (field.IsContainer) throw new InvalidDataException("하위 필드 또는 HEX로 입력하세요.");
        if (field.Type.IsText)
        {
            var value = JsonSerializer.Deserialize<string>(text) ?? throw new InvalidDataException("문자열은 큰따옴표로 감싸세요.");
            var encoder = field.Type.Element!.Kind == BinaryKind.WChar ? new UnicodeEncoding(false, false, true) : EncodingFor(encoding);
            var bytes = encoder.GetBytes(value);
            if (bytes.Length > field.Type.Size) throw new InvalidDataException($"문자열이 {field.Type.Size}바이트를 초과합니다.");
            data.AsSpan(field.Offset, field.Type.Size).Clear(); bytes.CopyTo(data, field.Offset); return;
        }
        if (field.Type.Kind == BinaryKind.Float)
        {
            var value = float.Parse(text, NumberStyles.Float, Culture);
            if (float.IsInfinity(value) && !text.Contains("Infinity", StringComparison.OrdinalIgnoreCase)) throw new OverflowException("float 범위를 초과합니다.");
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(field.Offset), value); return;
        }
        if (field.Type.Kind == BinaryKind.Double)
        {
            var value = double.Parse(text, NumberStyles.Float, Culture);
            if (double.IsInfinity(value) && !text.Contains("Infinity", StringComparison.OrdinalIgnoreCase)) throw new OverflowException("double 범위를 초과합니다.");
            BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(field.Offset), value); return;
        }
        BigInteger integer;
        if (field.Type.Kind == BinaryKind.Bool && bool.TryParse(text, out var flag)) integer = flag ? 1 : 0;
        else if (field.Type.Kind == BinaryKind.Enum && field.Type.EnumValues.TryGetValue(text, out var enumValue)) integer = enumValue;
        else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) integer = BigInteger.Parse("0" + text[2..], NumberStyles.HexNumber, Culture);
        else integer = BigInteger.Parse(text, Culture);
        WriteInteger(data, field, integer);
    }
    public static void WriteInteger(byte[] data, BinaryField field, BigInteger value)
    {
        Bytes(data, field);
        var bits = field.BitWidth > 0 ? field.BitWidth : field.Type.Size * 8;
        var signed = IsSigned(field.Type);
        var min = signed ? -(BigInteger.One << (bits - 1)) : BigInteger.Zero;
        var max = (BigInteger.One << (signed ? bits - 1 : bits)) - 1;
        if (field.Type.Kind == BinaryKind.Bool) max = 1;
        if (value < min || value > max) throw new OverflowException($"{field.Path}: 허용 범위 {min} ~ {max}");
        var mask = (BigInteger.One << bits) - 1;
        var raw = value & mask;
        if (field.BitWidth > 0)
        {
            var previous = new BigInteger(Bytes(data, field), true, false);
            raw = (previous & ~(mask << field.BitOffset)) | (raw << field.BitOffset);
        }
        var span = data.AsSpan(field.Offset, field.ByteLength);
        span.Clear();
        if (!raw.TryWriteBytes(span, out _, true, false)) throw new OverflowException("정수 저장 실패");
    }
    public static void CopyConverted(byte[] source, BinaryField from, byte[] target, BinaryField to)
    {
        if (from.Signature == to.Signature)
        {
            if (from.BitWidth > 0) WriteInteger(target, to, ReadInteger(source, from));
            else Bytes(source, from).CopyTo(target.AsSpan(to.Offset, to.ByteLength));
            return;
        }
        if (from.IsContainer || to.IsContainer || from.Type.IsText || to.Type.IsText)
            throw new InvalidDataException("구조 또는 배열 길이가 다릅니다. 하위 항목을 연결하거나 직접 입력하세요.");
        var sourceFloat = from.Type.Kind is BinaryKind.Float or BinaryKind.Double;
        var targetFloat = to.Type.Kind is BinaryKind.Float or BinaryKind.Double;
        if (!sourceFloat && !targetFloat) { WriteInteger(target, to, ReadInteger(source, from)); return; }
        double number = sourceFloat ? (from.Type.Kind == BinaryKind.Float ? BinaryPrimitives.ReadSingleLittleEndian(Bytes(source, from)) : BinaryPrimitives.ReadDoubleLittleEndian(Bytes(source, from))) : (double)ReadInteger(source, from);
        if (!sourceFloat && (!double.IsFinite(number) || new BigInteger(number) != ReadInteger(source, from))) throw new InvalidDataException("정밀도가 손실되는 변환입니다.");
        if (!targetFloat)
        {
            if (!double.IsFinite(number) || Math.Truncate(number) != number) throw new InvalidDataException("정수로 손실 없이 변환할 수 없습니다.");
            WriteInteger(target, to, new BigInteger(number)); return;
        }
        if (to.Type.Kind == BinaryKind.Float)
        {
            var single = (float)number;
            if (double.IsNaN(number) || (double)single != number) throw new InvalidDataException("float 변환 시 정밀도가 손실됩니다.");
            BinaryPrimitives.WriteSingleLittleEndian(target.AsSpan(to.Offset), single);
        }
        else
        {
            if (double.IsNaN(number)) throw new InvalidDataException("NaN 타입 변환은 HEX 또는 직접 입력을 사용하세요.");
            BinaryPrimitives.WriteDoubleLittleEndian(target.AsSpan(to.Offset), number);
        }
    }
}
