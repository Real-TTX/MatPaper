using System.Text.RegularExpressions;
using SMBLibrary;
using SMBLibrary.Client;

namespace MatPaper.Services;

/// <summary>Connection details for an SMB/CIFS share.</summary>
public sealed record SmbConnection(string Host, string Share, string? Domain, string Username, string Password);

/// <summary>
/// Thin wrapper over SMBLibrary's SMB2 client for network-share imports without a
/// host mount: connect once, enumerate files under a path, read a file's bytes and
/// optionally delete it. Paths use share-relative form (forward or back slashes).
/// </summary>
public sealed class SmbSession : IDisposable
{
    private readonly SMB2Client _client;
    private readonly ISMBFileStore _store;
    private bool _disposed;

    private SmbSession(SMB2Client client, ISMBFileStore store)
    {
        _client = client;
        _store = store;
    }

    public static SmbSession Connect(SmbConnection c)
    {
        var client = new SMB2Client();
        if (!client.Connect(c.Host, SMBTransportType.DirectTCPTransport))
        {
            throw new IOException($"Cannot reach SMB host '{c.Host}'.");
        }

        var login = client.Login(c.Domain ?? string.Empty, c.Username, c.Password);
        if (login != NTStatus.STATUS_SUCCESS)
        {
            client.Disconnect();
            throw new IOException($"SMB login failed ({login}).");
        }

        var store = client.TreeConnect(c.Share, out var treeStatus);
        if (treeStatus != NTStatus.STATUS_SUCCESS || store is null)
        {
            client.Logoff();
            client.Disconnect();
            throw new IOException($"Cannot connect to share '{c.Share}' ({treeStatus}).");
        }

        return new SmbSession(client, store);
    }

    private static string Norm(string? path)
        => (path ?? string.Empty).Trim().Replace('/', '\\').Trim('\\');

    public IReadOnlyList<string> ListFiles(string startPath, string? pattern, bool recursive)
    {
        var results = new List<string>();
        Walk(Norm(startPath), pattern, recursive, results);
        return results;
    }

    private void Walk(string dir, string? pattern, bool recursive, List<string> results)
    {
        var status = _store.CreateFile(out var handle, out _, dir,
            AccessMask.GENERIC_READ, SMBLibrary.FileAttributes.Directory,
            ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DIRECTORY_FILE, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            return;
        }

        try
        {
            _store.QueryDirectory(out var list, handle, "*", FileInformationClass.FileDirectoryInformation);
            foreach (var info in list)
            {
                var entry = (FileDirectoryInformation)info;
                var name = entry.FileName;
                if (name is "." or "..")
                {
                    continue;
                }

                var full = string.IsNullOrEmpty(dir) ? name : dir + "\\" + name;
                var isDir = (entry.FileAttributes & SMBLibrary.FileAttributes.Directory) != 0;
                if (isDir)
                {
                    if (recursive)
                    {
                        Walk(full, pattern, recursive, results);
                    }
                }
                else if (MatchPattern(name, pattern))
                {
                    results.Add(full);
                }
            }
        }
        finally
        {
            _store.CloseFile(handle);
        }
    }

    private static bool MatchPattern(string name, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*")
        {
            return true;
        }

        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }

    public byte[] ReadAllBytes(string path)
    {
        var status = _store.CreateFile(out var handle, out _, Norm(path),
            AccessMask.GENERIC_READ, SMBLibrary.FileAttributes.Normal, ShareAccess.Read,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE, null);
        if (status != NTStatus.STATUS_SUCCESS)
        {
            throw new IOException($"Cannot open '{path}' ({status}).");
        }

        try
        {
            using var ms = new MemoryStream();
            long offset = 0;
            var chunk = (int)Math.Min(_client.MaxReadSize, 1024 * 1024);
            while (true)
            {
                status = _store.ReadFile(out var data, handle, offset, chunk);
                if (status == NTStatus.STATUS_END_OF_FILE)
                {
                    break;
                }
                if (status != NTStatus.STATUS_SUCCESS)
                {
                    throw new IOException($"Read error on '{path}' ({status}).");
                }
                if (data is null || data.Length == 0)
                {
                    break;
                }

                ms.Write(data, 0, data.Length);
                offset += data.Length;
            }

            return ms.ToArray();
        }
        finally
        {
            _store.CloseFile(handle);
        }
    }

    public void TryDelete(string path)
    {
        var status = _store.CreateFile(out var handle, out _, Norm(path),
            AccessMask.DELETE, SMBLibrary.FileAttributes.Normal, ShareAccess.None,
            CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_DELETE_ON_CLOSE, null);
        if (status == NTStatus.STATUS_SUCCESS)
        {
            _store.CloseFile(handle);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { _store.Disconnect(); } catch { /* best effort */ }
        try { _client.Logoff(); } catch { /* best effort */ }
        try { _client.Disconnect(); } catch { /* best effort */ }
    }
}
