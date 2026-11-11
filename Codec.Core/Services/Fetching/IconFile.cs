using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Codec.Services.Fetching
{
    /// <summary>
    /// One image inside an .ico file, with the directory fields it was listed under. Data is
    /// either a PNG stream or a headerless DIB, exactly as the icon resource stores it.
    /// </summary>
    internal sealed record IconFrame(byte Width, byte Height, byte ColorCount, ushort Planes, ushort BitCount, byte[] Data)
    {
        /// <summary>The frame's real pixel width, read from its own header rather than the directory byte.</summary>
        public int PixelWidth
        {
            get
            {
                if (IsPng && Data.Length >= 24)
                {
                    return BinaryPrimitives.ReadInt32BigEndian(Data.AsSpan(16, 4));
                }

                if (Data.Length >= 8)
                {
                    return Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(4, 4)));
                }

                return Width == 0 ? 256 : Width;
            }
        }

        public bool IsPng =>
            Data.Length >= 8 && Data[0] == 0x89 && Data[1] == 0x50 && Data[2] == 0x4E && Data[3] == 0x47;
    }

    /// <summary>Reads and writes the .ico container.</summary>
    internal static class IconFile
    {
        private const int HeaderSize = 6;
        private const int FileEntrySize = 16;

        public static byte[] Build(IReadOnlyList<IconFrame> frames)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)frames.Count);

            int offset = HeaderSize + FileEntrySize * frames.Count;
            foreach (IconFrame frame in frames)
            {
                writer.Write(frame.Width);
                writer.Write(frame.Height);
                writer.Write(frame.ColorCount);
                writer.Write((byte)0);
                writer.Write(frame.Planes);
                writer.Write(frame.BitCount);
                writer.Write((uint)frame.Data.Length);
                writer.Write((uint)offset);
                offset += frame.Data.Length;
            }

            foreach (IconFrame frame in frames)
            {
                writer.Write(frame.Data);
            }

            writer.Flush();
            return stream.ToArray();
        }

        public static IReadOnlyList<IconFrame> Parse(byte[] bytes)
        {
            var frames = new List<IconFrame>();
            if (bytes.Length < HeaderSize ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2)) != 0 ||
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)) != 1)
            {
                return frames;
            }

            int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
            for (int i = 0; i < count; i++)
            {
                int entry = HeaderSize + i * FileEntrySize;
                if (entry + FileEntrySize > bytes.Length)
                {
                    break;
                }

                int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry + 8, 4));
                int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(entry + 12, 4));
                if (size <= 0 || offset < 0 || (long)offset + size > bytes.Length)
                {
                    continue;
                }

                frames.Add(new IconFrame(
                    bytes[entry],
                    bytes[entry + 1],
                    bytes[entry + 2],
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entry + 4, 2)),
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entry + 6, 2)),
                    bytes.AsSpan(offset, size).ToArray()));
            }

            return frames;
        }

        public static int LargestWidth(IReadOnlyList<IconFrame> frames)
            => frames.Count == 0 ? 0 : frames.Max(frame => frame.PixelWidth);
    }

    /// <summary>
    /// Takes the icon Explorer shows for an exe straight out of its resources: the first icon
    /// group with every frame in it, at the sizes the file really has. Shell icon APIs scale up
    /// whatever they find, which hides a file that only carries a tiny icon.
    /// </summary>
    internal static class ExeIconReader
    {
        /// <summary>Smaller than this and the rail would only ever show a blurry upscale.</summary>
        public const int MinimumIconSize = 32;

        private const uint LoadLibraryAsDatafile = 0x2;
        private const uint LoadLibraryAsImageResource = 0x20;
        private static readonly IntPtr ResourceTypeIcon = 3;
        private static readonly IntPtr ResourceTypeGroupIcon = 14;
        private const int GroupHeaderSize = 6;
        private const int GroupEntrySize = 14;

        /// <summary>The first icon group as a complete .ico file, or null when the exe has none worth using.</summary>
        public static byte[]? ReadIcon(string executablePath)
        {
            IReadOnlyList<IconFrame> frames = ReadFirstGroup(executablePath);
            return IsLargeEnough(frames) ? IconFile.Build(frames) : null;
        }

        public static bool IsLargeEnough(IReadOnlyList<IconFrame> frames)
            => IconFile.LargestWidth(frames) >= MinimumIconSize;

        public static IReadOnlyList<IconFrame> ReadFirstGroup(string executablePath)
        {
            var frames = new List<IconFrame>();
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                return frames;
            }

            IntPtr module = LoadLibraryExW(executablePath, IntPtr.Zero, LoadLibraryAsDatafile | LoadLibraryAsImageResource);
            if (module == IntPtr.Zero)
            {
                return frames;
            }

            try
            {
                // The name pointer is only good inside the callback, so the group is read there.
                EnumResourceNameCallback callback = (handle, _, name, _) =>
                {
                    byte[]? group = ReadResource(handle, name, ResourceTypeGroupIcon);
                    if (group == null || group.Length < GroupHeaderSize)
                    {
                        return true;
                    }

                    int count = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(4, 2));
                    for (int i = 0; i < count; i++)
                    {
                        int entry = GroupHeaderSize + i * GroupEntrySize;
                        if (entry + GroupEntrySize > group.Length)
                        {
                            break;
                        }

                        ushort id = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(entry + 12, 2));
                        byte[]? data = ReadResource(handle, (IntPtr)id, ResourceTypeIcon);
                        if (data == null || data.Length < 40)
                        {
                            continue;
                        }

                        frames.Add(new IconFrame(
                            group[entry],
                            group[entry + 1],
                            group[entry + 2],
                            BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(entry + 4, 2)),
                            BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(entry + 6, 2)),
                            data));
                    }

                    // Only the first group: that is the one the file is known by.
                    return false;
                };

                EnumResourceNamesW(module, ResourceTypeGroupIcon, callback, IntPtr.Zero);
                GC.KeepAlive(callback);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Icons] Reading the icon of '{executablePath}' failed: {ex.Message}");
                frames.Clear();
            }
            finally
            {
                FreeLibrary(module);
            }

            return frames;
        }

        private static byte[]? ReadResource(IntPtr module, IntPtr name, IntPtr type)
        {
            IntPtr resource = FindResourceW(module, name, type);
            if (resource == IntPtr.Zero)
            {
                return null;
            }

            IntPtr loaded = LoadResource(module, resource);
            IntPtr pointer = loaded == IntPtr.Zero ? IntPtr.Zero : LockResource(loaded);
            int size = (int)SizeofResource(module, resource);
            if (pointer == IntPtr.Zero || size <= 0)
            {
                return null;
            }

            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            return bytes;
        }

        private delegate bool EnumResourceNameCallback(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string fileName, IntPtr file, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool EnumResourceNamesW(IntPtr module, IntPtr type, EnumResourceNameCallback callback, IntPtr parameter);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindResourceW(IntPtr module, IntPtr name, IntPtr type);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LockResource(IntPtr resourceData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    }
}
