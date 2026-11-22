using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace SpecTuner.Sidecar.Platform;

/// <summary>One architecture inside a Mach-O file, with what its code signature says.</summary>
public sealed record MachOSlice(string Arch, bool Signed, bool HardenedRuntime, bool AllowsDyldEnvironment, bool DisablesLibraryValidation);

/// <summary>
/// The few facts read from a Linux (ELF) or macOS (Mach-O, including universal "fat") program
/// header. Pure byte parsing, so it works the same on every host. Windows programs keep using
/// <see cref="Games.PeFile"/>.
/// </summary>
public sealed partial record ExecutableHeader(string Format, IReadOnlyList<string> Arches, IReadOnlyList<MachOSlice> Slices)
{
    public const string Elf = "elf";
    public const string MachO = "mach-o";
    public const string MachOUniversal = "mach-o-universal";
    public const string Pe = "pe";

    private const uint CsRuntime = 0x10000;
    private const string AllowDyld = "com.apple.security.cs.allow-dyld-environment-variables";
    private const string DisableLibraryValidation = "com.apple.security.cs.disable-library-validation";

    /// <summary>Null when the file is missing, unreadable or not a format we know.</summary>
    public static ExecutableHeader? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static ExecutableHeader? Read(Stream stream)
    {
        var head = ReadAt(stream, 0, 64);
        if (head.Length < 8)
            return null;
        if (head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F')
            return ReadElf(head);
        if (head[0] == (byte)'M' && head[1] == (byte)'Z')
            return new ExecutableHeader(Pe, [], []);

        var bigMagic = BinaryPrimitives.ReadUInt32BigEndian(head);
        if (bigMagic is 0xCAFEBABE or 0xCAFEBABF)
            return ReadFat(stream, head, bigMagic == 0xCAFEBABF);
        var littleMagic = BinaryPrimitives.ReadUInt32LittleEndian(head);
        if (littleMagic is 0xFEEDFACF or 0xFEEDFACE)
        {
            var slice = ReadMachOSlice(stream, 0);
            return slice is null ? null : new ExecutableHeader(MachO, [slice.Arch], [slice]);
        }
        return null;
    }

    private static ExecutableHeader? ReadElf(byte[] head)
    {
        if (head.Length < 20)
            return null;
        var littleEndian = head[5] == 1;
        var span = head.AsSpan(18, 2);
        var machine = littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(span) : BinaryPrimitives.ReadUInt16BigEndian(span);
        var arch = machine switch
        {
            0x3E => "x64",
            0x03 => "x86",
            0xB7 => "arm64",
            0x28 => "arm",
            _ => $"elf-machine-{machine}",
        };
        return new ExecutableHeader(Elf, [arch], []);
    }

    private static ExecutableHeader? ReadFat(Stream stream, byte[] head, bool is64)
    {
        var count = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
        // Java class files share the 0xCAFEBABE magic; their "count" is a version number in the 40s or more.
        if (count is 0 or > 16)
            return null;
        var entrySize = is64 ? 32 : 20;
        var table = ReadAt(stream, 8, (int)count * entrySize);
        if (table.Length < count * entrySize)
            return null;
        var slices = new List<MachOSlice>();
        for (var i = 0; i < count; i++)
        {
            var entry = table.AsSpan(i * entrySize);
            var offset = is64 ? (long)BinaryPrimitives.ReadUInt64BigEndian(entry[8..]) : BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            var slice = ReadMachOSlice(stream, offset);
            if (slice is not null)
                slices.Add(slice);
        }
        return slices.Count == 0 ? null : new ExecutableHeader(MachOUniversal, slices.Select(s => s.Arch).ToList(), slices);
    }

    private static MachOSlice? ReadMachOSlice(Stream stream, long sliceStart)
    {
        var header = ReadAt(stream, sliceStart, 32);
        if (header.Length < 28)
            return null;
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic is not (0xFEEDFACF or 0xFEEDFACE))
            return null;
        var is64 = magic == 0xFEEDFACF;
        var cpuType = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var arch = cpuType switch
        {
            0x01000007 => "x64",
            0x0100000C => "arm64",
            7 => "x86",
            12 => "arm",
            _ => $"mach-cpu-{cpuType:x}",
        };
        var commandCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        var commandsSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        var headerSize = is64 ? 32 : 28;
        if (commandsSize > 16 * 1024 * 1024)
            return new MachOSlice(arch, false, false, false, false);
        var commands = ReadAt(stream, sliceStart + headerSize, (int)commandsSize);

        var position = 0;
        for (var i = 0; i < commandCount && position + 8 <= commands.Length; i++)
        {
            var cmd = BinaryPrimitives.ReadUInt32LittleEndian(commands.AsSpan(position));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(commands.AsSpan(position + 4));
            if (size < 8)
                break;
            if (cmd == 0x1D && position + 16 <= commands.Length) // LC_CODE_SIGNATURE
            {
                var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(commands.AsSpan(position + 8));
                var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(commands.AsSpan(position + 12));
                return ReadSignature(stream, arch, sliceStart + dataOffset, (int)Math.Min(dataSize, 4 * 1024 * 1024));
            }
            position += size;
        }
        return new MachOSlice(arch, false, false, false, false);
    }

    /// <summary>Code signature super blob (big-endian): the CodeDirectory's flags and the XML entitlements.</summary>
    private static MachOSlice ReadSignature(Stream stream, string arch, long start, int size)
    {
        var blob = ReadAt(stream, start, size);
        if (blob.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(blob) != 0xFADE0CC0)
            return new MachOSlice(arch, true, false, false, false);
        var count = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(8));
        var hardened = false;
        var entitlements = "";
        for (var i = 0; i < count && 12 + i * 8 + 8 <= blob.Length; i++)
        {
            var type = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(12 + i * 8));
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(16 + i * 8));
            if (offset + 8 > blob.Length)
                continue;
            var magic = BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(offset));
            var length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(offset + 4)), (uint)(blob.Length - offset));
            if (type == 0 && magic == 0xFADE0C02 && offset + 16 <= blob.Length) // CodeDirectory
                hardened = (BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(offset + 12)) & CsRuntime) != 0;
            else if (type == 5 && magic == 0xFADE7171 && length > 8) // XML entitlements
                entitlements = Encoding.UTF8.GetString(blob, offset + 8, length - 8);
        }
        return new MachOSlice(arch, true, hardened, HasTrue(entitlements, AllowDyld), HasTrue(entitlements, DisableLibraryValidation));
    }

    private static bool HasTrue(string plist, string key) =>
        Regex.IsMatch(plist, "<key>\\s*" + Regex.Escape(key) + "\\s*</key>\\s*<true\\s*/>");

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        if (count <= 0 || offset < 0 || offset >= stream.Length)
            return [];
        stream.Position = offset;
        var buffer = new byte[(int)Math.Min(count, stream.Length - offset)];
        stream.ReadExactly(buffer);
        return buffer;
    }
}
