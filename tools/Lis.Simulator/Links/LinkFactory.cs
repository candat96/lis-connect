using System.IO.Ports;
using System.Net;
using System.Net.Sockets;

namespace Lis.Simulator.Links;

/// <summary>Mở đường nối theo tham số dòng lệnh: <c>--connect</c>, <c>--listen</c> hoặc <c>--com</c>.</summary>
public static class LinkFactory
{
    public static async Task<IMachineLink> OpenAsync(CommandLine args, Action<string> log, CancellationToken ct)
    {
        if (args.Get("connect") is { } endpoint)
        {
            var (host, port) = ParseEndpoint(endpoint);
            var client = new TcpClient();
            log($"Đang nối tới LIS {host}:{port} …");
            await client.ConnectAsync(host, port, ct);
            log("Đã nối.");
            return new StreamLink(client.GetStream(), client);
        }

        if (args.Get("listen") is { } portText)
        {
            if (!int.TryParse(portText, out var port) || port is < 1 or > 65535)
            {
                throw new ArgumentException($"Cổng không hợp lệ: {portText}");
            }

            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            log($"Đang chờ LIS nối vào cổng {port} …");
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                log($"LIS đã nối từ {client.Client.RemoteEndPoint}.");
                return new StreamLink(client.GetStream(), client);
            }
            finally
            {
                listener.Stop();
            }
        }

        if (args.Get("com") is { } portName)
        {
            var baud = args.GetInt("baud", 9600);
            var dataBits = args.GetInt("data-bits", 8);
            var parity = Enum.Parse<Parity>(args.Get("parity") ?? "None", ignoreCase: true);
            var stopBits = args.GetInt("stop-bits", 1) == 2 ? StopBits.Two : StopBits.One;
            var port = new SerialPort(portName, baud, parity, dataBits, stopBits);
            port.Open();
            log($"Đã mở {portName} {baud} bps, {dataBits} bit, {parity}, {(stopBits == StopBits.Two ? 2 : 1)} stop.");
            return new StreamLink(port.BaseStream, port);
        }

        throw new ArgumentException("Cần một trong: --connect host:port, --listen port, --com COMx --baud N");
    }

    private static (string Host, int Port) ParseEndpoint(string text)
    {
        var colon = text.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(text[(colon + 1)..], out var port) || port is < 1 or > 65535)
        {
            throw new ArgumentException($"Địa chỉ phải dạng host:port, nhận được: {text}");
        }

        return (text[..colon], port);
    }
}
