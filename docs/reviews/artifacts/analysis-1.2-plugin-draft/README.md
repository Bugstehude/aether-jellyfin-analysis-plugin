# Offlinebeispiele zum Pluginentwurf

Die fünf Dokumentbeispiele sind synthetische Strukturbeispiele, keine Medienmessungen und keine aktiven Uploadfixtures.
Identität `1.2.0-draft`. Die neue Gruppe wird im produktiven Pluginpfad noch nicht validiert oder vollständig erhalten.
Die zusätzlichen Wiregoldens enthalten auch tatsächlich gemessene Audiopunkte und werden durch den isolierten Entwurfscodec geprüft.
Eine erfolgreiche Wire-/JSON-Prüfung ist keine Freigabe der DSP-/PTSqualität oder Lesematrix.

Ausgangspunkt ist das lokale Aether-Beispiel `analysis-1.2-draft/available.json` vom 2026-10-03.
Anpassungen: eigenständige `denseAudioFrames`, separate grobe Legacy-Audiospur, `completeness`,
`driveProjection` und `representation.denseAudio*` gemäß [Pluginentwurf](../../2026-10-03-analysis-1.2-contract-draft.md).
Die Werte sind illustrativ und müssen vor algorithmischer Abnahme durch echte Workerfixtures ersetzt werden.

| Datei | Zustand |
| --- | --- |
| `available.json` | Neue Audiomessung mit Onset, daneben unveränderte Legacyfeldstruktur |
| `silence.json` | Bekannte Spur, gemessene Nullwerte, keine Onsets |
| `no-track.json` | Abgeschlossene Spurprüfung ohne Audio, keine synthetischen Audiowerte |
| `partial-audio.json` | Decodefehler mit zwei gültigen Bereichen, getrennte FFTfenster und Fluxreset nach Lücke |
| `cuts-with-eof.json` | Bestätigter Schnitt und unbestätigter Endkandidat, partielle Ereignis-Coverage |

Die Audiobeispiele deklarieren die neue Schnittgruppe als nicht analysiert. Das Schnittbeispiel illustriert die Ereignisstruktur und partielle Abdeckung. Schnittqualität und EOF-Verhalten werden damit nicht nachgewiesen.
Mastermetadaten und Nullfingerprint sind Platzhalter. Nicht an den produktiven Server senden.

`python3 docs/reviews/artifacts/analysis-1.2-plugin-draft/payload-size-probe.py` schätzt die Rohbytes
der neuen Reihe allein durch Fortschreibung der illustrativen Punkte. Sie misst weder echte
Featurewerte noch Worker-/Client-RSS. Video, Legacy-Audio, Ereignisse und Header fehlen.
90 Minuten ergeben rund 44,10 MiB, zwei Stunden rund 58,90 MiB. Dies ist ein konkreter
Abstimmungspunkt vor Freigabe gegen das heutige 50-MiB-Limit.

`packed-f64-probe.py --artifacts <Aether-Messartefaktverzeichnis>` prüft die 100 realen
Zweisekunden-Audiopunkte als 51-Byte-F64-Records und berechnet die Base64-Größendifferenz
gegenüber den sechs vorhandenen F32-Messrecords. Ergebnis in `packed-f64-probe-result.json`.
Dies ist ein Offline-Formatprobe, keine vollständige Worker-/Decoder-/RSSabnahme. Einzelheiten
im [Rückmeldungsreview](../../2026-10-04-analysis-1.2-aether-response-review.md).

`packed-f64-golden.json` enthält gemeinsame Wirefälle mit Full-/Balanced-/Compactbytes:
100 gemessene Zweisekundenpunkte, einen synthetischen Binary64-Nachbarwert-/Gleichstandsfall
ohne Bänder und eine leere Payload. Python hat die Bytes unabhängig vom C#-Codec erzeugt.
Keine aktive 1.2-Uploadfixture. Umsetzung und Prüfergebnisse im
[konsolidierten Entscheidungsstand](../../2026-10-04-analysis-1.2-consolidated-decisions.md).

## Reale kurze Full-Teiljobs und Plugin-Komposition

`audio-full-v2.json` und `video-full-v2.json` sind hier neu erzeugte reale Full-v2-Teiljobs
einer synthetischen 2-s-H264-/FLAC-Datei. Sie ergänzen die gelieferten Balancedbeispiele.
Erzeugungsbefehl, konkrete Workerrevision und alle Quell-/Artefakthashes stehen in
`composition-manifest.json`. Der Snapshot verwendet ausdrücklich eine fiktive Offlineidentität,
keinen serverseitigen Jellyfinfingerprint. Nicht produktiv hochladen.

Workeraufruf aus `Aether/packages/server-analysis-worker`, getrennt für beide Modi:

```sh
node --import tsx src/draft-storage-cli.ts \
  --input /tmp/neutral.mkv --out /tmp/audio-full-v2.json \
  --mode audio-only --format artifact --detail full \
  --snapshot /tmp/snapshot.json \
  --producer-revision sha256:8ddc99b791acc18b70aba15237c909024a69364637f2b776fb9a48f96a758e3b \
  --stream-index 1 --width 480 --fps 2 \
  --max-bytes 52428800 --max-wall-ms 120000 --max-rss-bytes 536870912
```

Für Video `--mode video-only` und einen eigenen Ausgabepfad verwenden. Das Snapshotobjekt
steht in den gemessenen Komponenten. Andere FFmpegversionen können andere Messbytes erzeugen.
Die unabhängigen `composition-wire-expected.json`-Records binden die eingecheckten Teiljobs.
Python liest `<IiHB5d`-Records, gruppiert nach `timestampMs // intervalMs` und ersetzt eine
Auswahl nur bei strikt größerem RMS. Kein Mittelwert und keine nachträgliche F64neuberechnung.

`composed-full.json`, `composed-balanced.json` und `composed-compact.json` stammen vom C#-Builder.
Die Testfälle ergänzen absichtlich kleine fremde Root-/Frame-/Packedheaderfelder. Eine isolierte
Neuerzeugung ist mit `AETHER_DRAFT_EXPORT_DIRECTORY=<privates Zielverzeichnis>` beim gefilterten
`DraftAnalysisCompositionTests`-Lauf möglich. Das Testexportflag ist kein Pluginruntimepfad.
Erwartete Anzahl: 100/40/20. Maxgap: 20/80/180 ms, tatsächliche ganze Records bleiben unverändert.

`desktop-interop-result.json` dokumentiert den historischen ersten Import mit damaliger
Compact-Cutdiagnose. Die Clientreader sind inzwischen erweitert. Aktuell prüfen
`desktop-legacy-fix-interop-result.json` und `tv-legacy-fix-interop-result.json` alle drei
Details diagnosefrei, inklusive vollständiger Legacy-Audioreihe und ursprünglicher Cutcoverage.
Compact hat jetzt 12.560 Bytes. Seine frühere Fassung und das damalige Manifest liegen in
`before-legacy-audio-fix`. Full, Balanced und alle F64-Wirebytes bleiben unverändert.

`consumer-profiles/manifest.json` bindet 18 unveränderte kleine Audio-/Zeitprofile des Workers.
Es wurden keine Medien übernommen. Testhostidentität wird nur im Speicher eingesetzt.
Ein belegter vollständiger no-track-Zustand erhält keine erfundenen Audiogruppen.
Aktueller Prüfstand und Folgeprompts stehen im
[Legacy-Korrekturreview](../../2026-10-04-analysis-1.2-legacy-audio-fix-results.md).

Die beiden `.schema.json`-Dateien sind beschreibende Offline-Strukturschemas. Die C#-Komposition
nimmt ein engeres, vollständig belegtes Fullprofil an. Strukturprüfung ist weder DSPnachweis
noch die Freigabe neuer Zustände, Routinemigrationen oder Lesematrixrichtungen.
