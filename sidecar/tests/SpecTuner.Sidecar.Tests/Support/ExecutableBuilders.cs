using System.Buffers.Binary;
using System.Text;

namespace SpecTuner.Sidecar.Tests.Support;

/// <summary>A minimal ELF header (no runnable code) for Linux fixture programs.</summary>
public static class ElfBuilder
{
    public const ushort MachineX64 = 0x3E;
    public const ushort MachineX86 = 0x03;
    public const ushort MachineArm64 = 0xB7;

    public static byte[] Build(ushort machine = MachineX64)
    {
        var bytes = new byte[256];
        bytes[0] = 0x7F; bytes[1] = (byte)'E'; bytes[2] = (byte)'L'; bytes[3] = (byte)'F';
        bytes[4] = machine == MachineX86 ? (byte)1 : (byte)2; // class
        bytes[5] = 1; // little-endian
        bytes[6] = 1; // version
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 2); // ET_EXEC
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), machine);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 1);
        return bytes;
    }
}

/// <summary>What a fixture Mach-O slice's code signature says.</summary>
public sealed record MachOSignature(bool HardenedRuntime, params string[] Entitlements);

/// <summary>Minimal Mach-O programs (thin or universal), optionally with a code signature, for macOS fixtures.</summary>
public static class MachOBuilder
{
    public const string Arm64 = "arm64";
    public const string X64 = "x64";
    public const string AllowDyld = "com.apple.security.cs.allow-dyld-environment-variables";
    public const string DisableLibraryValidation = "com.apple.security.cs.disable-library-validation";

    public static byte[] Thin(string arch, MachOSignature? signature = null)
    {
        var cpuType = arch == Arm64 ? 0x0100000Cu : 0x01000007u;
        var signatureBlob = signature is null ? [] : SignatureBlob(signature);
        const int headerSize = 32;
        var commandsSize = signature is null ? 0 : 16;
        var signatureOffset = (headerSize + commandsSize + 15) / 16 * 16;
        var bytes = new byte[signatureOffset + signatureBlob.Length + 64];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), cpuType);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 2); // MH_EXECUTE
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), signature is null ? 0u : 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), (uint)commandsSize);
        if (signature is not null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(headerSize), 0x1D); // LC_CODE_SIGNATURE
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(headerSize + 4), 16);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(headerSize + 8), (uint)signatureOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(headerSize + 12), (uint)signatureBlob.Length);
            signatureBlob.CopyTo(bytes, signatureOffset);
        }
        return bytes;
    }

    /// <summary>A universal ("fat") file holding the given slices.</summary>
    public static byte[] Universal(params byte[][] slices)
    {
        const int align = 4096;
        var offsets = new List<int>();
        var position = align;
        foreach (var slice in slices)
        {
            offsets.Add(position);
            position += (slice.Length + align - 1) / align * align;
        }
        var bytes = new byte[position];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)slices.Length);
        for (var i = 0; i < slices.Length; i++)
        {
            var entry = bytes.AsSpan(8 + i * 20);
            BinaryPrimitives.WriteUInt32BigEndian(entry, BinaryPrimitives.ReadUInt32LittleEndian(slices[i].AsSpan(4)));
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)offsets[i]);
            BinaryPrimitives.WriteUInt32BigEndian(entry[12..], (uint)slices[i].Length);
            BinaryPrimitives.WriteUInt32BigEndian(entry[16..], 12);
            slices[i].CopyTo(bytes, offsets[i]);
        }
        return bytes;
    }

    private static byte[] SignatureBlob(MachOSignature signature)
    {
        var codeDirectory = new byte[48];
        BinaryPrimitives.WriteUInt32BigEndian(codeDirectory, 0xFADE0C02);
        BinaryPrimitives.WriteUInt32BigEndian(codeDirectory.AsSpan(4), (uint)codeDirectory.Length);
        BinaryPrimitives.WriteUInt32BigEndian(codeDirectory.AsSpan(8), 0x20400);
        BinaryPrimitives.WriteUInt32BigEndian(codeDirectory.AsSpan(12), signature.HardenedRuntime ? 0x10000u : 0x2u);

        var plist = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict>");
        foreach (var key in signature.Entitlements)
            plist.Append("\n\t<key>").Append(key).Append("</key>\n\t<true/>");
        plist.Append("</dict></plist>");
        var plistBytes = Encoding.UTF8.GetBytes(plist.ToString());
        var entitlements = new byte[8 + plistBytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(entitlements, 0xFADE7171);
        BinaryPrimitives.WriteUInt32BigEndian(entitlements.AsSpan(4), (uint)entitlements.Length);
        plistBytes.CopyTo(entitlements, 8);

        const int indexStart = 12 + 2 * 8;
        var blob = new byte[indexStart + codeDirectory.Length + entitlements.Length];
        BinaryPrimitives.WriteUInt32BigEndian(blob, 0xFADE0CC0);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(4), (uint)blob.Length);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(12), 0); // CodeDirectory
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(16), indexStart);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(20), 5); // entitlements
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(24), (uint)(indexStart + codeDirectory.Length));
        codeDirectory.CopyTo(blob, indexStart);
        entitlements.CopyTo(blob, indexStart + codeDirectory.Length);
        return blob;
    }
}
