<p align="center">
  <img src="images/icon-256.png" width="128" alt="Icona di Battery Forecast">
</p>

<h1 align="center">Battery Forecast</h1>

<p align="center">
  <b>Quanto durerà davvero oggi la batteria del tuo portatile Windows?</b><br>
  Una previsione con l'intelligenza artificiale che impara come usi il PC, e gira tutta sul PC.<br>
  <sub>Con <a href="https://github.com/mistralai/timestral">Timestral</a> · fork per Windows di <a href="https://github.com/mattBonfanti/till-dusk">Till Dusk</a></sub>
</p>

<p align="center">
  <a href="https://mattboxx.github.io/battery-forecast/"><b>Sito</b></a> ·
  <a href="https://github.com/Mattboxx/battery-forecast/releases/latest"><b>Download</b></a> ·
  <a href="../README.md">English</a> ·
  <a href="https://ko-fi.com/mattboxx">♥ Supporta su Ko-fi</a>
</p>

<p align="center">
  <img src="images/it-panel-dark.png" width="300" alt="Pannello di Battery Forecast, tema Crepuscolo">
  &nbsp;&nbsp;
  <img src="images/it-panel-light.png" width="300" alt="Pannello di Battery Forecast, tema Alba">
</p>
<p align="center"><sub>Modalità demo: nelle immagini la scarica è simulata a partire da un livello reale della batteria.</sub></p>

> **Anteprima.** Battery Forecast funziona ed è in prova su portatili reali. La sua accuratezza non è ancora stata
> misurata, quindi non ne dichiara: mostra una fascia e dice da dove viene ogni numero.

## Cosa fa

- **Fino a quando:** l'ora in cui la batteria arriva alla riserva (5% di default), con una fascia indicativa
  dalla stima prudente a quella ottimista. Orari stimati arrotondati a 5 minuti, mai un numero finto preciso.
- **La tua sera:** il livello previsto alle 22:00 o all'ora che scegli, e il suggerimento del risparmio
  energetico quando non basterebbe.
- **Grafico delle 24 ore** con stima centrale, fascia, linea della riserva e obiettivo serale.
- **Abitudini e presente insieme:** accanto alla previsione sulle abitudini ci sono sempre la stima al ritmo
  attuale e quella di Windows.
- **Avvisi discreti:** a circa 60, 30 e 15 minuti dalla riserva, a batteria bassa, a carica raggiunta e con un
  consumo insolito. Un avviso per soglia in ogni scarica, mai con l'alimentatore.
- **Storico e salute:** le scariche osservate e la capacità a piena carica rispetto a quella di progetto.
- **Vicino all'orologio:** icona con il livello esatto, quattro temi (Alba, Nebbia, Crepuscolo, Notte) o quello
  di Windows, italiano e inglese, tastiera e lettori di schermo.
- **Aggiornamenti dall'app:** un clic controlla GitHub, scarica l'installer e ne verifica lo SHA-256.

<p align="center">
  <img src="images/it-history-dark.png" width="420" alt="Finestra Storico e salute della batteria">
</p>

## Come funziona

1. Mentre è aperta, l'app registra ogni minuto livello e alimentazione, più sospensioni, riprese e alimentatore.
   Le lacune restano lacune: per il tempo a PC spento non si inventa nulla.
2. [Timestral](https://github.com/mistralai/timestral), un modello di Mistral AI per le serie temporali, legge
   fino a 110 giorni di storico su una griglia di 5 minuti e prevede le prossime 24 ore.
3. Lo scenario è sempre lo stesso: *uso abituale, senza collegare l'alimentatore da adesso*. La curva non può
   risalire né superare il livello attuale.
4. Quando di solito colleghi l'alimentatore, il modello non può sapere quanto consumeresti a batteria: il
   pannello lo dice e il grafico prosegue, tratteggiato, al ritmo medio previsto.
5. Finché lo storico non basta, si mostra la stima al ritmo attuale, e il pannello lo dice.

Dettagli e limiti nella [model card](MODEL_CARD.md) (in inglese).

## Leggera sulla batteria

Il modello non resta in esecuzione: si avvia, calcola e si chiude. In background a batteria parte al massimo
ogni 30 minuti (oppure solo quando apri il pannello, o ogni 10 minuti), mai con il risparmio energetico attivo.
Una previsione costa circa 3 secondi di CPU sul portatile di sviluppo; fra una previsione e l'altra non resta
nessun processo del motore.

## Privacy

Nessun account, nessuna telemetria: lo storico resta in `%LOCALAPPDATA%\BatteryForecast` e si può esportare o
cancellare dalle Preferenze. Le previsioni funzionano senza rete. L'unica richiesta di rete è il controllo
giornaliero facoltativo degli aggiornamenti su `api.github.com`.

## Installazione

1. Scarica `BatteryForecast-Setup-<versione>-x64.exe` (Intel/AMD) oppure `-arm64.exe` (PC ARM) dalle
   [Release](https://github.com/Mattboxx/battery-forecast/releases/latest).
2. Facoltativo: confronta il file con il suo `.sha256` usando `Get-FileHash .\BatteryForecast-Setup-*.exe`.
3. Avvialo: si installa solo per il tuo utente, senza privilegi di amministratore. L'anteprima **non è firmata**,
   quindi SmartScreen può avvisarti: scegli *Ulteriori informazioni → Esegui comunque*.
4. L'icona della batteria compare vicino all'orologio; se Windows la nasconde, trascinala fuori dall'area `^`.

Requisiti: Windows 10 2004 o Windows 11, x64 o ARM64, circa 280 MB da scaricare.

## Crediti e licenze

- Battery Forecast è un fork di **[Till Dusk](https://github.com/mattBonfanti/till-dusk) di Matteo Bonfanti**
  (licenza MIT), l'app originale per la barra dei menu di macOS.
- **[Timestral](https://github.com/mistralai/timestral)** di Mistral AI: codice Apache 2.0, pesi Timestral-28M
  sotto licenza MIT modificata (testo esatto in [`licenses/`](../licenses), con una condizione per le aziende
  sopra una soglia di fatturato mensile). Battery Forecast non è affiliato a Mistral AI.
- Il codice di Battery Forecast è MIT, vedi [LICENSE](../LICENSE).

## Sostegno

Se Battery Forecast ti è utile puoi sostenerlo su **[Ko-fi](https://ko-fi.com/mattboxx)**. Segnalazioni e idee
nelle [Issues](https://github.com/Mattboxx/battery-forecast/issues).
