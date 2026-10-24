<p align="center"><img src="app/icon-1024.png" width="160" alt="Till Dusk icon"></p>

# Till Dusk

A menu-bar battery forecast for macOS. Instead of "how long if I keep going like this", Till Dusk tells you **where your charge will be tonight, based on how you actually use your Mac**, using Mistral's [Timestral](https://github.com/mistralai/timestral) time-series model running locally.

*Italiano più sotto.*

## Download and install (English)

- **Requirements:** macOS 26 or later on an Apple silicon Mac (M1 or later). The download is a few hundred MB because it bundles Python, PyTorch and the model, so the app works offline.
- **Install:** download `TillDusk-1.0.zip` from [Releases](https://github.com/mattBonfanti/till-dusk/releases), open it and drag `Till Dusk.app` to Applications.
- **First launch:** the app is not notarized by Apple, so macOS says it can't verify it. Close the alert, open System Settings → Privacy & Security, scroll to the message about Till Dusk, click **Open Anyway** and confirm. After that it opens normally.
- **What you get:** a menu-bar item showing time to empty, the expected charge at 22:00, or the percentage; a panel with a 24-hour forecast chart, a cautious/optimistic band and macOS's own estimate for comparison; a Low Power Mode suggestion when the forecast runs out before evening.
- **Privacy:** battery history is read from copies of macOS's power log and stays on your Mac. The forecast runs locally with the bundled model; the app sends nothing over the network.
- **Low Power Mode:** if you turn it on from the app, the password is asked by the macOS system dialog; the app never sees it.
- **Licenses:** Till Dusk's code is MIT. Timestral's code is Apache 2.0; Mistral AI's Timestral-28M weights are under a modified MIT license (free use for companies under $20M monthly revenue). The texts are in `Till Dusk.app/Contents/Resources/licenses`.

## Scaricare e installare (Italiano)

- **Requisiti:** macOS 26 o successivo, Mac con Apple silicon (M1 o successivi). Il download pesa qualche centinaio di MB perché dentro ci sono Python, PyTorch e il modello: l'app funziona senza internet.
- **Installazione:** scarica `TillDusk-1.0.zip` da [Releases](https://github.com/mattBonfanti/till-dusk/releases), aprilo e trascina `Till Dusk.app` in Applicazioni.
- **Prima apertura:** l'app non è notarizzata da Apple, quindi al primo avvio macOS dice che non può verificarla. Chiudi l'avviso, apri Impostazioni di Sistema → Privacy e sicurezza, scorri fino al messaggio su Till Dusk e premi **Apri comunque**, poi conferma. Dalla volta dopo si apre normalmente.
- **Cosa fa:** nella barra dei menu mostra il tempo alla scarica, la carica prevista alle 22 o la percentuale; il pannello ha il grafico delle prossime 24 ore, una fascia prudente e ottimista e la stima di macOS per confronto; propone il risparmio energetico quando la previsione finisce prima di sera.
- **Privacy:** la storia della batteria si legge da copie del registro energetico di macOS e resta sul Mac. La previsione si calcola in locale con il modello incluso e l'app non manda dati in rete.
- **Risparmio energetico:** se lo attivi dall'app, la password la chiede la finestra di sistema di macOS e l'app non la vede.
- **Licenze:** il codice di Till Dusk è MIT. Il codice di Timestral è Apache 2.0; i pesi del modello Timestral-28M di Mistral AI sono sotto licenza MIT modificata (uso libero per aziende sotto i 20 milioni di dollari di fatturato al mese). I testi sono in `Till Dusk.app/Contents/Resources/licenses`.

## Build from source

Needs macOS 26, the Xcode Command Line Tools and [uv](https://docs.astral.sh/uv/).

```bash
app/build.sh      # Python env in ~/Library/Application Support/Autonomia, compiles and installs ~/Applications/Till Dusk.app
app/release.sh    # self-contained app (Python 3.12 + deps + Timestral-28M), ad-hoc signed → app/dist/TillDusk-1.0.zip
```

`release.sh` copies the model from the Hugging Face cache, so download it once first (e.g. `uv run python -c "from huggingface_hub import snapshot_download; snapshot_download('mistralai/Timestral-28M')"`). Name, bundle id and version live in `app/app.conf`; the icon is `app/AppIcon.icns` (source `app/icon.svg`). The forecast service is `scripts/battery_now.py`. Internally the executable and data folders are still called "Autonomia".
