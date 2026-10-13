using System.Net.WebSockets;
using System.Text.Json;
using ClashTray.Contracts;

namespace ClashTray.Core;

/// <summary>
/// Reads one bounded structured-log WebSocket stream and appends parsed
/// entries. Oversized or non-text messages are dropped so a noisy core cannot
/// exhaust memory.
/// </summary>
internal static class ControllerLogStreamReceiver
{
    private const int MaxLogMessageBytes = EndpointTransportPolicy.MaxWebSocketMessageBytes;

    public static async Task ReceiveLogMessagesAsync(
        ClientWebSocket socket,
        string logSource,
        Action<LogEntry> append,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentException.ThrowIfNullOrWhiteSpace(logSource);
        ArgumentNullException.ThrowIfNull(append);
        byte[] receiveBuffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using MemoryStream message = new MemoryStream();
            bool isText = true;
            bool isOversized = false;
            while (true)
            {
                WebSocketReceiveResult received = await socket.ReceiveAsync(
                    new ArraySegment<byte>(receiveBuffer),
                    cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                isText &= received.MessageType == WebSocketMessageType.Text;
                if (isText && !isOversized)
                {
                    if (message.Length > MaxLogMessageBytes - received.Count)
                    {
                        isOversized = true;
                    }
                    else
                    {
                        await message.WriteAsync(receiveBuffer.AsMemory(0, received.Count), cancellationToken);
                    }
                }

                if (received.EndOfMessage)
                {
                    break;
                }
            }

            if (!isText || isOversized || message.Length == 0)
            {
                continue;
            }

            message.Position = 0;
            try
            {
                using JsonDocument document = await JsonDocument.ParseAsync(message, cancellationToken: cancellationToken);
                foreach (LogEntry entry in MihomoDataParser.ParseLogs(document, logSource))
                {
                    append(entry);
                }
            }
            catch (JsonException)
            {
            }
        }
    }
}
