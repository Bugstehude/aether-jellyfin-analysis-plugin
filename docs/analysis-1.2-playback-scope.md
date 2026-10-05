# Analyse 1.2: aktueller Wiedergabeumfang

Stand 05.10.2026. Marc hat den tatsächlichen Einsatz konkretisiert:
**Direct Play mit genau einer Tonspur.** Dieser Umfang gilt für die nächste
gemeinsame Clientintegration. Mehrspurwahl, Transcoding und ein nativer Desktopplayer
sind keine Voraussetzungen für diese Umstellung.

Der [Feld-/Wirevertrag](analysis-1.2-contract.md) und gespeicherte Messidentitäten bleiben
unverändert. Diese Vorgabe betrifft ausschließlich die lokale Playbackqualifikation.
Sie aktiviert weder eine Produktionsversion noch eine Capabilitymatrix oder Clientgate.

## Unterstützter Fall

Die Anwendung vertraut der bestehenden Jellyfin-/Dateiquelle innerhalb ihrer üblichen
Wiedergabegrenze. Folgende Bedingungen qualifizieren den unterstützten Fall:

1. Die Analyse gehört zur konkret angefragten Datei beziehungsweise ausgewählten
   Medienquelle. Bei Jellyfin bleiben Server, Benutzer, Item und MediaSourceId
   im selben unveränderlichen Lade-/Sessionkontext.
2. Ein aktueller Quellenfingerprint passt zur Analyse. Dokumentfingerprint oder
   alter Cachestatus allein ersetzen den aktuellen Quellenabgleich nicht.
3. Der bestehende Wiedergabeweg liefert Direct Play der Originalquelle mit ihrer
   unveränderten Medienzeit. Eine Transcoding-/HLS-Ausgabe wird nicht als dieser Fall behandelt.
4. Die aktuelle Quelle hat genau einen Audiostream. Die analysierte Referenz gehört
   zu dieser Quelle und diesem einzigen Stream. Stereo- oder Mehrkanalaudio ist
   weiterhin eine Tonspur, nicht mehrere Streams.
5. Die optionalen Messgruppen, Methoden und Coverage sind wie bisher gültig.

Unter diesen Bedingungen ist kein zusätzlicher Nachweis der gehörten Spur über
AVPlayer, Lautsprecheroutput oder einen nativen Native↔Host-Crosswalk erforderlich.
Es gibt in der belegten Originalquelle keine zweite Audiospur, auf die der Player
abweichend umschalten könnte. Native Track-IDs und globale FFmpeg-/Jellyfin-Indizes
werden trotzdem nicht numerisch gleichgesetzt. Der Zusammenhang folgt aus der
gleichen Quelle und ihrer eindeutigen Audioreferenz.

Die einzelnen Request-/Playerkontexte und aktuellen Quellenmetadaten müssen zusammenpassen.
Eine URL allein oder ein Defaultflag allein beweist weder Quellenaktualität noch
Audiostreamanzahl. Zugleich wird nicht für jeden normalen Directplayabruf ein zusätzlicher
nativer Decoder- oder physischer Hörnachweis verlangt. Redirect-/Responseprobleme bleiben
Teil der regulären Liveprüfung des gewählten Jellyfinwegs.

Für neue Cuts genügt die passende Directplayquelle und originale Medienzeit mit gültiger
Cutgruppe. Die Anzahl der Audiostreams qualifiziert die Audiofunktionen, nicht Bildereignisse.
Bei belegter Spurlosigkeit entstehen keine Audiosignale. Bei mehreren oder unbekannten
Audiostreams bleiben neue referenzspurgebundene Audiofunktionen im Fallback.
Bei unpassender Quelle/Fingerprint/Zeitbasis bleiben neue quellgebundene Gruppen gesperrt.

## Erhaltener Schutz und ausgeschlossene Arbeit

Quellen-/Session-/Dokumentwechsel, Abbruch und verspätete Callbacks behalten ihre Guards.
Medienzeit, Seek, Pause, Bypass, Ereignisepochen und Coverage bleiben unverändert.
Das 32-MiB-Dokumentlimit und vorhandene Cache-/Deadlinebudgets bleiben bestehen.
Legacyfallback darf bei fehlenden Zusatzgruppen weiter genutzt werden.

Keine neue native Desktoparchitektur, Mehrspurwahl, automatische Spurumschaltung,
Transcodingzeittransformation oder physische Lautsprecherqualifikation in diesem Auftrag.
Die bisherigen synthetischen Mehrspur- und Nativeadaptertests bleiben historische
Schutzbelege, sind aber keine offenen Voraussetzungen des Einspur-Directplayumfangs.

Die vorherige allgemeine Anforderung eines positiven nativen Quellen-/Gehörtspurbelegs
für jede neue Desktopgruppe wird für diesen unterstützten Umfang ersetzt. Beide Clients
müssen diese Qualifikation noch implementieren und prüfen. Gates bleiben aus, bis die
gemeinsame Versions-/Live-/Ressourcenabnahme erfolgt.
