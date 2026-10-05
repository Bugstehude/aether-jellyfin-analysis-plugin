# Analyse 1.2: verbindlicher Feld- und Wirevertrag für die Clientadapter

Stand 04.10.2026. **Dieser Vertrag ist die aktuelle Implementierungsgrundlage für Aether und
AetherTV.** Die hier festgelegten Felder, Typen, F64-Wirebytes und Detailregeln sind abgestimmt
und mit beiden Clients geprüft. Frühere 1.2-Vorschläge haben bei Widersprüchen keinen Vorrang.

Aktualisierung 05.10.2026: Marc hat den ersten Einsatz mit Aether Desktop freigegeben.
Plugin 0.4.0.0 komponiert neue, frisch gemessene Master als `aether-visual/1.2.0`, Schema `2`.
Die Komponenten behalten ihre tatsächliche `1.2.0-draft`-Identität und Gruppenprovenienz.
Methoden- und Codecbezeichner mit `-draft` bleiben unverändert. Historische Dokumente werden
nicht umetikettiert. Der neue Reader darf 1.2/1.1/1.0 lesen, alte Reader bleiben auf 1.0↔1.1.
Live-Geräte-RAM, Startlatenz und TV-Abnahme folgen im Betrieb und gelten nicht als bestanden.

## Alte Signale und optionale Gruppen

Die bekannte Struktur mit `item`, tatsächlicher `algorithm`-Identität, `durationMs`, `sampling`,
`producer`, `frames` und optionalen `audioFrames` bleibt bestehen. Neue Gruppen sind additiv.
Clients lesen bekannte Signale bei fehlenden neuen Gruppen weiter. Unbekannte optionale Felder
werden ignoriert oder für den Raw-Roundtrip erhalten. Eine ungültige optionale Gruppe liefert
eine Diagnose und den bisherigen Rückfall. Sie darf keine erfundenen Nullmessungen erzeugen.

| Gruppe | Inhalt |
| --- | --- |
| `timebase` | Gemeinsamer Medienursprung in µs und belegte Bildzeitbasis |
| `audioAnalysis` | Zustand, Vollständigkeit, Spur, Methoden, Coverage und Fullreferenzen der neuen Audioanalyse |
| `packedDenseAudioFrames` | Begrenzte unabhängige neue Audiomessreihe als F64-Wirepayload |
| `denseAudioFrames` | Logische Objektform derselben Reihe für unterstützte Offlineeingänge |
| `audioOnsets` | Eigenständige Audioereignisse mit Kandidaten- und Bestätigungszeit |
| `legacyAudioAnalysis`, `legacyAudioProvenance` | Herkunft und Zeitgrenzen der separat berechneten Legacy-Audioreihe |
| `cutAnalysis`, `cutEvents` | Neue Schnittmethode, Coverage, unbestätigte Endkandidaten und bestätigte Ereignisse |
| `signalProvenance` | Gruppenbezogene Methode, Producerrevision, Operation und autoritativer Quellfingerprint |
| `representation` | Tatsächliches Detail, Ableitung und Reduktionsmethode |

Für eine vorhandene neue Messreihe gibt es genau eine Repräsentation, Packed oder Objekt.
Beide parallel sind ungültig. Der neue Pluginpfad erzeugt Packed. Die Reihe ist von den
Bildframes unabhängig und wird nach Medienzeit abgefragt. Ein neues Signal darf nicht allein
deshalb fehlen, weil gerade kein neues Videobild eintrifft.

`audioFrames[].rms/flux` behalten die bisherige normalisierte Bedeutung und stammen aus der
separaten Legacyberechnung. Sie werden nicht aus neuen Rohfeatures rekonstruiert.
**Die vollständige unabhängige Legacyreihe bleibt in allen drei Details wertgleich mit Full.**
An ein Bild angehängtes Legacy-Audio benötigt exakte gemessene Zeitübereinstimmung und darf
keine nominell aufgefüllte Tailzeile als Messung verwenden.

## F64-Wireformat

Kennung: `le-u32-i32-u16-flags-f64x5-v1-draft`. Jeder Record ist exakt **51 Bytes**, Little-endian.

| Offset | Typ | Bedeutung |
| ---: | --- | --- |
| 0 | UInt32 | Absolute `timestampMs` auf der Medienzeitachse |
| 4 | Int32 | `windowStartSample`, negative Werte für Randpadding zulässig |
| 8 | UInt16 | `validSamples`, tatsächlich gültige Samples im Fenster |
| 10 | UInt8 | Flags, Bit 0 bezeichnet vorhandene Bandmessung |
| 11 | Binary64 | `rmsLinear` |
| 19 | Binary64 | `spectralFluxLinear` |
| 27 | Binary64 | Bass-RMS |
| 35 | Binary64 | Mitten-RMS |
| 43 | Binary64 | Höhen-RMS |

Header: `encoding`, `strideBytes:51`, `pointCount`, `checksum` und `dataBase64`.
`checksum` ist `sha256:` plus kleingeschriebener SHA256 der rohen Recordbytes.
Kanonisches Base64, Länge genau `pointCount * 51`, höchstens 864.000 Records.
Unbekannte Headerfelder dürfen erhalten bleiben. Reservierte Flagbits sind null.
Ohne Bandmessung sind die drei Bandslots positive Nullbits und werden logisch als fehlend
behandelt. Eine bekannte stille Spur mit gemessenen Nullwerten ist dagegen verfügbar.

Amplituden sind endlich und nichtnegativ. Es gibt keine F32-Quantisierung und kein stilles
Clamping von Resampling-Overs. Zeitpunkte sind streng steigend und vor `durationMs`.
Der Decoder prüft bekannte Parameter, Fenster/Sampleanzahl, Zeitformel und Coverage zusätzlich
zum Byteformat. Eine gültige Checksumme ist kein Nachweis gültiger Messungen.

## Methoden, Spuren und fehlende Daten

Das aktuell zugelassene neue Audioprofil verwendet 22.050 Hz, FFTgröße 2048, symmetrisches Hann,
Hop 441 Samples und `window-center-v1`. Downmix ist `channel-arithmetic-mean-v1`.
RMS, Bänder und Flux sind die in `audioAnalysis.methods` ausdrücklich benannten linearen
Verfahren. Driveableitung ist `file-peak-v1`; Peaks stammen immer aus dem Fullmaster.
Nullpeak ergibt Drive `0`. Ereignisstärke und Schnittscore sind keine Confidence.
Methoden und Parameter stehen vollständig in den geprüften Fixtures und dürfen nicht durch
bloße Ähnlichkeit als ein anderes bekanntes Verfahren interpretiert werden.

`ffmpegStreamIndex` ist der globale FFmpeg-Index, kein Index innerhalb einer Audio-Unterliste.
`jellyfinStreamIndex` ist optional und wird nur nach separater Hostprüfung geschrieben.
Im neuen Audio-Teiljob ist eine vom Host vorgegebene Zuordnung zu bestätigen.
In `legacyAudioAnalysis.referenceTrack` kann der Jellyfin-Index fehlen, während der belegte
FFmpeg-Index erhalten bleibt. Falls der Jellyfin-Index vorhanden ist, muss er zur Hostzuordnung
passen. Fehlende Indizes werden nicht aus Ordinal, Sprache, Codec oder Defaultflag erfunden.
Eine Metadatenzuordnung belegt noch keine Auswahl der tatsächlich gehörten nativen Playerspur.

`audioAnalysis.state` unterscheidet `available`, `no-track`, `decode-error`, `unsupported` und
`not-analyzed`. `completeness` unterscheidet `complete`, `partial` und `absent`.
Coverage nennt tatsächlich untersuchte Bereiche. Lücken werden nicht interpoliert.
Ein vollständiger `no-track`-Nachweis hat Referenzspur null und keine Dense-, Onset-, Legacy-
oder FFTmessung. Ein Probe-/Decodefehler ist kein Beweis, dass die Datei kein Audio enthält.

Der aktuelle Pluginpublisher akzeptiert vollständige zusammenhängende verfügbare Audiomessung
mit belegter Zeit- und Legacy-Defaultspurequivalenz oder belegtes vollständiges `no-track`.
Weitere Zustände können vom Clientmodell dargestellt werden, sind aber noch kein zugelassener
neuer Pluginmaster. Producerrevision und tatsächlicher Quellfingerprint bleiben sichtbar.

## Zeit, Ereignisse und Details

Full-Bildzeiten stammen aus Quell-PTS. `ptsTimeBase` ist rational, PTS werden als dezimale
Tickstrings übertragen. `requestedTimestampMs` ist von der tatsächlichen Bildzeit zu unterscheiden.
`mediaOriginUs` gilt für die gemeinsame Zeitachse. Aus FPS oder Arrayindex rekonstruierte Zeiten
dürfen nicht als gemessene PTS ausgegeben werden.

`timebase.video.sourceTimeline` erhält `frameCount`, `firstTimestampMs`, `lastTimestampMs`,
`firstSourcePts`, `lastSourcePts` der validierten Fullquellbilder. Alle fünf Anker bleiben bei
Reduktion unverändert. Gemittelte Bildbuckets haben keinen Einzelbild-PTS. Fehlende PTS sind nur
bei belegter Aggregation mit gültigen Quellankern zulässig, nicht aufgrund eines Detailnamens allein.

Onsets und Schnitte behalten ID, Kandidatenzeit, Bestätigungszeit, Zeitklammer, Stärke/Score,
Methoden und Coverage. Ereignis- und Bestätigungszeit liegen vor der Mediendauer.
Eine Onsetklammer darf bei `durationMs` enden. Unbestätigte EOF-Schnittkandidaten stehen nur in
`pendingAtEof`, lösen kein Schnittsignal aus und begrenzen die Cutcoverage.
Cutcoverage wird gegen die ursprüngliche Quellreihe geprüft, nicht gegen reduzierte Bildzeiten.

| Detail | Neue Audioauswahl | Unverändert |
| --- | --- | --- |
| Full | Alle gemessenen 20-ms-Punkte | Legacyreihe, Fullpeaks, Ereignisse, Methoden, Coverage und Spur |
| Balanced | Ganze Records mit maximalem RMS je 50-ms-Bucket | Dieselben Fullreferenzen und dieselbe Legacyreihe |
| Compact | Ganze Records mit maximalem RMS je 100-ms-Bucket | Dieselben Fullreferenzen und dieselbe Legacyreihe |

Bei RMSgleichstand gewinnt der früheste Record. Buckets beginnen auf der absoluten Medienzeit.
Neue Audioauswahl läuft unabhängig vom Bildintervall. Keine Mittelung von Audiorecords oder
Verschiebung von Ereignissen auf Bucketanfänge. Neue Reduktionskennung:
`aether-reduction-2-draft`; Bildaggregation `bucket-mean-v1`.

## Budgets, Versionsauswahl und Freigabe

Plugin höchstens 50 MiB unkomprimiertes Gesamtdokument, bei kleinerer Konfiguration deren Grenze.
TV höchstens 32 MiB, standardmäßig Balanced. Alle Gruppen und Metadaten zählen mit.
Ein ausdrücklich gewähltes Detail wird nicht still herabgestuft. Überbudget führt zum
nachvollziehbaren Rückfall. Kein Trunkieren und keine verdeckte Änderung von Hop oder Limits.

Die tatsächliche Algorithmusidentität bleibt in Query, GET/HEAD und Cache erhalten.
Master- und Repräsentations-ETags sind getrennt. Exakte Routen führen keine Versionsfallbacks aus.
Nur explizit abgenommene Leserichtungen dürfen in die Capabilitymatrix aufgenommen werden.
Der [aktive Versionsvertrag](analysis-version-contract.md) gilt weiterhin unverändert.

Ein alter Client kann neue optionale Felder ignorieren. Ob er neue Messungen auswählen darf
und ob bekannte Signale semantisch nutzbar bleiben, ist separat nachzuweisen. Insbesondere
die korrigierte Bildzeitbasis macht den Feldvertrag allein noch nicht zu einer Freigabe
für `1.2→1.0/1.1`. Alte Analysen bleiben lesbar und werden nicht auf eine neue Version umetikettiert.

Teiljobs werden privat gestaged und nur gemeinsam nach erneuter Host-/ETagprüfung gespeichert.
Konkurrierende Uploads gewinnen. Abbruch und Fehler lassen die alte Analyse erhalten.
Alte Messungen liefern keine fehlenden Rohfeatures oder Quell-PTS durch Umbenennung.
Gruppen-Reuse und das Routineziel für methodisch partielle EOF-Cuts sind noch nicht freigegeben.

Referenzen: [F64-Goldens](reviews/artifacts/analysis-1.2-plugin-draft/packed-f64-golden.json),
[v2-Komponenten-Strukturschema](reviews/artifacts/analysis-1.2-plugin-draft/analysis-job-artifact-v2.schema.json),
[Packed-Strukturschema](reviews/artifacts/analysis-1.2-plugin-draft/packed-dense-audio-f64.schema.json),
[abgenommene Clientfixtures](reviews/2026-10-04-analysis-1.2-client-fixture-acceptance.md).
Die Offline-Strukturschemas ersetzen weder aktive Uploadvalidierung noch Kontext-/Wireprüfung.
