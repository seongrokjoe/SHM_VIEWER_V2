namespace ShmViewer.Core.Dat;

// Correspondence is structural: values and spelling similarity are deliberately irrelevant.
internal sealed class DatMemberPairing
{
    private readonly Dictionary<(BinaryType, BinaryType), Dictionary<string, BinaryMember>> _cache = new();
    internal int CachedTypePairs => _cache.Count;

    internal IReadOnlyDictionary<string, BinaryMember> Get(BinaryType left, BinaryType right, CancellationToken token)
    {
        if (_cache.TryGetValue((left, right), out var cached)) return cached;
        var result = new Dictionary<string, BinaryMember>(StringComparer.Ordinal);
        if (left.Kind == BinaryKind.Record && right.Kind == BinaryKind.Record)
        {
            var a = left.Members.Where(m => !m.IsPadding).ToArray();
            var b = right.Members.Where(m => !m.IsPadding).ToArray();
            var am = a.ToDictionary(m => m.Name, StringComparer.Ordinal);
            var bm = b.ToDictionary(m => m.Name, StringComparer.Ordinal);
            var common = am.Keys.Intersect(bm.Keys, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            var ab = Boundaries(a, common); var bb = Boundaries(b, common);
            var remainingA = a.Where(m => !common.Contains(m.Name)).ToLookup(m => m.Offset);
            var remainingB = b.Where(m => !common.Contains(m.Name)).ToLookup(m => m.Offset);
            foreach (var member in a)
            {
                token.ThrowIfCancellationRequested();
                if (common.Contains(member.Name) || !Independent(left, member)) continue;
                var candidates = remainingB[member.Offset].Where(other => SameLayout(member, other)).ToArray();
                if (candidates.Length != 1) continue;
                var other = candidates[0];
                if (!Independent(right, other) || remainingA[other.Offset].Count(m => SameLayout(m, other)) != 1) continue;
                var bounds = ab[member.Name];
                if (bounds != bb[other.Name]) continue;
                if (bounds.Before != null && !SameAnchor(am[bounds.Before], bm[bounds.Before]) ||
                    bounds.After != null && !SameAnchor(am[bounds.After], bm[bounds.After])) continue;
                result.Add(member.Name, other);
            }
        }
        _cache.Add((left, right), result);
        return result;
    }

    private static Dictionary<string, (string? Before, string? After)> Boundaries(BinaryMember[] members, HashSet<string> common)
    {
        var result = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
        string? before = null, after = null;
        foreach (var member in members)
        {
            result.Add(member.Name, (before, null));
            if (common.Contains(member.Name)) before = member.Name;
        }
        for (var i = members.Length - 1; i >= 0; i--)
        {
            var name = members[i].Name; result[name] = (result[name].Item1, after);
            if (common.Contains(name)) after = name;
        }
        return result;
    }

    private static bool SameAnchor(BinaryMember a, BinaryMember b) => a.Offset == b.Offset &&
        a.BitOffset == b.BitOffset && a.BitWidth == b.BitWidth && a.ExplicitAlignment == b.ExplicitAlignment &&
        a.IsAnonymous == b.IsAnonymous && DatSchemaFile.SameLayout(a.Type, b.Type);

    internal static bool SameLayout(BinaryMember a, BinaryMember b) =>
        a.Offset == b.Offset && a.BitWidth == 0 && b.BitWidth == 0 && !a.Type.IsContainer && !b.Type.IsContainer &&
        a.Type.Kind != BinaryKind.Array && b.Type.Kind != BinaryKind.Array &&
        a.ExplicitAlignment == b.ExplicitAlignment && a.IsAnonymous == b.IsAnonymous && DatSchemaFile.SameLayout(a.Type, b.Type);

    internal static bool Independent(BinaryType parent, BinaryMember selected)
    {
        var start = (long)selected.Offset; var end = start + selected.Type.Size;
        return parent.Members.All(member => member.IsPadding || member.Name == selected.Name ||
            member.Offset >= end || (long)member.Offset + (member.BitWidth > 0 ? (member.BitOffset + member.BitWidth + 7) / 8 : member.Type.Size) <= start);
    }
}
