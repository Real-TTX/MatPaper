namespace MatPaper.Data;

/// <summary>
/// Per-kind endpoint data stored in <see cref="Connection.SettingsJson"/>. Each kind reads
/// its own shape with the tolerant <c>TaskSettingsJson</c> reader, so a new kind needs a new
/// DTO but no schema change. Auth (username, secret, tokens) is NOT here — it lives in
/// dedicated columns on <see cref="Connection"/>.
/// </summary>
public sealed class SmbEndpoint
{
    public string Host { get; set; } = string.Empty;

    /// <summary>The share name. One share is one connection; a NAS with three shares is three.</summary>
    public string Share { get; set; } = string.Empty;
}

/// <summary>Endpoint for an IMAP or POP3 mailbox.</summary>
public sealed class MailEndpoint
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool UseSsl { get; set; } = true;
}

/// <summary>Endpoint for a Google Drive connection. The root folder is chosen per storage location.</summary>
public sealed class GoogleDriveEndpoint
{
    /// <summary>Optional default Drive folder id; the actual root is set per storage location.</summary>
    public string? RootFolderId { get; set; }
}

/// <summary>Endpoint for a OneDrive connection reached over Microsoft Graph.</summary>
public sealed class OneDriveEndpoint
{
    public string? DriveId { get; set; }
    public string? RootItemId { get; set; }
}
