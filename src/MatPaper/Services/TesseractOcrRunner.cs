using System.Diagnostics;
using System.Text;

namespace MatPaper.Services;

/// <summary>
/// Runs the installed <c>tesseract</c> CLI (languages deu+eng) against an image file.
/// Never throws: returns an empty string on any failure.
/// </summary>
public sealed class TesseractOcrRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private readonly ILogger<TesseractOcrRunner> _logger;

    public TesseractOcrRunner(ILogger<TesseractOcrRunner> logger)
    {
        _logger = logger;
    }

    public async Task<string> RunAsync(string imagePath, CancellationToken ct)
    {
        // tesseract writes to "<outBase>.txt"; give it a unique temp base.
        var outBase = Path.Combine(Path.GetTempPath(), $"matpaper-ocr-{Guid.NewGuid():N}");
        var outText = outBase + ".txt";

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "tesseract",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(imagePath);
            startInfo.ArgumentList.Add(outBase);
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add("deu+eng");

            using var process = new Process { StartInfo = startInfo };

            if (!process.Start())
            {
                _logger.LogWarning("Failed to start tesseract process for {ImagePath}", imagePath);
                return string.Empty;
            }

            // Drain stderr/stdout so the process never blocks on a full pipe.
            var stdErrTask = process.StandardError.ReadToEndAsync(ct);
            var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                _logger.LogWarning("tesseract timed out or was cancelled for {ImagePath}", imagePath);
                return string.Empty;
            }

            var stdErr = await stdErrTask;
            _ = await stdOutTask;

            if (process.ExitCode != 0)
            {
                _logger.LogWarning(
                    "tesseract exited with code {ExitCode} for {ImagePath}: {StdErr}",
                    process.ExitCode, imagePath, stdErr);
                return string.Empty;
            }

            if (!File.Exists(outText))
            {
                _logger.LogWarning("tesseract produced no output file for {ImagePath}", imagePath);
                return string.Empty;
            }

            return await File.ReadAllTextAsync(outText, Encoding.UTF8, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "tesseract OCR failed for {ImagePath}", imagePath);
            return string.Empty;
        }
        finally
        {
            TryDelete(outText);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }
}
