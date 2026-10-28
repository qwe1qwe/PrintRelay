using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace PrintRelay
{
    public static class Program
    {
        public const string ServiceName = "PrintRelay";

        public static void Main(string[] args)
        {
            // ContentRoot = Ordner der exe, damit appsettings.json auch als Dienst gefunden wird
            // (Dienste starten sonst in C:\Windows\System32).
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory
            });

            // Läuft die exe als Windows-Dienst, übernimmt der Service Control Manager die Steuerung.
            // Als normales Konsolenprogramm hat das keine Auswirkung (Beenden mit Strg+C).
            builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);

            var options = builder.Configuration.GetSection("PrintRelay").Get<PrintRelayOptions>() ?? new PrintRelayOptions();
            builder.Services.AddSingleton(options);
            builder.Services.AddHostedService<LpdServer>();

            builder.Build().Run();
        }
    }

    public sealed class PrintRelayOptions
    {
        /// <summary>TCP-Port für LPD (Standard 515).</summary>
        public int Port { get; set; } = 515;

        /// <summary>Ablageordner für PDFs/Rohdaten. Relativ = relativ zum Ordner der exe.</summary>
        public string PrintJobsDir { get; set; } = "printjobs";

        /// <summary>Zieldrucker. Leer = Windows-Standarddrucker des ausführenden Kontos.</summary>
        public string? PrinterName { get; set; }

        /// <summary>Pfad zu gswin64c.exe. Leer = automatisch suchen.</summary>
        public string? GhostscriptPath { get; set; }

        /// <summary>Komma-getrennte Liste erlaubter Client-IPs. Leer = alle erlaubt.</summary>
        public string? AllowedIps { get; set; }

        /// <summary>Rohdateien nach erfolgreichem Druck behalten.</summary>
        public bool KeepRawFiles { get; set; }
    }
}
