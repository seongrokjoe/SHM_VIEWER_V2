namespace ShmViewer.Core.Dat;

public enum DatSourceVersion { Version1, Version2 }

[Flags]
public enum DatDifferenceKind { None = 0, Size = 1, Value = 2, Type = 4, Members = 8, RawBytes = 16, LeftOnly = 32, RightOnly = 64, Layout = 128 }
