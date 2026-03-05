namespace ShmViewer.Core.Model;

public enum PrimitiveKind
{
    None,
    Char,
    UChar,
    Short,
    UShort,
    Int,
    UInt,
    Long,       // Windows: 4 bytes
    ULong,      // Windows: 4 bytes
    LongLong,
    ULongLong,
    Float,
    Double,
    Bool,
    WChar,
    Pointer     // x64: 8 bytes
}
