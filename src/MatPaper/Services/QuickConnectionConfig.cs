namespace MatPaper.Services;

/// <summary>
/// Which connection selects on a page get a "New connection ..." entry (see _QuickConnection.cshtml):
/// the posted field names of the selects for NAS shares, mailboxes and cloud drives.
/// </summary>
public sealed record QuickConnectionConfig(string[] Smb, string[] Mail, string[] Cloud)
{
    public static QuickConnectionConfig None { get; } = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
}
