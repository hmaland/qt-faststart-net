using System.Buffers.Binary;
using System.Text;

namespace QTFastStart
{
    public class Processor
    {
        private const int CHUNK_SIZE = 1024*1024; // The original CHUNK_SIZE was 8192, but we see better performance on large files by increasing chunk size
        private readonly LogLevel _logLevel;

        public Processor() : this(LogLevel.Silent) { }
        public Processor(LogLevel logLevel)
        {
            _logLevel = logLevel;
        }

        /// <summary>
        /// Read an atom and return a tuple of (size, type) where size is the size 
        /// in bytes (including the 8 bytes already read) and type is a "fourcc" 
        /// like "ftyp" or "moov".
        /// </summary>
        /// <param name="datastream"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        private (uint, string) ReadAtom(BinaryReader dataStream)
        {
            byte[] sizeBytes = dataStream.ReadBytes(4);  // The data is stored as big endian
            uint size = BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt32(sizeBytes, 0)); // TODO: ToUInt32

            byte[] typeBytes = dataStream.ReadBytes(4);
            string type = Encoding.ASCII.GetString(typeBytes);

            return (size, type);
        }

        /// <summary>
        /// Read an Atom from datastream
        /// </summary>
        /// <param name="dataStream"></param>
        /// <returns></returns>
        private Atom ReadAtomEx(BinaryReader dataStream)
        {
            long position = dataStream.BaseStream.Position;
            (long size, string type) = ReadAtom(dataStream);

            if (size == 1)
            {
                var sizeBytes = dataStream.ReadBytes(8);
                size = BinaryPrimitives.ReverseEndianness(BitConverter.ToInt64(sizeBytes, 0));
            }

            return new Atom(type, position, size);
        }

        /// <summary>
        /// Return an index of top level atoms, their absolute byte-position in the
        /// file and their size in a list:
        ///
        /// index = [
        ///    ("ftyp", 0, 24),
        ///    ("moov", 25, 2658),
        ///    ("free", 2683, 8),
        ///    ...
        /// ]
        /// The tuple elements will be in the order that they appear in the file.
        /// </summary>
        /// <param name="dataStream"></param>
        /// <returns></returns>
        public IEnumerable<Atom> GetIndex(BinaryReader dataStream)
        {
            LogInfo("Getting index of top level atoms...");
            var index = ReadAtoms(dataStream).ToList();
            EnsureValidIndex(index);

            return index;
        }

        /// <summary>
        /// Read atoms until an error occurs
        /// </summary>
        /// <param name="datastream"></param>
        /// <returns></returns>
        private IEnumerable<Atom> ReadAtoms(BinaryReader dataStream)
        {
            while (true)
            {
                if (dataStream.BaseStream.Position == dataStream.BaseStream.Length)
                {
                    yield break;
                }

                Atom atom;
                try
                {
                    atom = ReadAtomEx(dataStream);
                    LogDebug($"{atom.Name}: {atom.Size}");
                }
                catch
                {
                    yield break;
                }

                yield return atom;

                if (atom.Size == 0)
                {
                    if (atom.Name == "mdat")
                    {
                        // Some files may end in mdat with no size set, which generally
                        // means to seek to the end of the file. We can just stop indexing
                        // as no more entries will be found!
                        yield break;
                    }
                    else
                    {
                        // Weird, but just continue to try to find more atoms
                        continue;
                    }
                }

                dataStream.BaseStream.Seek(atom.Position + atom.Size, SeekOrigin.Begin);
            }
        }

        /// <summary>
        /// Ensure the minimum viable atoms are present in the index.
        /// Raise MalformedFileError if not.
        /// </summary>
        /// <param name="index"></param>
        private void EnsureValidIndex(List<Atom> index)
        {
            var topLevelAtoms = index.Select(item => item.Name).ToList();

            foreach (var key in new[] { "moov", "mdat" })
            {
                if (!topLevelAtoms.Contains(key))
                {
                    var msg = $"{key} atom not found, is this a valid MOV/MP4 file?";
                    LogWarning(msg);
                    throw new MalformedFileException(msg);
                }

            }
        }

        //private IEnumerable<string> FindAtoms(long size, BinaryReader dataStream)
        //{
        //    Atom fakeParent = new Atom("fake", dataStream.BaseStream.Position - 8, size + 8);
        //    foreach (string atomName in FindAtomsEx(fakeParent, dataStream))
        //    {
        //        yield return atomName;
        //    }
        //}

        private IEnumerable<Atom> _find_atoms_ex(Atom parentAtom, BinaryReader dataStream)
        {
            long stop = parentAtom.Position + parentAtom.Size;

            while (dataStream.BaseStream.Position < stop)
            {
                Atom atom;
                try
                {
                    atom = ReadAtomEx(dataStream);
                }
                catch (Exception ex)
                {
                    string msg = "Error reading next atom!";
                    LogError(msg);
                    throw new MalformedFileException(msg, ex);
                }

                if (new List<string> { "trak", "mdia", "minf", "stbl" }.Contains(atom.Name))
                {
                    // Known ancestor atom of stco or co64, search within it!
                    foreach (var res in _find_atoms_ex(atom, dataStream))
                    {
                        yield return res;
                    }
                }
                else if (new List<string> { "stco", "co64" }.Contains(atom.Name))
                {
                    yield return atom;
                }
                else
                {
                    // Ignore this atom, seek to the end of it.
                    dataStream.BaseStream.Seek(atom.Position + atom.Size, SeekOrigin.Begin);
                }
            }
        }

        private bool MoovIsCompressed(BinaryReader dataStream, Atom moovAtom)
        {
            // Seek to the beginning of the moov atom contents
            dataStream.BaseStream.Seek(moovAtom.Position + 8, SeekOrigin.Begin);

            // Step through the moov atom children to see if a cmov atom is among them
            long stop = moovAtom.Position + moovAtom.Size;
            while (dataStream.BaseStream.Position < stop)
            {
                Atom childAtom = ReadAtomEx(dataStream);
                dataStream.BaseStream.Seek(dataStream.BaseStream.Position + childAtom.Size - 8, SeekOrigin.Begin);

                // cmov means compressed moov header!
                if (childAtom.Name == "cmov")
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Convert a Quicktime/MP4 file for streaming by moving the metadata to 
        /// the front of the file. This method writes a new file.
        /// 
        /// If limit is set to something other than zero it will be used as the
        /// number of bytes to write of the atoms following the moov atom.This
        /// is very useful to create a small sample of a file with full headers,
        /// which can then be used in bug reports and such.
        /// 
        /// If cleanup is set to False, free atoms and zero atoms will not be
        /// scrubbed from the mov
        /// </summary>
        /// <param name="infilename"></param>
        /// <param name="outfilename"></param>
        /// <param name="limit"></param>
        /// <param name="to_end"></param>
        /// <param name="cleanup"></param>
        /// <exception cref="FastStartSetupException"></exception>
        /// <exception cref="UnsupportedFormatException"></exception>
        public void Process(string infilename, string outfilename, long limit = long.MaxValue, bool to_end = false, bool cleanup = true)
        {
            using (FileStream fileStream = new FileStream(infilename, FileMode.Open, FileAccess.Read))
            {
                using (BinaryReader datastream = new BinaryReader(fileStream))
                {
                    // Get the top level atom index
                    List<Atom> index = GetIndex(datastream).ToList();

                    // long mdat_pos = long.MaxValue;
                    long mdat_pos = 999999;  // Why this number?

                    long mdat_pos_first = -1;
                    long mdat_pos_last = -1;
                    long free_size = 0;

                    // Get position of first and last mdat atom(s)
                    foreach (var atom in index)
                    {
                        if (atom.Name == "mdat")
                        {
                            if (atom.Position < mdat_pos_first || mdat_pos_first < 0)
                                mdat_pos_first = atom.Position;
                            if (atom.Position > mdat_pos_last || mdat_pos_last < 0)
                                mdat_pos_last = atom.Position;
                        }
                    }

                    if (mdat_pos_first < 0)
                    {
                        // No mdat atom found
                        var msg = "No mdat atom found.";
                        LogError(msg);
                        throw new FastStartSetupException(msg);
                    }

                    Atom moovAtom = null;
                    long moov_pos = 0;


                    // Make sure moov occurs AFTER mdat, otherwise no need to run!
                    foreach (Atom atom in index)
                    {
                        // The atoms are guaranteed to exist from get_index above!
                        if (atom.Name == "moov")
                        {
                            moovAtom = atom;
                            moov_pos = atom.Position;
                        }
                        else if (atom.Name == "mdat")
                        {
                            mdat_pos = atom.Position;
                        }
                        else if (atom.Name == "free" && atom.Position < mdat_pos && cleanup)
                        {
                            // This free atom is before the mdat!
                            free_size += atom.Size;
                            LogInfo($"Removing free atom at {atom.Position} ({atom.Size} bytes)");
                        }
                        else if (atom.Name == "\x00\x00\x00\x00" && atom.Position < mdat_pos)
                        {
                            // This is some strange zero atom with incorrect size
                            free_size += 8;
                            LogInfo($"Removing strange zero atom at {atom.Position} (8 bytes)");
                        }
                    }

                    // Offset to shift positions
                    long offset = -free_size;
                    if (moov_pos < mdat_pos && to_end)
                    {
                        // moov is in the wrong place, shift by moov size
                        offset -= moovAtom.Size;
                    }
                    else if (moov_pos > mdat_pos_first && !to_end)
                    {
                        // moov is in the wrong place, shift by moov size
                        offset += moovAtom.Size;
                    }
                    else if (offset == 0)
                    {
                        // No free atoms to process and moov is correct, we are done!
                        string msg = "This file appears to already be set up!";
                        LogError(msg);
                        throw new FastStartSetupException(msg);
                    }

                    // Check for compressed moov atom
                    bool isCompressed = MoovIsCompressed(datastream, moovAtom);
                    if (isCompressed)
                    {
                        string msg = "Movies with compressed headers are not supported";
                        LogError(msg);
                        throw new UnsupportedFormatException(msg);
                    }

                    // read and fix moov
                    var moov = PatchMoov(datastream, moovAtom, offset);

                    LogInfo("Writing output...");
                    using (var outfile = new FileStream(outfilename, FileMode.Create, FileAccess.Write))
                    {
                        // Write ftyp
                        foreach (Atom atom in index)
                        {
                            if (atom.Name == "ftyp")
                            {
                                LogDebug($"Writing ftyp... ({atom.Size} bytes)");
                                datastream.BaseStream.Seek(atom.Position, SeekOrigin.Begin);
                                var buffer = new byte[atom.Size];
                                datastream.Read(buffer, 0, (int)atom.Size);
                                outfile.Write(buffer, 0, (int)atom.Size);
                            }
                        }

                        if (!to_end)
                        {
                            WriteMoov(moov, outfile);
                        }

                        // Write the rest
                        var skipAtomTypes = new List<string> { "ftyp", "moov" };
                        if (cleanup)
                        {
                            skipAtomTypes.Add("free");
                        }

                        var atoms = index.Where(item => !skipAtomTypes.Contains(item.Name));
                        foreach (Atom atom in atoms)
                        {
                            LogDebug($"Writing {atom.Name}... ({atom.Size} bytes)");
                            datastream.BaseStream.Seek(atom.Position, SeekOrigin.Begin);

                            // for compatability, allow '0' to mean no limit
                            if (limit == 0)
                                limit = long.MaxValue;
                            long cur_limit = Math.Min(limit, atom.Size);
                            long remaining = cur_limit;

                            while (remaining > 0)
                            {
                                var chunkSize = (int)Math.Min(remaining, CHUNK_SIZE);
                                var buffer = new byte[chunkSize];
                                var bytesRead = datastream.Read(buffer, 0, chunkSize);
                                outfile.Write(buffer, 0, bytesRead);
                                remaining -= bytesRead;
                            }
                        }

                        if (to_end)
                        {
                            WriteMoov(moov, outfile);
                        }
                    }

                    try
                    {
                        File.SetAttributes(outfilename, File.GetAttributes(infilename));
                    }
                    catch
                    {
                        LogWarning("Could not copy file permissions!");
                    }
                }
            }
        }

        private void WriteMoov(MemoryStream memoryStream, FileStream outfile)
        {
            byte[] bytes = memoryStream.ToArray();
            LogDebug($"Writing moov... ({bytes.Length} bytes)");
            outfile.Write(bytes, 0, bytes.Length);
        }

        private MemoryStream PatchMoov(BinaryReader dataStream, Atom atom, long offset)
        {
            dataStream.BaseStream.Seek(atom.Position, SeekOrigin.Begin);
            var moovMs = new MemoryStream(dataStream.ReadBytes((int)atom.Size));
            var moov = new BinaryReader(moovMs);

            // Reload the atom from the fixed stream
            Atom reloadedAtom = ReadAtomEx(moov);

            var sizeDictionary = new Dictionary<string, int>
                {
                    { "stco", 4},
                    { "co64", 8 }
                };

            var moovChildAtoms_Enumerator = _find_atoms_ex(reloadedAtom, moov);
            foreach (Atom childAtom in moovChildAtoms_Enumerator)
            {
                // Get number of entries
                byte[] versionBytes = moov.ReadBytes(4);
                int version = BinaryPrimitives.ReverseEndianness(BitConverter.ToInt32(versionBytes, 0));
                byte[] entryCountBytes = moov.ReadBytes(4);
                int entryCount = BinaryPrimitives.ReverseEndianness(BitConverter.ToInt32(entryCountBytes, 0));

                LogInfo($"Patching {childAtom.Name} with {entryCount} entries");

                long entriesPos = moov.BaseStream.Position;

                // Read either 32-bit or 64-bit offsets
                var csize = sizeDictionary[childAtom.Name];

                // Read entries
                var entriesBytes = moov.ReadBytes(csize * entryCount);
                var entries = new List<long>();
                for (int i = 0; i < entryCount; i++)
                {
                    var entryBytes = entriesBytes.Skip(i * csize).Take(csize).ToArray();
                    if (childAtom.Name == "stco")
                        entries.Add(BinaryPrimitives.ReverseEndianness(BitConverter.ToUInt32(entryBytes, 0)));
                    else if (childAtom.Name == "co64")
                        entries.Add(BinaryPrimitives.ReverseEndianness(BitConverter.ToInt64(entryBytes, 0)));
                }

                // Patch and write entries
                var offsetEntries = entries.Select(entry => entry + offset).ToList();
                moov.BaseStream.Position = entriesPos;

                foreach (long entry in offsetEntries)
                {
                    if (childAtom.Name == "stco")
                    {
                        moov.BaseStream.Write(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness((uint)entry)), 0, 4);
                        // The previous line is equal to the following:
                        //var bytes = BitConverter.GetBytes((uint)entry);
                        //moov.BaseStream.WriteByte(bytes[3]);
                        //moov.BaseStream.WriteByte(bytes[2]);
                        //moov.BaseStream.WriteByte(bytes[1]);
                        //moov.BaseStream.WriteByte(bytes[0]);
                    }
                    else if (childAtom.Name == "co64")
                    {
                        moov.BaseStream.Write(BitConverter.GetBytes(BinaryPrimitives.ReverseEndianness(entry)), 0, 8);
                    }
                }
            }

            return moovMs;
        }

        #region Logging
        private void LogDebug(string message)
        {
            if (_logLevel >= LogLevel.Debug)
            {
                var foregroundColor = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine(message);
                Console.ForegroundColor = foregroundColor;
            }
        }

        private void LogInfo(string message)
        {
            if (_logLevel >= LogLevel.Info)
            {
                var foregroundColor = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(message);
                Console.ForegroundColor = foregroundColor;
            }
        }

        private void LogWarning(string message)
        {
            if (_logLevel >= LogLevel.Warning)
            {
                var foregroundColor = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(message);
                Console.ForegroundColor = foregroundColor;
            }
        }

        private void LogError(string message)
        {
            if (_logLevel >= LogLevel.Error)
            {
                var foregroundColor = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(message);
                Console.ForegroundColor = foregroundColor;
            }
        }
        #endregion

    }
}