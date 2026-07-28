using System.Text;
using Microsoft.Extensions.Logging;
using UniFlow.Common.Services;

namespace UniFlow.WorkListCleaner.Services;

public class CentralinkService
{
    private readonly ILogger<CentralinkService> _logger;

    public CentralinkService(ILogger<CentralinkService> logger)
    {
        _logger = logger;
    }

    public void SendLasFlag(List<Models.WorkListRecord> records, string ip, int port,
        string driver, string noSampleFlag, string naResultFlag, string instrumentId)
    {
        if (records.Count == 0) return;

        try
        {
            var client = new AstmConnection(ip, port);
            var datetime = DateTime.Now.ToString("yyyyMMddHHmmss");

            foreach (var r in records)
            {
                var sbMsg = new StringBuilder();
                sbMsg.Append(instrumentId).Append(';').Append(r.TestType);
                string clMsg;
                if (!string.IsNullOrEmpty(r.Cps))
                {
                    clMsg = $"\\{naResultFlag}\\" + sbMsg.Append(';').Append(r.DilutionFactor)
                        .Append(';').Append(r.Cps) + "\\NA Result\\";
                }
                else
                {
                    clMsg = $"\\{noSampleFlag}\\" + sbMsg + "\\No Sample\\";
                }

                string frame;
                if (driver == "AUTO")
                {
                    frame = $"H|\\^&||||||||||P|1|{datetime}{(char)0x0D}{(char)0x1D}"
                        + $"C|1|I|S004^{r.AccessionNum}{clMsg}{datetime}|S{(char)0x0D}{(char)0x1D}"
                        + $"L|1|N{(char)0x0D}{(char)0x1D}";
                }
                else
                {
                    clMsg = !string.IsNullOrEmpty(r.Cps)
                        ? $"{sbMsg}|{naResultFlag}|NA Result"
                        : $"{sbMsg}|{noSampleFlag}|No Sample";
                    frame = $"H|\\^&||||||||||P|1|{datetime}{(char)0x0D}{(char)0x1D}"
                        + $"M|1|WARN|{r.AccessionNum}|{datetime}|{clMsg}{(char)0x0D}{(char)0x1D}"
                        + $"L|1|N{(char)0x0D}{(char)0x1D}";
                }

                client.SendFrame(frame);
                _logger.LogInformation("Sent LAS flag for {Sample}", r.AccessionNum);
            }

            client.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Centralink send failed: {Message}", ex.Message);
        }
    }
}
