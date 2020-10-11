﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Pak.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using Serilog;

namespace CUE4Parse.UE4.Pak
{
    public partial class PakFileReader
    {

        private static readonly ILogger log = Log.ForContext<PakFileReader>();

        public readonly FArchive Ar;
        public bool IsConcurrent { get; set; } = false;
        public readonly string FileName; 
        public readonly FPakInfo Info;

        public string MountPoint { get; private set; }
        public int FileCount { get; private set; } = 0;
        public int EncryptedFileCount { get; private set; } = 0;
        
        public FAesKey? AesKey { get; set; }

        public bool IsEncrypted => Info.EncryptedIndex;

        public PakFileReader(FArchive Ar)
        {
            this.Ar = Ar;
            FileName = Ar.Name;
            Info = FPakInfo.ReadFPakInfo(Ar);
            if (Info.Version > EPakFileVersion.PakFile_Version_Latest)
            {
                log.Warning($"Pak file \"{FileName}\" has unsupported version {(int) Info.Version}");
            }
        }

        public PakFileReader(string filePath, UE4Version ver = UE4Version.VER_UE4_LATEST, EGame game = EGame.GAME_UE4_LATEST)
            : this(new FileInfo(filePath), ver, game) {}
        public PakFileReader(FileInfo file, UE4Version ver = UE4Version.VER_UE4_LATEST, EGame game = EGame.GAME_UE4_LATEST)
            : this(new FStreamArchive(file.Name, file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite), ver, game)) {}

        public Dictionary<string, FPakEntry> ReadIndex(
            ConcurrentDictionary<string, ConcurrentDictionary<string, GameFile>>? outFiles = null)
        {
            var watch = new Stopwatch();
            watch.Start();
            var result = ReadIndexUpdated(outFiles);
            var elapsed = watch.Elapsed;
            var sb = new StringBuilder($"Pak {FileName}: {FileCount} files");
            if (EncryptedFileCount != 0)
                sb.Append($" ({EncryptedFileCount} encrypted)");
            if (MountPoint.Contains("/"))
                sb.Append($", mount point: \"{MountPoint}\"");
            sb.Append($", version {(int) Info.Version} in {elapsed}");
            log.Information(sb.ToString());
            return result;
        }

        private Dictionary<string, FPakEntry> ReadIndexUpdated(ConcurrentDictionary<string, ConcurrentDictionary<string, GameFile>>? outFiles = null)
        {
            // Prepare primary index and decrypt if necessary
            Ar.Position = Info.IndexOffset;
            FArchive primaryIndex = new FByteArchive($"{FileName} - Primary Index", ReadAndDecrypt((int) Info.IndexSize));

            string mountPoint;
            try
            {
                mountPoint = primaryIndex.ReadFString();
            }
            catch (Exception e)
            {
                throw new InvalidAesKeyException($"Given aes key '{AesKey?.KeyString}'is not working with '{FileName}'", e);
            }
            
            ValidateMountPoint(ref mountPoint);
            MountPoint = mountPoint;

            FileCount = primaryIndex.Read<int>();
            EncryptedFileCount = 0;

            primaryIndex.Position += 8; // PathHashSeed

            if (!primaryIndex.ReadBoolean())
                throw new ParserException(primaryIndex, "No path hash index");

            primaryIndex.Position += 36; // PathHashIndexOffset (long) + PathHashIndexSize (long) + PathHashIndexHash (20 bytes)

            if (!primaryIndex.ReadBoolean())
                throw new ParserException(primaryIndex, "No directory index");

            var directoryIndexOffset = primaryIndex.Read<long>();
            var directoryIndexSize = primaryIndex.Read<long>();
            primaryIndex.Position += 20; // Directory Index hash

            var encodedPakEntriesSize = primaryIndex.Read<int>();
            var encodedPakEntries = primaryIndex.ReadBytes(encodedPakEntriesSize);

            if (primaryIndex.Read<int>() < 0)
                throw new ParserException("Corrupt pak PrimaryIndex detected");

            // Read FDirectoryIndex
            Ar.Position = directoryIndexOffset;
            var directoryIndex = new FByteArchive($"{FileName} - Directory Index", ReadAndDecrypt((int) directoryIndexSize));
            if (outFiles != null)
            {
                ReadDirectoryIndexInto(directoryIndex, encodedPakEntries, outFiles);
                return new Dictionary<string, FPakEntry>(); // TODO not sure whether that's good
            }
            return ReadDirectoryIndexToDict(directoryIndex, encodedPakEntries, FileCount);
        }

        private Dictionary<string, FPakEntry> ReadDirectoryIndexToDict(FArchive directoryIndex, byte[] encodedPakEntries, int fileCount)
        {
            var mountPoint = MountPoint;
            // Read FDirectoryIndex
            var directoryIndexLength = directoryIndex.Read<int>();
            var directoryIndexDict = new Dictionary<string, Dictionary<string, int>>(directoryIndexLength);
            for (int i = 0; i < directoryIndexLength; i++)
            {
                var dir = directoryIndex.ReadFString();
                var dirDictLength = directoryIndex.Read<int>();
                var dirDict = new Dictionary<string, int>(dirDictLength);
                for (int j = 0; j < dirDictLength; j++)
                {
                    dirDict[directoryIndex.ReadFString()] = directoryIndex.Read<int>();
                }

                directoryIndexDict[dir] = dirDict;
            }
            
            // Read EncodedPakEntries
            var files = new Dictionary<string, FPakEntry>(fileCount);
            unsafe
            {
                var ptr = (byte*) Unsafe.AsPointer(ref encodedPakEntries[0]);
                
                foreach (var entry in directoryIndexDict)
                {
                    var dirName = entry.Key!;
                    var dirContent = entry.Value!;
                    foreach (var innerEntry in dirContent)
                    {
                        var fileName = innerEntry.Key!;
                        var offset = innerEntry.Value!;

                        string path = mountPoint + dirName + fileName;
                        files[path] = new FPakEntry(this, path, ptr + offset);
                    }
                }
            }

            return files;
        }

        private void ReadDirectoryIndexInto(FArchive directoryIndex, byte[] encodedPakEntries, ConcurrentDictionary<string, ConcurrentDictionary<string, GameFile>> outFiles)
        {
            // Read FDirectoryIndex
            var mountPoint = MountPoint;
            unsafe
            {
                var directoryIndexLength = directoryIndex.Read<int>();
                var ptr = (byte*) Unsafe.AsPointer(ref encodedPakEntries[0]);
                for (int i = 0; i < directoryIndexLength; i++)
                {
                    var dir = mountPoint + directoryIndex.ReadFString();
                    var dirDictLength = directoryIndex.Read<int>();
                    var dirDict = outFiles.GetOrAdd(dir.ToLower(), () => new ConcurrentDictionary<string, GameFile>(Environment.ProcessorCount, dirDictLength));
                    for (int j = 0; j < dirDictLength; j++)
                    {
                        var name = directoryIndex.ReadFString();
                        var entry = new FPakEntry(this, dir + name, ptr + directoryIndex.Read<int>());
                        if (entry.IsEncrypted)
                            EncryptedFileCount++;
                        dirDict[name.ToLower()] = entry;
                    }
                }
            }
        }

        private byte[] ReadAndDecrypt(int length)
        {
            if (IsEncrypted)
            {
                if (AesKey != null)
                {
                    return Ar.ReadBytes(length).Decrypt(AesKey);
                }
                throw new InvalidAesKeyException("Reading an encrypted index requires a valid aes key");
            }

            return Ar.ReadBytes(length);
        }

        private void ValidateMountPoint(ref string mountPoint)
        {
            var badMountPoint = !mountPoint.StartsWith("../../..");
            mountPoint = mountPoint.SubstringAfter("../../..");
            if (mountPoint[0] != '/' || ( (mountPoint.Length > 1) && (mountPoint[1] == '.') ))
                badMountPoint = true;

            if (badMountPoint)
            {
                log.Warning($"Pak \"{FileName}\" has strange mount point \"{mountPoint}\", mounting to root");
                mountPoint = "/";
            }

            mountPoint = mountPoint.Substring(1);
        }

        public override string ToString() => FileName;
    }
}