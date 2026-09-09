using System.Threading.Channels;

namespace MatPaper.Services;

/// <summary>
/// In-process, unbounded work queue of document ids awaiting OCR / thumbnail
/// processing. Registered as a singleton; producers call <see cref="Enqueue"/>
/// and the background service consumes <see cref="Reader"/>.
/// </summary>
public sealed class DocumentProcessingQueue
{
    private readonly Channel<long> _channel =
        Channel.CreateUnbounded<long>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

    public void Enqueue(long documentId) => _channel.Writer.TryWrite(documentId);

    public ChannelReader<long> Reader => _channel.Reader;
}
