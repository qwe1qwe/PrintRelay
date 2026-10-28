using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PrintRelay
{
    /// <summary>
    /// LPD-Server (RFC 1179): empfängt Aufträge, erzeugt per Ghostscript ein PDF und druckt es.
    /// </summary>
    public sealed class LpdServer : BackgroundService
    {
        // Schutzgrenzen gegen kaputte oder böswillige Clients
        private const long MaxFileBytes = 512L * 1024 * 1024;   // max. Größe einer einzelnen Datei
        private const int MaxFilesPerJob = 64;                   // max. Anzahl Dateien pro Auftrag
        private const int MaxLineLength = 1024;                  // max. Länge einer Protokollzeile
        private const int SocketTimeoutMs = 60_000;              // Timeout für Lesen/Schreiben
        private const int GhostscriptTimeoutMs = 5 * 60_000;     // max. Laufzeit eines GS-Aufrufs

        private readonly ILogger<LpdServer> _log;
        private readonly PrintRelayOptions _opt;
        private readonly string _printJobsDir;
        private readonly HashSet<IPAddress> _allowedClients;

        // Gefundener Ghostscript-Pfad wird gemerkt, damit nicht bei jedem Auftrag PATH und Ordner durchsucht werden
        private string? _gsCache;

        public LpdServer(PrintRelayOptions options, ILogger<LpdServer> log)
        {
            _opt = options;
            _log = log;
            _printJobsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, options.PrintJobsDir));
            _allowedClients = ParseAllowedClients(options.AllowedIps);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Directory.CreateDirectory(_printJobsDir);

            var listener = new TcpListener(IPAddress.Any, _opt.Port);
            listener.Start();
            _log.LogInformation("Listening on port {Port}, jobs folder: {Dir}", _opt.Port, _printJobsDir);
            _log.LogInformation("Target printer: {Printer}",
                string.IsNullOrWhiteSpace(_opt.PrinterName) ? "(Windows default printer)" : _opt.PrinterName);
            if (_allowedClients.Count > 0)
                _log.LogInformation("Allowed clients: {Clients}", string.Join(", ", _allowedClients));

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    Socket client;
                    try
                    {
                        client = await listener.AcceptSocketAsync(stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (SocketException ex)
                    {
                        _log.LogWarning("Accept error: {Message}", ex.Message);
                        continue;
                    }

                    _log.LogInformation("Connection from {Remote}", client.RemoteEndPoint);

                    if (!IsClientAllowed(client))
                    {
                        _log.LogWarning("Rejected client {Remote} (not in AllowedIps).", client.RemoteEndPoint);
                        client.Close();
                        continue;
                    }

                    _ = Task.Run(() => HandleClient(client), CancellationToken.None);
                }
            }
            finally
            {
                listener.Stop();
                _log.LogInformation("Listener stopped.");
            }
        }

        private void HandleClient(Socket socket)
        {
            socket.ReceiveTimeout = SocketTimeoutMs;
            socket.SendTimeout = SocketTimeoutMs;

            using var ns = new NetworkStream(socket, ownsSocket: true);
            using var br = new BinaryReader(ns, Encoding.ASCII, leaveOpen: true);
            using var bw = new BinaryWriter(ns, Encoding.ASCII, leaveOpen: true);

            string jobDir = "";
            try
            {
                // Main command: 0x02 (receive a printer job) + <queue>\n
                int cmd = ns.ReadByte();
                if (cmd != 0x02)
                {
                    _log.LogWarning("Unsupported command: 0x{Cmd:X2}", cmd);
                    return;
                }

                string queue = ReadLineAscii(br);
                if (string.IsNullOrWhiteSpace(queue))
                {
                    _log.LogWarning("Empty queue name.");
                    return;
                }

                // ACK
                bw.Write((byte)0x00);
                bw.Flush();

                // Temp job folder (Zeitstempel + Zufallsanteil, damit parallele Jobs nicht kollidieren)
                string jobId = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N")[..8];
                jobDir = Path.Combine(_printJobsDir, San(queue), jobId);
                Directory.CreateDirectory(jobDir);

                string? controlFilePath = null;
                var dataFiles = new List<string>();
                bool jobOk = true;
                int fileCount = 0;

                // Subcommands: 0x01 abort, 0x02 control, 0x03 data
                while (true)
                {
                    int sub = ns.ReadByte();     // -1 => connection closed
                    if (sub < 0) break;

                    string line = ReadLineAscii(br); // "byteCount fileName" (bei 0x01 leer)

                    if (sub == 0x01)
                    {
                        // RFC 1179: "Abort job" – bisher Empfangenes verwerfen
                        _log.LogInformation("Job aborted by client.");
                        bw.Write((byte)0x00);
                        bw.Flush();
                        jobOk = false;
                        break;
                    }

                    int sp = line.IndexOf(' ');
                    if (sp < 0)
                    {
                        _log.LogWarning("Malformed subcmd header.");
                        bw.Write((byte)0x01); // NAK
                        bw.Flush();
                        jobOk = false;
                        break;
                    }

                    if (!long.TryParse(line.Substring(0, sp), out long count) || count < 0 || count > MaxFileBytes)
                    {
                        _log.LogWarning("Invalid or too large byte count: '{Count}'.", line.Substring(0, sp));
                        bw.Write((byte)0x01);
                        bw.Flush();
                        jobOk = false;
                        break;
                    }

                    if (++fileCount > MaxFilesPerJob)
                    {
                        _log.LogWarning("Too many files in job (max {Max}).", MaxFilesPerJob);
                        bw.Write((byte)0x01);
                        bw.Flush();
                        jobOk = false;
                        break;
                    }

                    string name = line.Substring(sp + 1);
                    string outPath = Path.Combine(jobDir, San(name));

                    // ACK header
                    bw.Write((byte)0x00);
                    bw.Flush();

                    // Read exactly 'count' bytes
                    using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                        CopyExact(ns, fs, count);

                    // Trailer 0x00
                    int trailer = ns.ReadByte();
                    if (trailer != 0x00)
                    {
                        _log.LogWarning("Missing/invalid trailer byte after file body.");
                        jobOk = false;
                        break;
                    }

                    // ACK file received
                    bw.Write((byte)0x00);
                    bw.Flush();

                    if (sub == 0x02) controlFilePath = outPath;
                    else if (sub == 0x03) dataFiles.Add(outPath);
                }

                _log.LogInformation("Received job in {JobDir} (control: {Control}, data files: {Count})",
                    jobDir, controlFilePath != null ? Path.GetFileName(controlFilePath) : "-", dataFiles.Count);

                if (!jobOk)
                {
                    _log.LogWarning("Job incomplete or aborted – nothing printed.");
                    if (!_opt.KeepRawFiles) TryDeleteDirectory(jobDir);
                    return;
                }

                // --- PS -> (ein) PDF im printjobs-Ordner + anschließender Druck ---
                try
                {
                    string finalPdf = BuildFinalPdf(jobDir, controlFilePath, dataFiles);
                    if (!string.IsNullOrEmpty(finalPdf) && File.Exists(finalPdf))
                    {
                        _log.LogInformation("Final PDF: {Pdf}", finalPdf);
                        string printer = PrintWithGhostscript(finalPdf);
                        _log.LogInformation("Printed on '{Printer}'.", printer);

                        if (!_opt.KeepRawFiles)
                            TryDeleteDirectory(jobDir);
                    }
                    else
                    {
                        _log.LogWarning("Kein finales PDF erzeugt.");
                    }
                }
                catch (Exception exPdf)
                {
                    _log.LogError("PDF render/print error: {Message}", exPdf.Message);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("Client error: {Message}", ex.Message);
            }
        }

        // ---------- PS -> ein PDF + finale Ablage ----------

        private string BuildFinalPdf(string jobDir, string? controlFile, List<string> dataFiles)
        {
            // Falls dataFiles leer: alle Nicht-Control-Dateien nehmen
            if (dataFiles == null || dataFiles.Count == 0)
            {
                dataFiles = Directory.EnumerateFiles(jobDir)
                                     .Where(p => !IsControlFileName(Path.GetFileName(p)))
                                     .OrderBy(p => p)
                                     .ToList();
            }
            if (dataFiles.Count == 0)
            {
                _log.LogWarning("Keine Daten-Dateien gefunden.");
                return "";
            }

            string jobName = TryGetJobNameFromControl(controlFile) ?? new DirectoryInfo(jobDir).Name;
            if (jobName.Length > 100) jobName = jobName[..100];
            string timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string finalName = $"{San(jobName)}_{timeStamp}.pdf";
            string finalPathInRoot = Path.Combine(_printJobsDir, finalName);

            string? gs = FindGhostscript();
            if (gs == null)
            {
                _log.LogError("Ghostscript nicht gefunden. Installieren mit 'winget install -e --id ArtifexSoftware.Ghostscript' " +
                              "oder PrintRelay:GhostscriptPath in appsettings.json setzen.");
                return "";
            }

            // Alle PS-Dateien in EINEM GS-Aufruf zu einem PDF bündeln
            var args = new List<string>
            {
                "-dSAFER", "-dBATCH", "-dNOPAUSE", "-dQUIET", "-sDEVICE=pdfwrite",
                $"-sOutputFile={finalPathInRoot}"
            };
            args.AddRange(dataFiles);

            RunTool(gs, args);

            if (!File.Exists(finalPathInRoot))
            {
                _log.LogWarning("Ghostscript hat kein PDF erzeugt.");
                return "";
            }

            return finalPathInRoot;
        }

        private static bool IsControlFileName(string? fileName)
            => !string.IsNullOrEmpty(fileName) && fileName.StartsWith("cf", StringComparison.OrdinalIgnoreCase);

        private static string? TryGetJobNameFromControl(string? controlPath)
        {
            if (string.IsNullOrEmpty(controlPath) || !File.Exists(controlPath)) return null;
            // RFC: "J" = job name
            foreach (var line in File.ReadLines(controlPath))
            {
                if (line.StartsWith("J") && line.Length > 1)
                {
                    var name = line.Substring(1).Trim();
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
            return null;
        }

        // ---------- Drucken via Ghostscript (ohne System.Drawing) ----------

        private string PrintWithGhostscript(string pdfPath)
        {
            string? gs = FindGhostscript();
            if (gs == null)
                throw new InvalidOperationException("Ghostscript nicht gefunden (mswinpr2).");

            string printer = string.IsNullOrWhiteSpace(_opt.PrinterName) ? GetDefaultPrinterName() : _opt.PrinterName.Trim();
            if (string.IsNullOrWhiteSpace(printer))
                throw new InvalidOperationException("Kein Drucker konfiguriert (PrintRelay:PrinterName) und kein Standarddrucker vorhanden.");

            // PDF → GDI-Drucker (ohne Dialog)
            var args = new List<string>
            {
                "-dSAFER", "-dBATCH", "-dNOPAUSE", "-dQUIET", "-dFitPage", "-sDEVICE=mswinpr2",
                $"-dNumRenderingThreads={Math.Max(1, Environment.ProcessorCount)}",   // Rastern auf mehreren Kernen
                $"-sOutputFile=%printer%{printer}",
                pdfPath
            };

            RunTool(gs, args);
            return printer;
        }

        // Default-Drucker via Win32 API ermitteln
        [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetDefaultPrinterW")]
        private static extern bool GetDefaultPrinter(StringBuilder? pszBuffer, ref int pcchBuffer);

        private static string GetDefaultPrinterName()
        {
            int needed = 0;
            // Erster Call: Größe ermitteln (liefert false + ERROR_INSUFFICIENT_BUFFER)
            GetDefaultPrinter(null, ref needed);
            if (needed <= 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Kein Windows-Standarddrucker für dieses Konto gefunden. Als Dienst bitte PrintRelay:PrinterName setzen.");

            var sb = new StringBuilder(needed);
            if (!GetDefaultPrinter(sb, ref needed))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetDefaultPrinter failed.");

            return sb.ToString();
        }

        // ---------- Ghostscript-Helfer ----------

        private string? FindGhostscript()
        {
            var cached = _gsCache;
            if (cached != null && File.Exists(cached)) return cached;
            return _gsCache = SearchGhostscript();
        }

        private string? SearchGhostscript()
        {
            // 1) Konfiguration bzw. ENV override
            if (!string.IsNullOrWhiteSpace(_opt.GhostscriptPath) && File.Exists(_opt.GhostscriptPath))
                return _opt.GhostscriptPath;
            var env = Environment.GetEnvironmentVariable("GS_PROG");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

            // 2) PATH
            var which = FindOnPath("gswin64c.exe");
            if (which != null) return which;
            which = FindOnPath("gswin32c.exe");
            if (which != null) return which;

            // 3) Standard-Installationspfade (neueste Version zuerst)
            string[] bases =
            {
                @"C:\Program Files\gs",
                @"C:\Program Files (x86)\gs"
            };

            foreach (var b in bases)
            {
                if (!Directory.Exists(b)) continue;
                foreach (var dir in Directory.EnumerateDirectories(b, "gs*").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var candidate = Path.Combine(dir, "bin", "gswin64c.exe");
                    if (File.Exists(candidate)) return candidate;
                    candidate = Path.Combine(dir, "bin", "gswin32c.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }

            return null;
        }

        private static string? FindOnPath(string exe)
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    var full = Path.Combine(dir.Trim(), exe);
                    if (File.Exists(full)) return full;
                }
                catch { /* ignore */ }
            }
            return null;
        }

        private static void RunTool(string exe, IEnumerable<string> args)
        {
            using var p = new Process();
            p.StartInfo.FileName = exe;
            foreach (var a in args)
                p.StartInfo.ArgumentList.Add(a);   // korrektes Quoting, keine Argument-Injection
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.Start();

            // stdout und stderr parallel lesen, sonst kann der Prozess bei vollem Puffer hängen
            Task<string> stdout = p.StandardOutput.ReadToEndAsync();
            Task<string> stderr = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(GhostscriptTimeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw new TimeoutException($"{Path.GetFileName(exe)} timed out after {GhostscriptTimeoutMs / 1000}s.");
            }
            p.WaitForExit();
            Task.WaitAll(stdout, stderr);

            if (p.ExitCode != 0)
            {
                string err = (stderr.Result + stdout.Result).Trim();
                if (err.Length > 2000) err = err[..2000];
                throw new Exception($"{Path.GetFileName(exe)} exited with {p.ExitCode}" +
                                    (err.Length > 0 ? $": {err}" : ""));
            }
        }

        // ---------- LPD Helfer ----------

        private static string ReadLineAscii(BinaryReader br)
        {
            using var ms = new MemoryStream();
            while (true)
            {
                int b = br.BaseStream.ReadByte();
                if (b < 0) break;
                if (b == 0x0A) break; // LF
                if (ms.Length >= MaxLineLength)
                    throw new IOException($"Protocol line longer than {MaxLineLength} bytes.");
                ms.WriteByte((byte)b);
            }
            return Encoding.ASCII.GetString(ms.ToArray());
        }

        private static void CopyExact(Stream src, Stream dst, long count)
        {
            byte[] buf = new byte[81920];
            long remaining = count;
            while (remaining > 0)
            {
                int n = src.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
                if (n <= 0) throw new IOException("Unexpected EOF while reading file body.");
                dst.Write(buf, 0, n);
                remaining -= n;
            }
        }

        // Macht aus beliebigem Text einen sicheren Datei-/Ordnernamen (kein "..", keine Pfadtrenner)
        private static string San(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            s = s.Trim().TrimEnd('.');   // Windows mag keine Namen mit Punkt/Leerzeichen am Ende
            if (s.Length == 0 || s.All(ch => ch == '.'))
                s = "_";
            return s;
        }

        private void TryDeleteDirectory(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Could not delete raw job folder '{Dir}': {Message}", dir, ex.Message);
            }
        }

        // ---------- Zugriffsbeschränkung ----------

        private HashSet<IPAddress> ParseAllowedClients(string? value)
        {
            var set = new HashSet<IPAddress>();
            if (string.IsNullOrWhiteSpace(value)) return set;
            foreach (var part in value.Split(',', ';', ' '))
            {
                if (IPAddress.TryParse(part.Trim(), out var ip))
                    set.Add(Normalize(ip));
                else if (!string.IsNullOrWhiteSpace(part))
                    _log.LogWarning("Ignoring invalid IP in AllowedIps: '{Part}'", part);
            }
            return set;
        }

        private bool IsClientAllowed(Socket client)
        {
            if (_allowedClients.Count == 0) return true;
            return client.RemoteEndPoint is IPEndPoint ep && _allowedClients.Contains(Normalize(ep.Address));
        }

        private static IPAddress Normalize(IPAddress ip)
            => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }
}
