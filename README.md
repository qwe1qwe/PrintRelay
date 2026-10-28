# PrintRelay

Kleiner LPD-Druckserver (RFC 1179) für Windows. PrintRelay nimmt Druckaufträge über das LPD/LPR-Protokoll (TCP-Port 515) entgegen, wandelt sie mit Ghostscript in ein PDF um und druckt dieses auf einem Drucker des Rechners aus, auf dem PrintRelay läuft. Läuft als Konsolenprogramm oder als Windows-Dienst.

Typischer Einsatz: Geräte, Systeme oder andere Rechner, die per LPR drucken, auf einen beliebigen lokal installierten Windows-Drucker umleiten – und nebenbei von jedem Auftrag ein PDF archivieren.

## Ablauf

1. Ein Client verbindet sich auf Port 515 und sendet einen Auftrag (Control- und Daten-Datei).
2. Die Rohdateien werden unter `printjobs/<queue>/<job-id>/` gespeichert.
3. Ghostscript fasst alle Daten-Dateien zu **einem** PDF zusammen: `printjobs/<jobname>_<zeitstempel>.pdf`.
4. Das PDF wird per Ghostscript (`mswinpr2`) ohne Dialog auf dem konfigurierten Drucker (bzw. dem Standarddrucker) ausgegeben.
5. Nach erfolgreichem Druck werden die Rohdateien gelöscht, das PDF bleibt erhalten.

## Voraussetzungen (Server)

- Windows
- [.NET 10 Runtime](https://dotnet.microsoft.com/download) – entfällt bei der Variante *selfcontained* (zum Bauen: .NET 10 SDK)
- [Ghostscript](https://ghostscript.com/releases/gsdnld.html), z. B. per
  ```
  winget install -e --id ArtifexSoftware.Ghostscript
  ```
- Ein lokal installierter Drucker – auf diesem kommt alles raus
- Port 515/TCP in der Windows-Firewall freigegeben, z. B. (als Administrator):
  ```
  netsh advfirewall firewall add rule name="PrintRelay LPD" dir=in action=allow protocol=TCP localport=515
  ```

## Download

Fertige Builds gibt es unter **Releases**:

- `PrintRelay-<version>-win-x64.zip` – benötigt die installierte .NET 10 Runtime
- `PrintRelay-<version>-win-x64-selfcontained.zip` – läuft ohne .NET-Installation (größer)

ZIP an einen festen Ort entpacken, z. B. `C:\PrintRelay`.

## Selbst bauen

```
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```

Im Ordner `publish` liegen danach `PrintRelay.exe` und `appsettings.json`. Den Ordner an einen festen Ort kopieren, z. B. `C:\PrintRelay`.

### Release erstellen (GitHub Actions)

Der Workflow `.github/workflows/release.yml` baut bei jedem Push und Pull Request auf `main`. Ein Release mit beiden ZIP-Dateien entsteht, sobald ein Versions-Tag gepusht wird:

```
git tag v1.0.0
git push origin v1.0.0
```

## Konfiguration

Alle Einstellungen stehen in der `appsettings.json` **neben der exe**:

```json
{
  "PrintRelay": {
    "Port": 515,
    "PrintJobsDir": "printjobs",
    "PrinterName": "",
    "GhostscriptPath": "",
    "AllowedIps": "",
    "KeepRawFiles": false
  }
}
```

| Einstellung | Bedeutung |
|---|---|
| `Port` | TCP-Port für LPD. Standard `515`. |
| `PrintJobsDir` | Ablage für PDFs und Rohdaten. Relative Pfade gelten relativ zum Ordner der exe. |
| `PrinterName` | Name des Zieldruckers, wie er in Windows angezeigt wird, z. B. `HP LaserJet M404`. Leer = Standarddrucker des ausführenden Kontos. **Beim Betrieb als Dienst setzen** (siehe unten). |
| `GhostscriptPath` | Vollständiger Pfad zu `gswin64c.exe`. Leer = automatisch suchen (Umgebungsvariable `GS_PROG`, PATH, `C:\Program Files\gs\...`). |
| `AllowedIps` | Komma-getrennte Liste von IP-Adressen, die drucken dürfen, z. B. `192.168.1.20,192.168.1.21`. Leer = alle Clients erlaubt. |
| `KeepRawFiles` | `true` = Rohdateien nach dem Druck behalten (zum Debuggen). |

Jede Einstellung lässt sich auch per Kommandozeile überschreiben, z. B. `PrintRelay.exe --PrintRelay:PrinterName="HP LaserJet M404"`.

Nach einer Änderung an der `appsettings.json` PrintRelay bzw. den Dienst neu starten.

## Starten als Konsolenprogramm

```
C:\PrintRelay\PrintRelay.exe
```

Beim Start erscheint `Listening on port 515 ...`. Beenden mit Strg+C. Zum Entwickeln geht auch `dotnet run`.

## Installieren als Windows-Dienst

Eingabeaufforderung **als Administrator** öffnen:

```bat
sc create PrintRelay binPath= "C:\PrintRelay\PrintRelay.exe" start= delayed-auto DisplayName= "PrintRelay LPD-Druckserver"
sc description PrintRelay "Nimmt LPD-Druckauftraege an, erzeugt PDFs und druckt sie aus."
sc failure PrintRelay reset= 86400 actions= restart/5000/restart/5000/restart/30000
sc start PrintRelay
```

Wichtig: Nach `binPath=`, `start=` usw. muss **ein Leerzeichen** stehen.

Weitere Befehle:

```bat
sc query PrintRelay      :: Status
sc stop PrintRelay       :: Stoppen
sc delete PrintRelay     :: Deinstallieren (vorher stoppen)
```

### Drucker beim Dienst

Ein Dienst läuft standardmäßig als **LocalSystem**. Dieses Konto hat einen eigenen (meist keinen) Standarddrucker und sieht nur Drucker, die **lokal auf dem Rechner** installiert sind, aber keine Netzwerkdrucker, die ein Benutzer für sich verbunden hat (z. B. `\\server\drucker`).

Daher:
- `PrinterName` in der `appsettings.json` auf den gewünschten Drucker setzen, und
- den Drucker lokal installieren (z. B. als TCP/IP-Drucker mit IP-Adresse), nicht als freigegebene Verbindung zu einem Druckserver.

Alternativ den Dienst unter einem Benutzerkonto laufen lassen, das den Drucker sieht:

```bat
sc config PrintRelay obj= ".\Benutzername" password= "Passwort"
```

(Das Konto braucht das Recht „Anmelden als Dienst“. Bei Einrichtung über `services.msc` → *Anmelden* wird es automatisch vergeben.)

### Logs

Als Dienst gibt es kein Konsolenfenster. Die Meldungen landen in der **Ereignisanzeige → Windows-Protokolle → Anwendung**, Quelle `PrintRelay`.

## Benutzung (Clients)

### Queue-Name

PrintRelay akzeptiert **jeden** Queue-Namen – alle Aufträge landen auf demselben Drucker. Der Name bestimmt nur den Unterordner in `printjobs/`. Empfohlen: **`lp`** (Standardname bei LPD).

### Druckformat

Die Daten müssen **PostScript** (oder PDF) sein, denn Ghostscript wandelt sie um. Auf dem Client also immer einen **PostScript-Druckertreiber** verwenden. Klartext, PCL oder herstellerspezifische Formate funktionieren nicht.

Im Folgenden ist `192.168.1.10` die IP des Rechners, auf dem PrintRelay läuft.

### Windows-Client

1. *Einstellungen → Bluetooth und Geräte → Drucker und Scanner → Gerät hinzufügen → „Der gewünschte Drucker ist nicht aufgeführt“*
2. *„Drucker unter Verwendung einer TCP/IP-Adresse oder eines Hostnamens hinzufügen“*
   - Gerätetyp: **TCP/IP-Gerät**
   - Hostname oder IP-Adresse: `192.168.1.10`
   - Haken bei „Den Drucker abfragen …“ **entfernen**
3. Falls nach zusätzlichen Portinformationen gefragt wird: **Benutzerdefiniert → Einstellungen**
   - Protokoll: **LPR**
   - Warteschlangenname: `lp`
   - **„LPR-Bytezählung aktiviert“ anhaken** (wichtig, sonst lehnt PrintRelay den Auftrag ab)
4. Treiber: **Microsoft → „Microsoft PS Class Driver“** (oder ein anderer PostScript-Treiber, z. B. HP Universal Printing PS).
5. Name vergeben, fertig – Testseite drucken.

Den Port kann man auch nachträglich unter *Druckereigenschaften → Anschlüsse → Konfigurieren* auf LPR umstellen.

Alternativ per PowerShell (als Administrator):

```powershell
Add-PrinterPort -Name "PrintRelay" -LprHostAddress "192.168.1.10" -LprQueueName "lp" -LprByteCounting
Add-PrinterDriver -Name "Microsoft PS Class Driver"
Add-Printer -Name "PrintRelay" -DriverName "Microsoft PS Class Driver" -PortName "PrintRelay"
```

### Linux-Client (CUPS)

Drucker anlegen (generischer PostScript-Treiber):

```bash
sudo lpadmin -p PrintRelay -E -v lpd://192.168.1.10/lp -m drv:///sample.drv/generic.ppd
```

Drucken:

```bash
lp -d PrintRelay dokument.pdf
```

CUPS wandelt PDF, Bilder, Text usw. automatisch in PostScript um. Den Drucker gibt es danach auch im Druckdialog aller Programme. Grafisch geht es ebenso über `http://localhost:631` → *Administration → Drucker hinzufügen → LPD/LPR-Host oder -Drucker* mit der Adresse `lpd://192.168.1.10/lp`.

Eine fertige PostScript-Datei unverändert (ohne Umwandlung durch CUPS) schicken:

```bash
lp -d PrintRelay -o raw dokument.ps
```

## Fehlersuche

- **Nichts kommt an:** Firewall-Regel für Port 515 prüfen; läuft ein anderer LPD-Dienst (z. B. Windows-Feature „LPD-Druckdienst“) auf demselben Port? Startet der Dienst nicht, steht der Grund in der Ereignisanzeige.
- **„Kein Windows-Standarddrucker für dieses Konto“ (als Dienst):** `PrinterName` setzen, siehe *Drucker beim Dienst*.
- **„Invalid or too large byte count“:** Auf dem Windows-Client die LPR-Bytezählung aktivieren.
- **„Ghostscript nicht gefunden“:** Ghostscript installieren oder `GhostscriptPath` setzen.
- **PDF ist leer/fehlerhaft:** Client verwendet keinen PostScript-Treiber. Mit `"KeepRawFiles": true` bleiben die empfangenen Rohdateien zur Analyse erhalten.

## Sicherheitshinweise

- PrintRelay hat **keine Authentifizierung**. Betreibe es nur in einem vertrauenswürdigen Netz und schränke den Zugriff per `AllowedIps` und/oder Windows-Firewall ein.
- Ghostscript verarbeitet PostScript aus dem Netz. Es wird mit `-dSAFER` aufgerufen, sollte aber trotzdem immer aktuell gehalten werden.
- Eingebaute Grenzen: max. 512 MB pro Datei, max. 64 Dateien pro Auftrag, 60 s Socket-Timeout, 5 min Laufzeit pro Ghostscript-Aufruf.

## Lizenz

Public Domain ([The Unlicense](LICENSE)) – jeder darf damit machen, was er will.
