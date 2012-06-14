using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace lokad_iddd_sample
{
    /// <summary>
    /// Simple embedded append-only store that uses Riak.Bitcask model
    /// for keeping records
    /// </summary>
    public class FileAppendOnlyStore : IAppendOnlyStore
    {
        sealed class Record
        {
            public readonly byte[] Bytes;
            public readonly string Name;
            public readonly int Version;

            public Record(byte[] bytes, string name, int version)
            {
                Bytes = bytes;
                Name = name;
                Version = version;
            }
        }

        readonly DirectoryInfo _info;

        // used to synchronize access between threads within a process

        readonly ReaderWriterLockSlim _thread = new ReaderWriterLockSlim();
        // used to prevent writer access to store to other processes
        FileStream _lock;
        FileStream _currentWriter;

        // caches
        readonly ConcurrentDictionary<string, TapeRecord[]> _items = new ConcurrentDictionary<string, TapeRecord[]>();
        AppendRecord[] _all = new AppendRecord[0];

        public void Initialize()
        {
            if (!_info.Exists)
                _info.Create();
            // grab the ownership
            _lock = new FileStream(Path.Combine(_info.FullName, "lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                8,
                FileOptions.DeleteOnClose);

            LoadCaches();
        }

        void LoadCaches()
        {
            try
            {
                _thread.EnterWriteLock();
                foreach (var record in EnumerateHistory())
                {
                    AddToCaches(record.Name, record.Bytes, record.Version);
                }

            }
            finally
            {
                _thread.ExitWriteLock();
            }
        }

        IEnumerable<Record> EnumerateHistory()
        {
            // cleanup old pending files
            // load indexes
            // build and save missing indexes
            var datFiles = _info.EnumerateFiles("*.dat");

            foreach (var fileInfo in datFiles.OrderBy(fi => fi.Name))
            {
                // quick cleanup
                if (fileInfo.Length == 0)
                {
                    fileInfo.Delete();
                }

                using (var reader = fileInfo.OpenRead())
                using (var binary = new BinaryReader(reader, Encoding.UTF8))
                {
                    Record result;
                    while (TryReadRecord(binary, out result))
                    {
                        yield return result;
                    }
                }
            }
        }
        static bool TryReadRecord(BinaryReader binary, out Record result)
        {
            result = null;
            try
            {
                var version = binary.ReadInt32();
                var name = binary.ReadString();
                var len = binary.ReadInt32();
                var bytes = binary.ReadBytes(len);
                var sha = binary.ReadBytes(20); // SHA1. TODO: verify data
                if (sha.All(s => s == 0))
                    throw new InvalidOperationException("definitely failed");

                result = new Record(bytes, name, version);
                return true;
            }
            catch (EndOfStreamException)
            {
                // we are done
                return false;
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);
                // Auto-clean?
                return false;
            }
        }


        public void Dispose()
        {
            if (!_closed)
                Close();
        }

        public FileAppendOnlyStore(DirectoryInfo info)
        {
            _info = info;
        }

        public void Append(string key, byte[] buffer, int serverVersion = -1)
        {
            // should be locked
            try
            {
                _thread.EnterWriteLock();

                var list = _items.GetOrAdd(key, s => new TapeRecord[0]);
                if (serverVersion >= 0)
                {
                    if (list.Length != serverVersion)
                        throw new AppendOnlyStoreConcurrencyException(serverVersion, list.Length, key);
                }

                EnsureWriterExists(_all.Length);
                int commit = list.Length + 1;

                PersistInFile(key, buffer, commit);
                AddToCaches(key, buffer, commit);
            }
            catch
            {
                Close();
            }
            finally
            {
                _thread.ExitWriteLock();
            }
        }

        void PersistInFile(string key, byte[] buffer, int commit)
        {
            using (var sha1 = new SHA1Managed())
            {
                // version, ksz, vsz, key, value, sha1
                using (var memory = new MemoryStream())
                {
                    using (var crypto = new CryptoStream(memory, sha1, CryptoStreamMode.Write))
                    using (var binary = new BinaryWriter(crypto, Encoding.UTF8))
                    {
                        binary.Write(commit);
                        binary.Write(key);
                        binary.Write(buffer.Length);
                        binary.Write(buffer);
                    }
                    var bytes = memory.ToArray();

                    _currentWriter.Write(bytes, 0, bytes.Length);
                }
                _currentWriter.Write(sha1.Hash, 0, sha1.Hash.Length);
                // make sure that we persist
                // NB: this is not guaranteed to work on Linux
                _currentWriter.Flush(true);
            }
        }

        void EnsureWriterExists(long version)
        {
            if (_currentWriter != null) return;

            var fileName = string.Format("{0:00000000}-{1:yyyy-MM-dd-HHmmss}.dat", version, DateTime.UtcNow);
            _currentWriter = File.OpenWrite(Path.Combine(_info.FullName, fileName));
        }

        void AddToCaches(string key, byte[] buffer, int commit)
        {
            var record = new TapeRecord(commit, buffer);
            _all = ImmutableAdd(_all, new AppendRecord(key, buffer));
            _items.AddOrUpdate(key, s => new[] { record }, (s, records) => ImmutableAdd(records, record));
        }

        static T[] ImmutableAdd<T>(T[] source, T item)
        {
            var copy = new T[source.Length + 1];
            Array.Copy(source, copy, source.Length);
            copy[source.Length] = item;
            return copy;
        }

        public IEnumerable<TapeRecord> ReadRecords(string key, int afterVersion, int maxCount)
        {
            // no lock is needed.
            TapeRecord[] list;
            return _items.TryGetValue(key, out list) ? list : Enumerable.Empty<TapeRecord>();
        }

        public IEnumerable<AppendRecord> ReadRecords(int afterVersion, int maxCount)
        {
            // collection is immutable so we don't care about locks
            return _all.Skip((int)afterVersion).Take(maxCount);
        }

        bool _closed;

        public void Close()
        {
            using (_lock)
            using (_currentWriter)
            {
                _closed = true;
            }
        }
    }
}