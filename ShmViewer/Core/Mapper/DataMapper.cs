using ShmViewer.Core.Model;
using ShmViewer.ViewModels;
using System.Text;

namespace ShmViewer.Core.Mapper;

public class DataMapper
{
    private readonly TypeDatabase _db;

    public DataMapper(TypeDatabase db) => _db = db;

    // 수정 1: SHM 연결 없이 빈 구조 트리 생성 (모든 값 "-")
    public TreeNodeViewModel MapEmpty(TypeInfo rootType)
    {
        var data = new byte[rootType.TotalSize];
        return Map(data, rootType);
    }

    public TreeNodeViewModel Map(byte[] data, TypeInfo rootType)
    {
        var root = new TreeNodeViewModel
        {
            Name = rootType.Name,
            TypeName = rootType.Name,
            Offset = 0,
            Size = rootType.TotalSize,
            Value = string.Empty
        };

        BuildChildren(root, data, rootType, 0);
        return root;
    }

    private void BuildChildren(TreeNodeViewModel parent, byte[] data, TypeInfo typeInfo, int baseOffset)
    {
        foreach (var member in typeInfo.Members)
        {
            if (member.ArrayDims.Length > 1)
            {
                parent.Children.Add(BuildMultiDimArrayNode(data, member, baseOffset, 0, 0));
            }
            else if (member.ArrayCount > 1)
            {
                parent.Children.Add(BuildArrayNode(data, member, baseOffset));
            }
            else
            {
                parent.Children.Add(BuildNode(data, member, baseOffset));
            }
        }
    }

    // 다차원 배열을 계층적 트리 노드로 구성 (e.g. [2][3] → [0]→{[0],[1],[2]}, [1]→{[0],[1],[2]})
    public TreeNodeViewModel BuildMultiDimArrayNode(
        byte[] data, MemberInfo member, int baseOffset, int dimIdx, int flatBase)
    {
        var elemSize = member.ResolvedType?.TotalSize
                       ?? TypeDatabase.GetPrimitiveSize(member.Primitive);
        if (elemSize == 0) elemSize = member.ArrayCount > 0 ? member.Size / member.ArrayCount : 1;

        // 현재 dim 노드
        var dimSuffix = string.Concat(member.ArrayDims.Select(d => $"[{d}]"));
        var node = new TreeNodeViewModel
        {
            Name = dimIdx == 0 ? member.Name : $"[{flatBase}]",
            TypeName = $"{member.TypeName}{dimSuffix}",
            Offset = baseOffset + member.Offset + flatBase * elemSize,
            Size = member.ArrayDims.Skip(dimIdx).Aggregate(1, (a, b) => a * b) * elemSize,
            IsSpare = member.IsSpare
        };

        int dimSize = member.ArrayDims[dimIdx];
        // 현재 dim 아래의 stride (하위 차원 총 원소 수)
        int stride = member.ArrayDims.Skip(dimIdx + 1).Aggregate(1, (a, b) => a * b);

        bool isLastDim = dimIdx == member.ArrayDims.Length - 1;

        for (int i = 0; i < dimSize; i++)
        {
            int childFlatBase = flatBase + i * stride;

            if (isLastDim)
            {
                // 마지막 차원 → 실제 원소 노드
                var elemMember = new MemberInfo
                {
                    Name = $"[{i}]",
                    TypeName = member.TypeName,
                    ResolvedType = member.ResolvedType,
                    ResolvedEnum = member.ResolvedEnum,
                    Primitive = member.Primitive,
                    IsPointer = member.IsPointer,
                    Offset = member.Offset + childFlatBase * elemSize,
                    Size = elemSize,
                    ArrayCount = 1,
                    ArrayDims = Array.Empty<int>(),
                    BitFieldWidth = member.BitFieldWidth,
                    BitFieldOffset = member.BitFieldOffset
                };
                node.Children.Add(BuildNode(data, elemMember, baseOffset));
            }
            else
            {
                // 중간 차원 → 재귀
                var childNode = BuildMultiDimArrayNode(data, member, baseOffset, dimIdx + 1, childFlatBase);
                childNode.Name = $"[{i}]";
                node.Children.Add(childNode);
            }
        }

        return node;
    }

    public TreeNodeViewModel BuildArrayNode(byte[] data, MemberInfo member, int baseOffset)
    {
        // 수정 3: elemSize 계산 개선 — ResolvedType.TotalSize 우선
        var elemSize = member.ResolvedType != null
            ? member.ResolvedType.TotalSize
            : (member.ArrayCount > 0 && member.Size > 0
                ? member.Size / member.ArrayCount
                : 0);

        var node = new TreeNodeViewModel
        {
            Name = member.Name,
            TypeName = $"{member.TypeName}[{member.ArrayCount}]",
            Offset = baseOffset + member.Offset,
            Size = member.Size,
            IsSpare = member.IsSpare
        };

        // char[] → show as string
        if (member.Primitive == PrimitiveKind.Char || member.Primitive == PrimitiveKind.UChar)
        {
            node.Value = ReadCharArray(data, baseOffset + member.Offset, member.Size, member.IsSpare);
            return node;
        }

        // struct/primitive array → expand as [0], [1], ...
        for (int i = 0; i < member.ArrayCount; i++)
        {
            var elemOffset = baseOffset + member.Offset + (i * elemSize);
            var elem = new MemberInfo
            {
                Name = $"[{i}]",
                TypeName = member.TypeName,
                ResolvedType = member.ResolvedType,
                ResolvedEnum = member.ResolvedEnum,
                Primitive = member.Primitive,
                IsPointer = member.IsPointer,
                Offset = elemOffset - baseOffset,
                Size = elemSize,
                ArrayCount = 1,
                ArrayDims = Array.Empty<int>(),
                BitFieldWidth = member.BitFieldWidth,
                BitFieldOffset = member.BitFieldOffset
            };
            node.Children.Add(BuildNode(data, elem, baseOffset));
        }
        return node;
    }

    public TreeNodeViewModel BuildNode(byte[] data, MemberInfo member, int baseOffset)
    {
        var absOffset = baseOffset + member.Offset;
        var node = new TreeNodeViewModel
        {
            Name = member.Name,
            TypeName = FormatTypeName(member),
            Offset = absOffset,
            Size = member.Size,
            IsSpare = member.IsSpare,
            MemberInfo = member
        };

        if (member.ResolvedType != null && !member.IsPointer)
        {
            // 수정 3: Size 보완
            if (node.Size == 0)
                node.Size = member.ResolvedType.TotalSize * member.ArrayCount;
            node.Value = string.Empty;

            // Use lazy loading for nested struct/union nodes
            var nestedMembers = member.ResolvedType.Members
                .Select(m => (m, absOffset))
                .ToList();
            node.SetLazy(nestedMembers, data, this);
        }
        else if (member.Primitive == PrimitiveKind.None && member.ResolvedType == null)
        {
            // 수정 4: 미발견 타입 — hex dump 대신 경고 표시
            node.Value = $"(미발견: {member.TypeName})";
        }
        else
        {
            node.Value = ReadValue(data, member, absOffset);
        }

        return node;
    }

    private string FormatTypeName(MemberInfo m)
    {
        if (m.ArrayCount > 1) return $"{m.TypeName}[{m.ArrayCount}]";
        if (m.IsPointer) return $"{m.TypeName}*";
        if (m.BitFieldWidth > 0) return $"{m.TypeName} : {m.BitFieldWidth}";
        return m.TypeName;
    }

    public string ReadValue(byte[] data, MemberInfo member, int absOffset)
    {
        // 수정 5: 빈 데이터 또는 범위 초과 → "-"
        if (data.Length == 0 || absOffset < 0 || absOffset >= data.Length) return "-";

        if (member.IsPointer)
            return ReadPointer(data, absOffset);

        if (member.BitFieldWidth > 0)
            return ReadBitField(data, member, absOffset).ToString();

        if (member.ResolvedEnum != null)
            return ReadEnum(data, member, absOffset);

        if (member.ArrayCount > 1 &&
            (member.Primitive == PrimitiveKind.Char || member.Primitive == PrimitiveKind.UChar))
            return ReadCharArray(data, absOffset, member.Size, member.IsSpare);

        return member.Primitive switch
        {
            PrimitiveKind.Char => ReadChar(data, absOffset),
            PrimitiveKind.UChar => SafeRead(data, absOffset, 1) is { } b ? b[0].ToString() : "-",
            PrimitiveKind.Short => SafeRead(data, absOffset, 2) is { } s ? BitConverter.ToInt16(s).ToString() : "-",
            PrimitiveKind.UShort => SafeRead(data, absOffset, 2) is { } us ? BitConverter.ToUInt16(us).ToString() : "-",
            PrimitiveKind.Int => SafeRead(data, absOffset, 4) is { } i ? BitConverter.ToInt32(i).ToString() : "-",
            PrimitiveKind.UInt => SafeRead(data, absOffset, 4) is { } ui ? BitConverter.ToUInt32(ui).ToString() : "-",
            PrimitiveKind.Long => SafeRead(data, absOffset, 4) is { } l ? BitConverter.ToInt32(l).ToString() : "-",
            PrimitiveKind.ULong => SafeRead(data, absOffset, 4) is { } ul ? BitConverter.ToUInt32(ul).ToString() : "-",
            PrimitiveKind.LongLong => SafeRead(data, absOffset, 8) is { } ll ? BitConverter.ToInt64(ll).ToString() : "-",
            PrimitiveKind.ULongLong => SafeRead(data, absOffset, 8) is { } ull ? BitConverter.ToUInt64(ull).ToString() : "-",
            PrimitiveKind.Float => SafeRead(data, absOffset, 4) is { } f ? BitConverter.ToSingle(f).ToString("G6") : "-",
            PrimitiveKind.Double => SafeRead(data, absOffset, 8) is { } d ? BitConverter.ToDouble(d).ToString("G10") : "-",
            PrimitiveKind.Bool => SafeRead(data, absOffset, 1) is { } bl ? (bl[0] != 0 ? "true" : "false") : "-",
            PrimitiveKind.WChar => ReadWChar(data, absOffset),
            PrimitiveKind.Pointer => ReadPointer(data, absOffset),
            // 수정 4: PrimitiveKind.None → 미발견 타입 표시
            PrimitiveKind.None => $"(미발견: {member.TypeName})",
            _ => ReadHexDump(data, absOffset, Math.Min(member.Size, 16))
        };
    }

    private string ReadChar(byte[] data, int offset)
    {
        if (offset >= data.Length) return "-";
        var b = data[offset];
        return b is >= 32 and < 127 ? $"'{(char)b}' ({b})" : b.ToString();
    }

    private string ReadWChar(byte[] data, int offset)
    {
        var bytes = SafeRead(data, offset, 2);
        if (bytes == null) return "-";
        var c = BitConverter.ToChar(bytes);
        return c is >= ' ' and < (char)127 ? $"'{c}' ({(int)c})" : ((int)c).ToString();
    }

    private string ReadPointer(byte[] data, int offset)
    {
        var bytes = SafeRead(data, offset, 8);
        if (bytes == null) return "-";
        var addr = BitConverter.ToInt64(bytes);
        return $"0x{addr:X16}";
    }

    private string ReadEnum(byte[] data, MemberInfo member, int offset)
    {
        var bytes = SafeRead(data, offset, Math.Min(member.Size, 8));
        if (bytes == null) return "-";
        long value = member.Size switch
        {
            1 => bytes[0],
            2 => BitConverter.ToInt16(bytes),
            8 => BitConverter.ToInt64(bytes),
            _ => BitConverter.ToInt32(bytes)
        };
        var name = member.ResolvedEnum!.FindName(value);
        return name != null ? $"{name}({value})" : value.ToString();
    }

    private long ReadBitField(byte[] data, MemberInfo member, int offset)
    {
        var storageSize = Math.Min(member.Size > 0 ? member.Size : 4, 8);
        var bytes = SafeRead(data, offset, storageSize);
        if (bytes == null) return 0;

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

    private string ReadCharArray(byte[] data, int offset, int size, bool isSpare)
    {
        if (offset < 0 || offset >= data.Length) return "-";
        var available = Math.Min(size, data.Length - offset);
        var slice = data.AsSpan(offset, available);

        if (isSpare) return ReadHexDump(data, offset, available);

        // Find null terminator
        int nullIdx = slice.IndexOf((byte)0);
        int strLen = nullIdx >= 0 ? nullIdx : available;

        bool isValidString = true;
        for (int i = 0; i < strLen; i++)
        {
            if (slice[i] < 32 && slice[i] != '\t' && slice[i] != '\n' && slice[i] != '\r')
            {
                isValidString = false;
                break;
            }
        }

        if (isValidString)
            return $"\"{Encoding.ASCII.GetString(slice[..strLen])}\"";

        return ReadHexDump(data, offset, available);
    }

    public static string ReadHexDump(byte[] data, int offset, int count)
    {
        if (offset < 0 || offset >= data.Length) return "-";
        var available = Math.Min(count, data.Length - offset);
        var sb = new StringBuilder();
        for (int i = 0; i < available; i++)
        {
            if (i > 0 && i % 8 == 0) sb.Append(' ');
            sb.Append($"{data[offset + i]:X2} ");
        }
        return sb.ToString().TrimEnd();
    }

    private static byte[]? SafeRead(byte[] data, int offset, int count)
    {
        if (offset < 0 || offset + count > data.Length) return null;
        var buf = new byte[count];
        Array.Copy(data, offset, buf, 0, count);
        return buf;
    }

    /// <summary>
    /// Collect all unresolved types in the structure tree without building TreeNodeViewModel.
    /// Returns list of unresolved type references like "ParentType.MemberName → TypeName 미발견"
    /// </summary>
    public List<string> CollectUnresolved(TypeInfo rootType, string parentName = "")
    {
        var unresolved = new List<string>();
        CollectUnresolvedRecursive(rootType, parentName, unresolved);
        return unresolved;
    }

    private void CollectUnresolvedRecursive(TypeInfo typeInfo, string parentName, List<string> unresolved)
    {
        foreach (var member in typeInfo.Members)
        {
            string path = string.IsNullOrEmpty(parentName)
                ? member.Name
                : $"{parentName}.{member.Name}";

            if (member.ResolvedType != null && !member.IsPointer)
            {
                // Recursively check nested struct members
                CollectUnresolvedRecursive(member.ResolvedType, path, unresolved);
            }
            else if (member.Primitive == PrimitiveKind.None && member.ResolvedType == null)
            {
                // Unresolved type found
                unresolved.Add($"{path} → {member.TypeName} 미발견");
            }
        }
    }
}
