using System.Text;

namespace SpecTuner.Sidecar.Tests.Support;

/// <summary>Builds a minimal but well-formed PE file header (no runnable code) for fixture exes and dlls.</summary>
public static class PeBuilder
{
    public const ushort MachineX64 = 0x8664;
    public const ushort MachineX86 = 0x014C;

    public static byte[] Build(ushort machine = MachineX64, params string[] sectionNames)
    {
        if (sectionNames.Length == 0)
            sectionNames = [".text", ".rdata"];

        var is64 = machine == MachineX64;
        const int peOffset = 0x40;
        const int fileAlignment = 0x200;
        const int sectionAlignment = 0x1000;
        var optionalSize = is64 ? 240 : 224;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // DOS header
        w.Write((ushort)0x5A4D);
        w.Write(new byte[0x3A]);
        w.Write(peOffset);

        // PE signature + COFF header
        w.Write(Encoding.ASCII.GetBytes("PE\0\0"));
        w.Write(machine);
        w.Write((ushort)sectionNames.Length);
        w.Write(0u); // TimeDateStamp
        w.Write(0u); // PointerToSymbolTable
        w.Write(0u); // NumberOfSymbols
        w.Write((ushort)optionalSize);
        w.Write((ushort)(is64 ? 0x0022 : 0x0102));

        // Optional header
        w.Write((ushort)(is64 ? 0x20B : 0x10B));
        w.Write((byte)14); w.Write((byte)0); // linker version
        w.Write((uint)fileAlignment); // SizeOfCode
        w.Write(0u); // SizeOfInitializedData
        w.Write(0u); // SizeOfUninitializedData
        w.Write((uint)sectionAlignment); // AddressOfEntryPoint
        w.Write((uint)sectionAlignment); // BaseOfCode
        if (is64)
        {
            w.Write(0x140000000UL); // ImageBase
        }
        else
        {
            w.Write(0u); // BaseOfData
            w.Write(0x400000u); // ImageBase
        }
        w.Write((uint)sectionAlignment);
        w.Write((uint)fileAlignment);
        w.Write((ushort)6); w.Write((ushort)0); // OS version
        w.Write((ushort)0); w.Write((ushort)0); // image version
        w.Write((ushort)6); w.Write((ushort)0); // subsystem version
        w.Write(0u); // Win32VersionValue
        w.Write((uint)(sectionAlignment * (sectionNames.Length + 1))); // SizeOfImage
        w.Write((uint)fileAlignment); // SizeOfHeaders
        w.Write(0u); // CheckSum
        w.Write((ushort)2); // Subsystem: Windows GUI
        w.Write((ushort)0x8160); // DllCharacteristics
        if (is64)
        {
            w.Write(0x100000UL); w.Write(0x1000UL); w.Write(0x100000UL); w.Write(0x1000UL);
        }
        else
        {
            w.Write(0x100000u); w.Write(0x1000u); w.Write(0x100000u); w.Write(0x1000u);
        }
        w.Write(0u); // LoaderFlags
        w.Write(16u); // NumberOfRvaAndSizes
        w.Write(new byte[16 * 8]);

        // Section headers
        for (var i = 0; i < sectionNames.Length; i++)
        {
            var name = new byte[8];
            Encoding.ASCII.GetBytes(sectionNames[i], 0, Math.Min(8, sectionNames[i].Length), name, 0);
            w.Write(name);
            w.Write((uint)fileAlignment); // VirtualSize
            w.Write((uint)(sectionAlignment * (i + 1))); // VirtualAddress
            w.Write((uint)fileAlignment); // SizeOfRawData
            w.Write((uint)(fileAlignment * (i + 1))); // PointerToRawData
            w.Write(0u); w.Write(0u); w.Write((ushort)0); w.Write((ushort)0);
            w.Write(0x60000020u); // code | execute | read
        }

        // Pad headers, then one zeroed raw block per section.
        w.Write(new byte[fileAlignment - ms.Length]);
        w.Write(new byte[fileAlignment * sectionNames.Length]);
        w.Flush();
        return ms.ToArray();
    }
}
