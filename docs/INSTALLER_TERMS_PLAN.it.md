# Proposta per i termini dei componenti nell’installer

L’utente ha approvato esplicitamente questa proposta il 26/10/2026. L’implementazione è in verifica.
Il codice MIT, il nome, l’icona, le schermate, le previsioni e i pesi restano soggetti ai rispettivi
termini esistenti. Le condizioni sotto riguardano i componenti identificati, non aggiungono divieti
al codice MIT di Battery Forecast.

## Fatti verificati

- x64: `libiomp5md.dll` e `libiompstubs5md.dll` sono identiche al wheel ufficiale Intel OpenMP2026.1.1.
  EULA Intel August2024 e third-party-programs originali sono in `licenses/vendor/Intel-OpenMP-*`.
- ARM64: `armpl_lp64.dll` contiene la versione24.10.0 ed è identica alla DLL del pacchetto ufficiale
  ArmPL24.10. Il pacchetto è stato aperto come database in sola lettura e i documenti estratti dal CAB,
  senza installazione. Licenza esatta: Simplified End User License Agreement for Free of Charge Arm
  Redistributables, in `licenses/vendor/ArmPL-24.10-LICENSE.txt`. Non la EULA generica di una versione diversa.
- Intel §2.1.D(2) e §7.3: condizioni sugli eseguibili e sui limiti di responsabilità da rendere vincolanti
  per i destinatari. Arm §2.1(a): accettazione/obblighi contrattuali per i destinatari.
- Python-LICENSE contiene anche le Additional Conditions relative al Microsoft Distributable Code.
- Allegare soltanto i file delle licenze non dimostra da solo l’accettazione richiesta.

## Modifica proposta

1. Aggiungere nei nuovi installer una pagina standard Inno Setup di lettura/accettazione dei termini
   dei componenti redistribuiti, con introduzione italiana/inglese e testi originali completi pertinenti
   all’architettura. Evidenziare la distinzione fra MIT del codice e licenze dei componenti.
2. Conservare tutti gli originali completi offline, con indice per DLL e hash.
3. Per un aggiornamento che introduce questi termini, mostrare l’installer interattivo; il vecchio
   comando `/SILENT` salta la pagina e non deve essere considerato una prova di consenso.
4. La CI può verificare il contenuto e il flusso tecnicamente, ma non attestare la validità giuridica
   dell’accettazione. Il distributore deve valutare gli obblighi applicabili; una revisione legale del
   testo destinato ai destinatari resta opportuna prima di dichiarare conformità completa.

## Decisione approvata e limiti della verifica

L’utente ha autorizzato questa modifica circoscritta all’installer il 26/10/2026. È implementata
con LicenseFile IT/EN, testi generati dalle copie originali verificate e aggiornamenti interattivi.
L’updater alpha.2 che passa `/SILENT /RELAUNCH` viene reindirizzato al wizard interattivo.
Un’installazione silenziosa senza tale provenienza richiede esplicitamente `/ACCEPTCOMPONENTTERMS`; la CI verifica sia il
rifiuto senza tale parametro sia installazione, reinstallazione e licenze con il parametro.
Nessun permesso aggiuntivo dagli autori originali viene richiesto per il codice MIT.

L’accettazione non certifica l’idoneità del distributore né la validità giuridica di ogni uso.
La release alpha.2 esistente non viene modificata retroattivamente; è stata promossa stabile/latest
su richiesta esplicita dell’utente, senza sostituire gli asset.
