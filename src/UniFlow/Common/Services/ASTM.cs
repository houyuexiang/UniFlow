using System.Net.Sockets;
using System.Text;

namespace UniFlow.Common.Services;

public static class AstmChars
{
    public const char ENQ = (char)0x05;
    public const char ACK = (char)0x06;
    public const char NAK = (char)0x15;
    public const char STX = (char)0x02;
    public const char ETX = (char)0x03;
    public const char EOT = (char)0x04;
    public const char ETB = (char)0x17;
    public const char CR = (char)0x0D;
    public const char LF = (char)0x0A;
    public const char FS = (char)0x1C;
    public const char GS = (char)0x1D;
    public const char RS = (char)0x1E;
    public const char US = (char)0x1F;

    public static readonly string CRLF = $"{CR}{LF}";
}

public class AstmMessage
{
    public List<AstmRecord> Records { get; } = new();

    public string Build()
    {
        var sb = new StringBuilder();
        foreach (var r in Records)
            sb.Append(r.Build()).Append(AstmChars.CRLF);
        sb.Append('L').Append('|').Append('1').Append('|').Append('N').Append(AstmChars.CRLF);
        return sb.ToString();
    }

    public static AstmMessage Parse(string text)
    {
        var msg = new AstmMessage();
        var lines = text.Split(new[] { AstmChars.CRLF }, StringSplitOptions.None);
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line)) continue;
            var rec = AstmRecord.Parse(line);
            if (rec != null) msg.Records.Add(rec);
        }
        return msg;
    }

    public string ToAstmFrame()
    {
        var content = Build();
        var data = Encoding.ASCII.GetBytes(content);
        var checksum = CalcChecksum(data);
        return $"{AstmChars.STX}{content}{AstmChars.ETX}{checksum:X2}{AstmChars.CR}{AstmChars.LF}";
    }

    public static byte CalcChecksum(byte[] data)
    {
        int sum = 0;
        foreach (var b in data) sum += b;
        return (byte)(sum % 256);
    }
}

public class AstmRecord
{
    public char RecordType { get; set; }
    public List<string> Fields { get; } = new();
    public List<List<string>> Components { get; } = new();

    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append(RecordType).Append('|');
        sb.Append(string.Join('|', Fields));
        return sb.ToString();
    }

    public static AstmRecord? Parse(string line)
    {
        if (string.IsNullOrEmpty(line) || line.Length < 1) return null;
        var rec = new AstmRecord { RecordType = line[0] };
        var parts = line.Split('|');
        for (int i = 1; i < parts.Length; i++)
        {
            rec.Fields.Add(parts[i]);
            rec.Components.Add(parts[i].Split('^').ToList());
        }
        return rec;
    }

    public string Field(int index) =>
        index < Fields.Count ? Fields[index] : "";

    public string Component(int fieldIndex, int compIndex)
    {
        if (fieldIndex >= Components.Count) return "";
        var comps = Components[fieldIndex];
        return compIndex < comps.Count ? comps[compIndex] : "";
    }
}

public class AstmConnection : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _receiveThread;
    private readonly Queue<string> _receivedFrames = new();
    private readonly object _lock = new();

    public string Host { get; }
    public int Port { get; }

    public AstmConnection(string host, int port)
    {
        Host = host;
        Port = port;
        _tcp = new TcpClient();
        _tcp.Connect(host, port);
        _stream = _tcp.GetStream();
        _receiveThread = new Thread(ReceiveLoop) { IsBackground = true };
        _receiveThread.Start();
    }

    private void ReceiveLoop()
    {
        var ct = _cts.Token;
        var buffer = new byte[4096];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var len = _stream.Read(buffer, 0, buffer.Length);
                if (len <= 0) break;
                var data = Encoding.ASCII.GetString(buffer, 0, len);
                lock (_lock) _receivedFrames.Enqueue(data);
            }
            catch { break; }
        }
    }

    public string? ReadFrame(int timeoutMs = 5000)
    {
        var start = Environment.TickCount;
        while (Environment.TickCount - start < timeoutMs)
        {
            lock (_lock)
            {
                if (_receivedFrames.Count > 0)
                    return _receivedFrames.Dequeue();
            }
            Thread.Sleep(50);
        }
        return null;
    }

    public void SendFrame(string frame)
    {
        var data = Encoding.ASCII.GetBytes(frame);
        _stream.Write(data, 0, data.Length);
    }

    public void SendChar(char c)
    {
        _stream.WriteByte((byte)c);
    }

    public static string MakeOrderMessage(string sampleId, string testCode, string sampleType = "S")
    {
        var now = DateTime.Now.ToString("yyyyMMddHHmmss");
        var msg = new AstmMessage();
        msg.Records.Add(new AstmRecord
        {
            RecordType = 'H',
            Fields = { @"\^&", "", "", "", "", "", "", "", "", "", "", "P", "1", now }
        });
        msg.Records.Add(new AstmRecord
        {
            RecordType = 'P',
            Fields = { "1", sampleId, "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "F" }
        });
        msg.Records.Add(new AstmRecord
        {
            RecordType = 'O',
            Fields = { "1", sampleId, "", $"^^^{testCode}", "R", "", now, "", "", "", "", "", "", "", "", "", sampleType, "", "U" }
        });
        var content = msg.Build();
        var data = Encoding.ASCII.GetBytes(content);
        var cs = AstmMessage.CalcChecksum(data);
        return $"{AstmChars.STX}{content}{AstmChars.ETX}{cs:X2}{AstmChars.CRLF}";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _stream.Dispose();
        _tcp.Dispose();
    }
}
